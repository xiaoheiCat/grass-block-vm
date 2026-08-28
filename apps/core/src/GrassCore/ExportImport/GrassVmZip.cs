using System.IO.Compression;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore.ExportImport;

/// <summary>
/// 导出（§15.1 冻结决策）：
/// - 任何导出前必须关机（运行中/挂起都不允许）。
/// - .grassvm.zip = 完整档案：包体内配置、磁盘、快照树、固件状态、artwork；
///   不包含 state.json 等本机使用痕迹，也不复制包外资源（保留绝对路径，跨机时走统一重定位）。
/// - OVA/OVF = 兼容交换格式：只导出当前有效状态，不承诺保留快照树/全部元数据。
/// </summary>
public static class GrassVmZip
{
    /// <summary>导出时排除的本机痕迹与瞬态（未知文件不属于这几类的保留）。</summary>
    private static readonly string[] ExcludedRelativePaths =
    {
        GrassVmPackage.StateFile,   // state.json：本机使用痕迹
        GrassVmPackage.LockFile,    // vm.lock
        GrassVmPackage.RuntimeDir + "/", // runtime/（session 等瞬态）
        GrassVmPackage.LogsDir + "/",    // logs/（本机日志）
    };

    private static bool IsExcluded(string relativeEntryName)
    {
        var norm = relativeEntryName.Replace('\\', '/');
        return ExcludedRelativePaths.Any(x =>
            x.EndsWith('/') ? norm.StartsWith(x, StringComparison.Ordinal) : norm == x)
            || norm.EndsWith(TransactionalDiskOps.TempSuffix, StringComparison.Ordinal);
    }

    /// <summary>校验 VM 处于可导出状态（必须已关机且未被占用）。</summary>
    public static void EnsureExportable(GrassVmPackage package, bool isRunning)
    {
        if (isRunning || File.Exists(package.LockPath))
            throw new GrassCoreException("导出前必须先正常关机。运行中或挂起的虚拟机不允许导出。");
        // 挂起的 VM 没有 vm.lock 也不在运行，但挂起状态绑定宿主指纹，导出的档案无法在别处恢复
        if (Config.VmState.Load(package).SuspendedStatePath is not null)
            throw new GrassCoreException("此虚拟机已挂起。请先恢复并正常关机后再导出。");
        // 链接克隆的磁盘 backing 指向父 VM 包：档案离开这台机器的库就断链，导出没有意义
        if (new Config.ConfigStore(package).Load().CloneInfo is not null)
            throw new GrassCoreException("链接克隆的磁盘依赖父虚拟机，无法导出为独立档案。请先转换为完整克隆。");
    }

    /// <summary>导出 .grassvm.zip（完整档案）。条目以 &lt;包名&gt;.grassvm/ 为前缀，导入时保持同名。</summary>
    public static void Export(GrassVmPackage package, string zipPath)
    {
        // 写临时文件后原子改名：导出失败不破坏调用方已有的旧档案
        var tempZip = zipPath + ".grass-tmp";
        try
        {
            var prefix = Path.GetFileName(package.Path) + "/";
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(package.Path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(package.Path, file).Replace('\\', '/');
                    if (IsExcluded(rel)) continue;
                    // 包内相对路径存储（导入后在任意位置解开都保持自包含）
                    zip.CreateEntryFromFile(file, prefix + rel, CompressionLevel.Optimal);
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tempZip, zipPath);
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 导入 .grassvm.zip：解到 Library Root 下；同位置已存在同名包则失败（不合并、不覆盖）。
    /// 导入后 VM 保持完整快照树；包外资源引用按统一重定位流程处理。
    /// </summary>
    public static GrassVmPackage Import(string zipPath, string libraryRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count == 0)
            throw new GrassCoreException("这是一个空的压缩包，不是有效的虚拟机档案。");
        // 包名取第一个一级目录（导出时以包根内容 + 包名目录形式写入）
        var first = zip.Entries[0].FullName.Split('/')[0];
        var pkgName = first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? first[..^GrassVmPackage.Extension.Length]
            : Path.GetFileNameWithoutExtension(zipPath);
        var target = Path.Combine(libraryRoot, pkgName + GrassVmPackage.Extension);
        if (Directory.Exists(target) || File.Exists(target))
            throw new GrassCoreException($"目标位置已存在同名虚拟机：{pkgName}。");
        // 先解压到临时名，全部成功后改名成包：中途失败（zip-slip/IO 错误）不留半成品
        var staging = Path.Combine(libraryRoot,
            $".importing-{Guid.NewGuid().ToString("N")[..8]}-{pkgName}{GrassVmPackage.Extension}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var entry in zip.Entries)
            {
                var rel = first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase)
                    ? entry.FullName[(first.Length + 1)..]
                    : entry.FullName;
                if (string.IsNullOrEmpty(rel)) continue;
                var dest = Path.GetFullPath(Path.Combine(staging, rel));
                if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new GrassCoreException("压缩包包含非法路径（zip slip），已拒绝导入。");
                if (IsExcluded(rel)) continue; // 本机痕迹即使被人塞进包里也不导入
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: false);
            }
            var pkg0 = new GrassVmPackage(staging);
            // 档案刻意不含 runtime/logs 等瞬态目录；导入时补齐固定结构（确定无损修复）
            pkg0.EnsureStructure();
            Directory.Move(staging, target);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
        return new GrassVmPackage(target);
    }
}
