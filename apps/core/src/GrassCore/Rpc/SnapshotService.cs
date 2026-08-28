using System.Text.Json;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Snapshots;

namespace GrassCore.Rpc;

/// <summary>
/// 快照落盘布局（QCOW2 外部 overlay 链）：
/// snapshots/&lt;uuid&gt;/{ metadata.json, config.json, memory.state(如有), disks/disk-&lt;deviceId&gt;.qcow2 }
/// 每个快照保存完整 config.json 副本；包外磁盘不进入快照链（只恢复引用关系）。
/// 运行中快照包含内存状态；关机快照不含。
/// </summary>
public static class SnapshotService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Snapshot Create(GrassVmPackage package, VmConfiguration config, string name,
        string? description = null, bool isUpgradeProtection = false)
    {
        var snap = new Snapshot
        {
            Uuid = Guid.NewGuid().ToString(),
            ParentSnapshotUuid = null, // 由调用方按"当前工作位置"填写；这里取树上最新叶
            Name = name,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            HasMemoryState = false, // 运行中快照由 QEMU stop+migrate 路径填 true
            FullConfigSnapshot = ConfigJson.Serialize(config),
            IsUpgradeProtection = isUpgradeProtection,
            UpgradeProtectionCreatedAt = isUpgradeProtection ? DateTimeOffset.UtcNow : null,
        };
        var tree = LoadTree(package);
        var leaf = tree.All.Where(s => !tree.ChildrenOf(s.Uuid).Any())
            .OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        snap.ParentSnapshotUuid = leaf?.Uuid;

        // 每个"包内"磁盘设备生成 overlay 引用（包外磁盘不参与数据回滚）
        foreach (var disk in config.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
        {
            var overlayFile = $"disks/disk-{disk.DeviceId}.qcow2";
            snap.DiskOverlayRefs[disk.DeviceId] = overlayFile;
            // 实际 overlay 创建（qemu-img create -f qcow2 -b backing）由 GrassCore 调用 qemu-img 完成；
            // 这里登记引用，Windows 实机联调阶段与 QMP stop/commit 序列对齐。
        }
        WriteSnapshot(package, snap);
        return snap;
    }

    public static void WriteSnapshot(GrassVmPackage package, Snapshot snap)
    {
        var dir = System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(System.IO.Path.Combine(dir, "disks"));
        File.WriteAllText(System.IO.Path.Combine(dir, "metadata.json"), JsonSerializer.Serialize(snap, Opts));
        File.WriteAllText(System.IO.Path.Combine(dir, "config.json"), snap.FullConfigSnapshot);
    }

    public static SnapshotTree LoadTree(GrassVmPackage package)
    {
        if (!Directory.Exists(package.SnapshotsPath)) return new SnapshotTree(Array.Empty<Snapshot>());
        var snaps = new List<Snapshot>();
        foreach (var dir in Directory.EnumerateDirectories(package.SnapshotsPath))
        {
            var meta = System.IO.Path.Combine(dir, "metadata.json");
            if (!File.Exists(meta)) continue; // 未知目录保留但不解释
            try
            {
                snaps.Add(JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(meta), Opts)!);
            }
            catch (JsonException)
            {
                // 快照树元数据损坏：可能改数据的修复必须先征得用户确认（保守修复），这里跳过加载
            }
        }
        return new SnapshotTree(snaps);
    }

    public static object List(GrassVmPackage package)
    {
        var tree = LoadTree(package);
        return tree.All.Select(s => new
        {
            uuid = s.Uuid,
            parent = s.ParentSnapshotUuid,
            name = s.Name,
            description = s.Description,
            createdAt = s.CreatedAt,
            hasMemory = s.HasMemoryState,
            hidden = s.IsUpgradeProtection, // 升级保护快照对用户隐藏
        }).ToList();
    }

    /// <summary>
    /// 恢复：硬件配置 + 外部资源路径引用一起回滚；当前未快照工作状态丢弃（调用方已警告）。
    /// </summary>
    public static void Restore(GrassVmPackage package, string uuid)
    {
        var tree = LoadTree(package);
        var snap = tree.Get(uuid);
        var restored = ConfigJson.Deserialize(snap.FullConfigSnapshot);
        new ConfigStore(package).Save(restored);
        // 磁盘 overlay 切换：当前工作 overlay 重定向到快照 overlay（qemu-img rebase / 事务重写），
        // 在 Windows 实机联调阶段与 qemu-img blockcommit 序列对齐。
    }

    /// <summary>删除快照。无链接克隆依赖 → 重绑后代；有依赖 → 已由调用方列出受影响链接克隆并获用户确认。</summary>
    public static void Delete(GrassVmPackage package, string uuid)
    {
        var tree = LoadTree(package);
        var plan = SnapshotPlanner.PlanDelete(tree, uuid, FindLinkedCloneReferences(package));
        foreach (var (childUuid, newParent) in plan.Rebindings)
        {
            var child = tree.Get(childUuid);
            child.ParentSnapshotUuid = newParent == "__root__" ? null : newParent;
            WriteSnapshot(package, child);
        }
        var dir = System.IO.Path.Combine(package.SnapshotsPath, uuid);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>扫描 Library Root 找出以此包内快照为基线的链接克隆（子 VM 的 cloneInfo 引用）。</summary>
    public static List<LinkedCloneReference> FindLinkedCloneReferences(GrassVmPackage parentPackage)
    {
        var result = new List<LinkedCloneReference>();
        var parentDir = System.IO.Path.GetDirectoryName(parentPackage.Path)!;
        foreach (var pkg in GrassVmPackage.ScanLibraryRoot(parentDir))
        {
            if (pkg.Path == parentPackage.Path) continue;
            if (!File.Exists(pkg.ConfigPath)) continue;
            try
            {
                var config = new ConfigStore(pkg).Load();
                if (config.CloneInfo is { } ci &&
                    string.Equals(PathPolicy.Resolve(pkg, ci.ParentVmPath), parentPackage.Path, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new LinkedCloneReference(pkg.Name, pkg.Path, ci.ParentSnapshotUuid));
                }
            }
            catch (Exception)
            {
                // 无法读取的包不影响依赖扫描
            }
        }
        return result;
    }
}
