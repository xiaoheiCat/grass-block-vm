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
    }

    /// <summary>导出 .grassvm.zip（完整档案）。条目以 &lt;包名&gt;.grassvm/ 为前缀，导入时保持同名。</summary>
    public static void Export(GrassVmPackage package, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        var prefix = Path.GetFileName(package.Path) + "/";
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(package.Path, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(package.Path, file).Replace('\\', '/');
            if (IsExcluded(rel)) continue;
            // 包内相对路径存储（导入后在任意位置解开都保持自包含）
            zip.CreateEntryFromFile(file, prefix + rel, CompressionLevel.Optimal);
        }
    }

    /// <summary>
    /// 导入 .grassvm.zip：解到 Library Root 下；同位置已存在同名包则失败（不合并、不覆盖）。
    /// 导入后 VM 保持完整快照树；包外资源引用按统一重定位流程处理。
    /// </summary>
    public static GrassVmPackage Import(string zipPath, string libraryRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // 包名取第一个一级目录（导出时以包根内容 + 包名目录形式写入）
        var first = zip.Entries[0].FullName.Split('/')[0];
        var pkgName = first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? first[..^GrassVmPackage.Extension.Length]
            : Path.GetFileNameWithoutExtension(zipPath);
        var target = Path.Combine(libraryRoot, pkgName + GrassVmPackage.Extension);
        if (Directory.Exists(target) || File.Exists(target))
            throw new GrassCoreException($"目标位置已存在同名虚拟机：{pkgName}。");
        Directory.CreateDirectory(target);
        foreach (var entry in zip.Entries)
        {
            var rel = first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase)
                ? entry.FullName[(first.Length + 1)..]
                : entry.FullName;
            if (string.IsNullOrEmpty(rel)) continue;
            var dest = Path.GetFullPath(Path.Combine(target, rel));
            if (!dest.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new GrassCoreException("压缩包包含非法路径（zip slip），已拒绝导入。");
            if (IsExcluded(rel)) continue; // 本机痕迹即使被人塞进包里也不导入
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: false);
        }
        var pkg = new GrassVmPackage(target);
        // 档案刻意不含 runtime/logs 等瞬态目录；导入时补齐固定结构（确定无损修复）
        pkg.EnsureStructure();
        return pkg;
    }
}
