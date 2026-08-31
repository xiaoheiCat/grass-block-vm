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
        "suspend.state",            // 挂起的 RAM 镜像：宿主指纹绑定+RAM 大小，属于本机痕迹
        GrassVmPackage.RuntimeDir + "/", // runtime/（session 等瞬态）
        GrassVmPackage.LogsDir + "/",    // logs/（本机日志）
        GrassVmPackage.TempDir + "/",   // temp/（升级备份等本机产物——档案不该带着旧配置旅行）
    };

    private static bool IsExcluded(string relativeEntryName)
    {
        var norm = relativeEntryName.Replace('\\', '/');
        return ExcludedRelativePaths.Any(x =>
            x.EndsWith('/') ? norm.StartsWith(x, StringComparison.OrdinalIgnoreCase)
                : string.Equals(norm, x, StringComparison.OrdinalIgnoreCase))
            || norm.EndsWith(TransactionalDiskOps.TempSuffix, StringComparison.OrdinalIgnoreCase);
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
        // 未收尾的快照/恢复事务：档案会把"半恢复"现场带走——导入侧没有 state.json
        // （PendingRestoreTxId 丢失），修复会把残局误判为"已提交"，prev（恢复前数据
        // 的唯一副本）被当垃圾删掉。先启动一次（入口会跑修复收尾）再导出
        if (File.Exists(Path.Combine(package.SnapshotsPath, "restore-journal.json")))
            throw new GrassCoreException("此虚拟机有一个未完成的恢复操作。请先启动一次让其自动收尾，再导出。");
        var enumOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        if (Directory.EnumerateFiles(package.Path, "*" + Rpc.SnapshotService.RestorePrevSuffix, enumOpts).Any()
            || Directory.EnumerateFiles(package.Path, "*" + Rpc.SnapshotService.StagedOverlaySuffix, enumOpts).Any())
            throw new GrassCoreException("此虚拟机存在未收尾的快照/恢复残留。请先启动一次让其自动修复，再导出。");
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
                foreach (var dir in Directory.EnumerateDirectories(package.Path, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                        throw new GrassCoreException($"无法导出包含链接目录的虚拟机包：{dir}。");
                foreach (var file in Directory.EnumerateFiles(package.Path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(package.Path, file).Replace('\\', '/');
                    if (IsExcluded(rel)) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new GrassCoreException($"无法导出符号链接或目录联接：{rel}。");
                    // 包内相对路径存储（导入后在任意位置解开都保持自包含）
                    zip.CreateEntryFromFile(file, prefix + rel, CompressionLevel.Optimal);
                }
                // 快照工作位置随档案走（state.json 属本机痕迹被排除，但没有它导入方会把
                // 新快照挂到"最新叶"——与导出时的物理链位置不符，树会说谎）
                var position = Config.VmState.Load(package).CurrentSnapshotUuid;
                if (position is not null)
                {
                    var entry = zip.CreateEntry(prefix + "snapshots/position.marker");
                    using var w = new StreamWriter(entry.Open());
                    w.Write(position);
                }
            }
            // 原子替换：旧档案先改名保底，新档案落位成功后才删旧——中间任何一步失败，
            // 用户手里至少留有一份完整档案
            // 每次使用唯一备份名：上一轮备份因杀毒/权限未能删除时，不得永久
            // 阻塞以后所有导出。
            var backupZip = zipPath + ".grass-old-" + Guid.NewGuid().ToString("N");
            var hadOld = File.Exists(zipPath);
            if (hadOld) File.Move(zipPath, backupZip);
            try
            {
                File.Move(tempZip, zipPath);
            }
            catch
            {
                if (hadOld) File.Move(backupZip, zipPath, overwrite: true);
                throw;
            }
            if (hadOld) try { File.Delete(backupZip); } catch { /* 清理失败无害 */ }
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
        // 损坏/截断/贴错扩展名的 zip 在 OpenRead 就抛 InvalidDataException/
        // IOException——英文原文不进错误横幅，与 OVF 路径同标准
        System.IO.Compression.ZipArchive Open()
        {
            try { return ZipFile.OpenRead(zipPath); }
            catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException
                 or System.IO.FileNotFoundException or NotSupportedException)
            {
                throw new GrassCoreException("无法读取这份压缩包（已损坏或不是有效的 zip 档案）。");
            }
        }
        using var zip = Open();
        if (zip.Entries.Count == 0)
            throw new GrassCoreException("这是一个空的压缩包，不是有效的虚拟机档案。");
        // 包名取第一个一级目录（导出时以包根内容 + 包名目录形式写入）
        var first = zip.Entries[0].FullName.Split('/')[0];
        var pkgName = first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? first[..^GrassVmPackage.Extension.Length]
            : Path.GetFileNameWithoutExtension(zipPath);
        if (string.IsNullOrWhiteSpace(pkgName)
            || pkgName is "." or ".."
            || Path.GetFileName(pkgName) != pkgName
            || pkgName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new GrassCoreException("压缩包中的虚拟机名称不合法。");
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
                // 前缀剥离只对真带前缀的条目做：畸形档案（首条目是名为 *.grassvm 的
                // 文件而非目录、或后续条目比前缀还短）会让范围切片抛裸
                // ArgumentOutOfRangeException——翻译成明确的"不是有效档案"
                string rel;
                if (first.EndsWith(GrassVmPackage.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (!entry.FullName.StartsWith(first + "/", StringComparison.Ordinal))
                        throw new GrassCoreException("压缩包的目录结构不是有效的虚拟机档案。");
                    rel = entry.FullName[(first.Length + 1)..];
                }
                else
                {
                    rel = entry.FullName;
                }
                if (string.IsNullOrEmpty(rel)) continue;
                // 显式目录条目（Finder / 多数 Windows 压缩工具会写）：无文件体，
                // ExtractToFile 会按"把目录当文件打开"直接抛异常。目录由下方
                // CreateDirectory 按需创建，这里跳过即可
                if (rel.EndsWith('/') || string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(staging, rel));
                if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new GrassCoreException("压缩包包含非法路径（zip slip），已拒绝导入。");
                if (IsExcluded(rel)) continue; // 本机痕迹即使被人塞进包里也不导入
                // 手工打包的"崩溃/半恢复现场"档案：restore-journal / prev 副本 / 暂存
                // overlay 进来后（档案不含 state.json，PendingRestoreTxId 必然丢失）
                // 修复会把残局误判为"已提交"，prev（恢复前数据的唯一副本）被当垃圾
                // 删掉。宁可拒收：让用户先在原机器上启动一次收尾再打包
                if (rel == "snapshots/restore-journal.json"
                    || rel.EndsWith(Rpc.SnapshotService.RestorePrevSuffix, StringComparison.Ordinal)
                    || rel.EndsWith(Rpc.SnapshotService.StagedOverlaySuffix, StringComparison.Ordinal))
                    throw new GrassCoreException(
                        "这个档案包含未完成的恢复/快照事务残迹（restore-journal 或暂存层）。" +
                        "请在原来的电脑上启动一次该虚拟机让其自动收尾，再重新导出/打包。");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: false);
            }
            var pkg0 = new GrassVmPackage(staging);
            // 档案刻意不含 runtime/logs 等瞬态目录；导入时补齐固定结构（确定无损修复）
            pkg0.EnsureStructure();
            // 名称规则与 CreateVm/UpdateConfig/克隆/OVF 同一套：名称同时是 QEMU
            // -name 的值（等号=未知键启动即退、逗号=QemuOpts 分隔符）与目录名。
            // 手工打包的档案可以塞进任意 config.json——不校验的话导入出一台
            // 永远开不了机的 VM（QEMU 拒参数即退，真错误埋在 qemu.log 里）
            string importedName;
            try
            {
                importedName = new Config.ConfigStore(pkg0).Load().Name;
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                throw new GrassCoreException("压缩包内的虚拟机配置（config.json）已损坏，无法导入。");
            }
            if (Rpc.GrassCoreService.IsInvalidVmName(pkgName) || Rpc.GrassCoreService.IsInvalidVmName(importedName))
                throw new GrassCoreException(
                    $"虚拟机名称不合法（{importedName}）：不能为空，不能包含文件系统不允许的字符、逗号或等号。");
            var importedConfig = new Config.ConfigStore(pkg0).Load();
            var importedProfile = GrassCore.Profiles.OsProfileLibrary.ById(importedConfig.OsProfileId);
            if (importedConfig.Firmware.Kind != importedProfile.Firmware)
                throw new GrassCoreException("压缩包内的固件类型与 OS Profile 不一致，无法导入。");
            if (importedConfig.Firmware.Kind == Config.FirmwareKind.Uefi
                && !File.Exists(Path.Combine(pkg0.FirmwarePath, "VARS.fd")))
                throw new GrassCoreException("压缩包缺少 UEFI 变量文件 VARS.fd，无法导入。");
            foreach (var disk in Config.VmState.Load(pkg0).SuspendedStatePath is null
                ? new Config.ConfigStore(pkg0).Load().Devices.OfType<DiskDevice>()
                : Enumerable.Empty<DiskDevice>())
            {
                if (disk.IsExternal)
                    throw new GrassCoreException("档案包含包外硬盘引用，请在原电脑上移入包内后再导出。");
                _ = PathPolicy.Resolve(pkg0, disk.Path);
            }
            foreach (var cd in importedConfig.Devices.OfType<CdromDevice>())
                if (cd.IsoPath is not null && Path.IsPathRooted(cd.IsoPath))
                    throw new GrassCoreException("档案包含包外光盘引用，请在原电脑上移入包内后再导出。");
            foreach (var folder in importedConfig.Devices.OfType<SharedFolderDevice>())
                if (!string.IsNullOrWhiteSpace(folder.HostPath) && Path.IsPathRooted(folder.HostPath))
                    throw new GrassCoreException("档案包含包外共享文件夹引用，请在原电脑上移除后再导出。");
            // 恢复快照工作位置（导出方写入的树拓扑语义；marker 只在档案里存在，落盘后转为 state）
            var marker = Path.Combine(staging, "snapshots", "position.marker");
            if (File.Exists(marker))
            {
                var uuid = File.ReadAllText(marker).Trim();
                if (Rpc.SnapshotService.LoadTree(pkg0).All.Any(s => s.Uuid == uuid))
                {
                    var state = Config.VmState.Load(pkg0);
                    state.CurrentSnapshotUuid = uuid;
                    state.Save(pkg0);
                }
                File.Delete(marker);
            }
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
