using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore.Clone;

/// <summary>
/// 克隆服务：
/// - 完整克隆 = 独立完整副本：磁盘扁平化为独立 QCOW2（脱离父链/快照），不含快照历史，
///   不复制 state.json 等本机痕迹，设备 UUID 重新生成。
/// - 链接克隆 = 必须基于某个快照创建：磁盘为指向该快照 overlay 的 QCOW2 backing overlay，
///   config 来自快照的完整副本 + cloneInfo{父包（同目录时相对路径）, 父快照 UUID}。
///   删除被依赖快照会让链接克隆失效（删除前列出受影响列表并警告，但最终允许删除）。
/// </summary>
public sealed class CloneService(TransactionalDiskOps diskOps)
{
    /// <summary>完整克隆。命名冲突时抛异常（不覆盖既有 VM）。</summary>
    public async Task<GrassVmPackage> FullCloneAsync(GrassVmPackage source, string newName, CancellationToken ct = default)
    {
        var config = new ConfigStore(source).Load();
        var parentDir = Path.GetDirectoryName(source.Path)!;
        var target = GrassVmPackage.CreateNew(parentDir, newName);
        try
        {
            var newConfig = ConfigJson.Deserialize(ConfigJson.Serialize(config)); // 深拷贝
            newConfig.Name = newName;
            newConfig.CloneInfo = null; // 完整克隆是独立 VM，不再依赖父
            foreach (var d in newConfig.Devices) d.DeviceId = Guid.NewGuid().ToString();

            foreach (var disk in newConfig.Devices.OfType<DiskDevice>().ToList())
            {
                var srcPath = PathPolicy.Resolve(source, disk.Path);
                if (disk.IsExternal)
                {
                    // 包外磁盘保持原引用（克隆不复制包外数据盘）
                    continue;
                }
                var newDiskPath = Path.Combine(target.DisksPath, Path.GetFileName(disk.Path));
                // 扁平化为独立镜像（脱离一切 backing 链）
                await diskOps.ConvertToQcow2Async(srcPath, newDiskPath, ct);
                disk.Path = PathPolicy.NormalizeReference(target, newDiskPath);
            }
            // NVRAM 独立复制（UEFI 变量属于 VM 实例）
            var vars = Path.Combine(source.FirmwarePath, "VARS.fd");
            if (File.Exists(vars)) File.Copy(vars, Path.Combine(target.FirmwarePath, "VARS.fd"));
            // artwork 跟随（封面是用户资产）
            if (Directory.Exists(source.ArtworkPath))
                CopyDirectory(source.ArtworkPath, target.ArtworkPath);

            new ConfigStore(target).Save(newConfig);
            return target;
        }
        catch
        {
            if (Directory.Exists(target.Path)) Directory.Delete(target.Path, recursive: true);
            throw;
        }
    }

    /// <summary>链接克隆：基于指定快照。磁盘 overlay 的 backing 指向快照 overlay 文件。</summary>
    public GrassVmPackage LinkedClone(GrassVmPackage source, string snapshotUuid, string newName)
    {
        var tree = SnapshotService.LoadTree(source);
        var snap = tree.Get(snapshotUuid);
        var parentDir = Path.GetDirectoryName(source.Path)!;
        var target = GrassVmPackage.CreateNew(parentDir, newName);
        try
        {
            var config = ConfigJson.Deserialize(snap.FullConfigSnapshot);
            config.Name = newName;
            config.CloneInfo = new CloneInfo
            {
                // 同一目录树下的父包保存相对引用（../Name.grassvm），移动整个 Library 后仍然有效；
                // 不同位置则保存绝对路径，失效时按统一重定位流程处理。
                ParentVmPath = NormalizeSiblingReference(target, source),
                ParentSnapshotUuid = snapshotUuid,
            };

            foreach (var disk in config.Devices.OfType<DiskDevice>().ToList())
            {
                if (disk.IsExternal || !snap.DiskOverlayRefs.TryGetValue(disk.DeviceId, out var overlayRef)) continue;
                var backingFile = Path.Combine(source.SnapshotsPath, snapshotUuid, overlayRef.Replace('/', Path.DirectorySeparatorChar));
                var overlayPath = Path.Combine(target.DisksPath, Path.GetFileName(disk.Path));
                // QCOW2 overlay：backing 指向父快照的 overlay（外部链）
                diskOps.CreateOverlay(backingFile, overlayPath);
                disk.Path = PathPolicy.NormalizeReference(target, overlayPath);
            }
            var vars = Path.Combine(source.SnapshotsPath, snapshotUuid, "VARS.fd");
            if (File.Exists(vars)) File.Copy(vars, Path.Combine(target.FirmwarePath, "VARS.fd"));

            new ConfigStore(target).Save(config);
            return target;
        }
        catch
        {
            if (Directory.Exists(target.Path)) Directory.Delete(target.Path, recursive: true);
            throw;
        }
    }

    /// <summary>父包引用规范化：同父目录 → 相对（../Name.grassvm）；否则绝对。</summary>
    private static string NormalizeSiblingReference(GrassVmPackage from, GrassVmPackage to)
    {
        var fromDir = Path.GetDirectoryName(from.Path)!;
        var toDir = Path.GetDirectoryName(to.Path)!;
        if (string.Equals(fromDir, toDir, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(from.Path, to.Path).Replace('\\', '/');
        }
        return to.Path;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
