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

    /// <summary>
    /// 工作盘的"暂存 overlay"后缀：新 overlay 先落在这里，工作盘冻结后再原子换入。
    /// 崩溃恢复：工作盘不存在而暂存存在 → 完成换入（见 <see cref="RepairStagedOverlays"/>）。
    /// </summary>
    public const string StagedOverlaySuffix = ".grass-overlay-staged";

    /// <summary>
    /// 修复"换入中断"窗口：Create/Restore 在【新 overlay 已生成、还没换到工作路径】之间崩溃时，
    /// 工作盘缺失但暂存 overlay 完好——把它换入即可恢复。幂等；在启动/恢复入口调用。
    /// </summary>
    public static void RepairStagedOverlays(GrassVmPackage package)
    {
        foreach (var staged in Directory.EnumerateFiles(package.Path, "*" + StagedOverlaySuffix, SearchOption.AllDirectories))
        {
            var active = staged[..^StagedOverlaySuffix.Length];
            if (!File.Exists(active))
                File.Move(staged, active); // 完成被中断的换入
            else
                File.Delete(staged);       // 换入已完成，暂存是残留
        }
    }

    public static Snapshot Create(GrassVmPackage package, VmConfiguration config, string name,
        string? description = null, bool isUpgradeProtection = false,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null)
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
        var state = VmState.Load(package);
        // 父 = 当前工作位置（恢复之后的位置）；没有位置记录时退回树上最新叶
        var parent = state.CurrentSnapshotUuid is not null && tree.All.Any(s => s.Uuid == state.CurrentSnapshotUuid)
            ? tree.Get(state.CurrentSnapshotUuid)
            : tree.All.Where(s => !tree.ChildrenOf(s.Uuid).Any())
                .OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        snap.ParentSnapshotUuid = parent?.Uuid;

        // 冻结语义（外部 QCOW2 overlay 链，与 config 里的稳定工作路径配合）：
        //   1. 把【当前工作盘文件】移动进 snapshots/<uuid>/disks/ —— 它成为冻结点；
        //   2. 在原路径创建指向冻结点的全新 overlay —— 客户机继续写这个新文件，
        //      冻结点从此只读、永不再变；
        //   3. config 的 disk.Path 不变（恢复/克隆/启动都引用稳定路径）。
        // 这样每个快照都是真实的"时间点"：读取走 overlay 链，写到链头。
        foreach (var disk in config.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
        {
            var frozenRel = $"disks/disk-{disk.DeviceId}.qcow2";
            snap.DiskOverlayRefs[disk.DeviceId] = frozenRel;
            if (diskOps is not null)
            {
                var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
                if (File.Exists(activeAbs))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(frozenAbs)!);
                    // 顺序（崩溃安全）：① 在工作路径旁生成暂存 overlay（backing=冻结点的相对引用，
                    // 与最终位置的相对路径一致；qemu-img create -b 不要求 backing 已存在）；
                    // ② 冻结：工作盘原子改名进快照；③ 换入：暂存改名为工作盘。
                    // ②③ 之间崩溃 → RepairStagedOverlays 幂等完成换入。
                    var staged = activeAbs + StagedOverlaySuffix;
                    diskOps.CreateOverlay(frozenAbs, staged, relativeBacking: true);
                    File.Move(activeAbs, frozenAbs);
                    File.Move(staged, activeAbs);
                }
            }
        }
        // UEFI 变量也是"时间点"的一部分（启动顺序、安全启动密钥状态）——一并冻结
        var activeVars = System.IO.Path.Combine(package.FirmwarePath, "VARS.fd");
        if (File.Exists(activeVars))
            File.Copy(activeVars, System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid, "VARS.fd"));
        WriteSnapshot(package, snap);
        // 新快照成为当前工作位置
        state.CurrentSnapshotUuid = snap.Uuid;
        state.Save(package);
        return snap;
    }

    public static void WriteSnapshot(GrassVmPackage package, Snapshot snap)
    {
        var dir = System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(System.IO.Path.Combine(dir, "disks"));
        // 原子写（torn write 会让快照从树上静默消失，孩子指向悬空父）
        AtomicFile.WriteJsonValidated(System.IO.Path.Combine(dir, "metadata.json"), JsonSerializer.Serialize(snap, Opts));
        AtomicFile.WriteJsonValidated(System.IO.Path.Combine(dir, "config.json"), snap.FullConfigSnapshot);
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
    public static void Restore(GrassVmPackage package, string uuid,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null)
    {
        var tree = LoadTree(package);
        var snap = tree.Get(uuid);
        var restored = ConfigJson.Deserialize(snap.FullConfigSnapshot);
        // 磁盘先行（物理操作成功后再改元数据——失败时树仍是旧世界的诚实描述）：
        // 丢弃当前工作 overlay（未快照的更改，调用方已警告），在快照冻结点上开全新 overlay。
        if (diskOps is not null)
        {
            foreach (var (deviceId, frozenRel) in snap.DiskOverlayRefs)
            {
                var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(frozenAbs)) continue; // 元数据先行时代的快照：保留只回滚配置
                var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, restoredDiskPath(restored, deviceId));
                if (string.Equals(activeAbs, frozenAbs, StringComparison.OrdinalIgnoreCase)) continue;
                // 崩溃安全顺序：先造暂存 overlay，再删旧工作盘（未快照更改，调用方已警告），最后换入
                var staged = activeAbs + StagedOverlaySuffix;
                diskOps.CreateOverlay(frozenAbs, staged, relativeBacking: true);
                File.Delete(activeAbs);
                File.Move(staged, activeAbs);
            }
            var frozenVars = System.IO.Path.Combine(package.SnapshotsPath, uuid, "VARS.fd");
            if (File.Exists(frozenVars))
                File.Copy(frozenVars, System.IO.Path.Combine(package.FirmwarePath, "VARS.fd"), overwrite: true);
        }
        new ConfigStore(package).Save(restored);
        // 恢复后工作位置 = 该快照（之后创建的快照是它的孩子）
        var state = VmState.Load(package);
        state.CurrentSnapshotUuid = uuid;
        state.Save(package);
    }

    private static string restoredDiskPath(VmConfiguration config, string deviceId) =>
        config.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId)?.Path
        ?? throw new GrassCoreException($"快照引用了未知的磁盘设备（{deviceId}）。");

    /// <summary>
    /// 删除快照。物理链维护（磁盘先行、元数据最后）：
    ///   有父：被删层 commit 并入父冻结点，其后代（孩子冻结文件 + 当前工作 overlay）rebase 到父；
    ///   无父（链根基座）：冻结文件是所有后代的物理基座——只删元数据，物理文件保留
    ///   （"删除链根"= 忘掉这个检查点，磁盘内容当然还在）。
    /// 有链接克隆依赖时由调用方列出并获用户确认。
    /// </summary>
    public static void Delete(GrassVmPackage package, string uuid,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null)
    {
        var tree = LoadTree(package);
        var deleted = tree.Get(uuid);
        var plan = SnapshotPlanner.PlanDelete(tree, uuid, FindLinkedCloneReferences(package));
        var state = VmState.Load(package);
        var positionWasHere = state.CurrentSnapshotUuid == uuid;
        var parentUuid = deleted.ParentSnapshotUuid;
        var dir = System.IO.Path.Combine(package.SnapshotsPath, uuid);

        if (diskOps is not null)
        {
            foreach (var (deviceId, frozenRel) in deleted.DiskOverlayRefs)
            {
                var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(frozenAbs)) continue;
                var parentFrozen = parentUuid is null ? null : FrozenPath(package, tree.Get(parentUuid), deviceId);

                // 直接依赖者 = 孩子快照的冻结文件 + （位置在被删快照时的）工作 overlay
                var dependents = new List<string>();
                foreach (var childUuid in tree.ChildrenOf(uuid).Select(c => c.Uuid))
                {
                    var p = FrozenPathOrNull(package, tree.Get(childUuid), deviceId);
                    if (p is not null && File.Exists(p)) dependents.Add(p);
                }
                if (positionWasHere)
                {
                    var config = new ConfigStore(package).Load();
                    var active = GrassCore.GrassVm.PathPolicy.Resolve(package,
                        config.Devices.OfType<DiskDevice>().First(d => d.DeviceId == deviceId).Path);
                    if (File.Exists(active)) dependents.Add(active);
                }

                if (parentFrozen is not null && File.Exists(parentFrozen))
                {
                    // 被删层先并入父（客户机可见内容不变），后代改挂父
                    diskOps.CommitOverlay(frozenAbs);
                    foreach (var dep in dependents)
                        if (!string.Equals(dep, frozenAbs, StringComparison.OrdinalIgnoreCase))
                            diskOps.RebaseOverlay(dep, parentFrozen, relativeBacking: true);
                }
                else
                {
                    // 链根基座：物理文件必须保留（后代 overlay 的 backing），只做元数据删除
                }
            }
        }

        foreach (var (childUuid, newParent) in plan.Rebindings)
        {
            var child = tree.Get(childUuid);
            child.ParentSnapshotUuid = newParent == "__root__" ? null : newParent;
            WriteSnapshot(package, child);
        }

        // 物理目录：链根保留 disks/（仍是后代的基座），其余整目录删除。
        // 没有 diskOps（无法做 commit/rebase 链维护）而冻结文件还在 → 只删元数据：
        // 物理文件是某条 overlay 链的一部分，删了就是数据丢失/断链。
        if (Directory.Exists(dir))
        {
            var frozenFiles = Directory.EnumerateFiles(dir, "*.qcow2", SearchOption.AllDirectories).ToList();
            var keepPhysical = parentUuid is null || diskOps is null && frozenFiles.Count > 0;
            if (keepPhysical)
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (!f.Contains("disks")) File.Delete(f);
            }
            else
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // 当前位置被删除 → 位置回到其父（保持"下一步快照挂哪"有确定答案）
        if (positionWasHere)
        {
            state.CurrentSnapshotUuid = parentUuid;
            state.Save(package);
        }
    }

    private static string FrozenPath(GrassVmPackage package, Snapshot snap, string deviceId) =>
        FrozenPathOrNull(package, snap, deviceId)
        ?? throw new GrassCoreException($"快照 {snap.Uuid} 缺少磁盘 {deviceId} 的冻结文件记录。");

    private static string? FrozenPathOrNull(GrassVmPackage package, Snapshot snap, string deviceId) =>
        snap.DiskOverlayRefs.TryGetValue(deviceId, out var rel)
            ? System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid, rel.Replace('/', System.IO.Path.DirectorySeparatorChar))
            : null;

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
