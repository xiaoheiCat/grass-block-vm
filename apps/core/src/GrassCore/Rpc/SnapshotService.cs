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
    /// 从 qcow2 头读虚拟尺寸（偏移 24 的 8 字节大端）。魔法不符/数值离谱 → null
    /// （配置里的 SizeBytes 只能做兜底：盘被 resize 过或 OVF 导入后两者可能不一致，
    /// overlay 的虚拟尺寸必须与既有镜像一致，否则 -u 暂存盘几何与链不匹配）。
    /// </summary>
    public static long? TryReadQcow2VirtualSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[32];
            if (fs.Read(header, 0, 32) < 32) return null;
            if (header[0] != 'Q' || header[1] != 'F' || header[2] != 'I' || header[3] != 0xFB) return null;
            var size = ((long)header[24] << 56) | ((long)header[25] << 48) | ((long)header[26] << 40) | ((long)header[27] << 32)
                | ((long)header[28] << 24) | ((long)header[29] << 16) | ((long)header[30] << 8) | header[31];
            // 合理域：1MiB..64TiB 且 512 对齐（假镜像此处是参数文本，按大端解释是天文数字）
            return size is >= (1L << 20) and <= (1L << 46) && size % 512 == 0 ? size : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>暂存 overlay 的虚拟尺寸：镜像头优先，配置兜底。</summary>
    private static long StagedOverlaySize(string activePath, long configSizeBytes) =>
        TryReadQcow2VirtualSize(activePath) ?? (configSizeBytes > 0 ? configSizeBytes : 64L * 1024 * 1024 * 1024);

    /// <summary>
    /// 修复"换入中断"窗口：Create/Restore 在【新 overlay 已生成、还没换到工作路径】之间崩溃时，
    /// 工作盘缺失但暂存 overlay 完好——把它换入即可恢复。幂等；在启动/恢复入口调用。
    /// 提供 diskOps 时额外修复"半创建快照"：已移动但没等到位置校正 rebase 的冻结文件
    /// （Create 在 ②移动 与 ③校正 之间崩溃，或回滚救援失败留下数据）——其相对 backing
    /// 仍按 disks/ 位置算，不修的话链在该层断裂；若对应工作盘缺失且无暂存，则把冻结
    /// 文件救回工作路径（那是该盘唯一副本）。
    /// 返回 false = Restore 事务日志无法收尾（回滚被 IO 占用卡住）：数据可能停在
    /// "一半盘旧时间点、一半盘新时间点"的撕裂态，调用方必须拒绝启动/变更这台 VM。
    /// </summary>
    public static bool RepairStagedOverlays(GrassVmPackage package,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null)
    {
        // ⓪ 中断的副本 commit 收尾（独立于其他阶段；无 diskOps 也可做——纯文件操作）
        GrassCore.Qemu.TransactionalDiskOps.FinishCommitTempFiles(package);

        // ① Restore 事务日志（最先处理，决定后续 staged/prev 的语义）：
        // 日志在场 = Restore 没提交 = 整个事务必须回滚。不回滚的话：已换入的盘在
        // 快照时间点、没处理到的盘在当前时间点——多盘客户机看到的文件系统是穿越的。
        // 回滚【没走完】就停手：staged/prev 的处置语义取决于本事务的最终走向，
        // 现在按 Create 的规则动它们 = 删掉 prev（旧数据的唯一副本）或强行完成
        // 一个未提交的换入——都不可逆。
        if (!RollbackInterruptedRestore(package)) return false;

        // 孤儿 pending（崩溃在 pending 落盘与日志落盘之间）：没有日志就没有未完结
        // 事务，清掉——留着会让下一次真事务的"已提交"判定永远为假
        try
        {
            if (!File.Exists(RestoreJournalPath(package)))
            {
                var st = VmState.Load(package);
                if (st.PendingRestoreTxId is not null)
                {
                    st.PendingRestoreTxId = null;
                    st.Save(package);
                }
            }
        }
        catch { /* state 读写失败：留待下轮 */ }

        // 修复绝不阻断启动：子目录无权限（AllDirectories 枚举在 Windows 上对任一
        // 不可访问目录直接抛 UnauthorizedAccessException）或单文件被占用（杀软/
        // 索引器握着新建的 overlay）都按"留待下一轮"处理
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };
        foreach (var staged in Directory.EnumerateFiles(package.Path, "*" + StagedOverlaySuffix, enumOpts))
        {
            var active = staged[..^StagedOverlaySuffix.Length];
            try
            {
                if (!File.Exists(active))
                    File.Move(staged, active); // 完成被中断的换入
                else
                    File.Delete(staged);       // 换入已完成，暂存是残留
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 占用/竞态：留待下一轮
            }
        }
        // Restore 的 prev 暂留：active 在场 → prev 是成功后的残留（删除）；
        // active 缺失 → 换入没完成，prev 救回工作路径（Restore 回滚方向的修复）
        foreach (var prev in Directory.EnumerateFiles(package.Path, "*" + RestorePrevSuffix, enumOpts))
        {
            var active = prev[..^RestorePrevSuffix.Length];
            try
            {
                if (File.Exists(active)) File.Delete(prev);
                else File.Move(prev, active);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 占用/竞态：留待下一轮
            }
        }

        if (diskOps is null) return true;
        var ops = diskOps; // 非空局部（流分析在长方法/循环里会丢参数的判空事实）
        try
        {
            // 半创建快照 = snapshots/<uuid>/ 存在但没有 metadata.json（树上看不见）。
            // rebase 目标优先级：冻结意向（创建时记录的物理链头）> 位置快照引用。
            // 意向缺失时才用元数据推断——分支树上"最新叶"兜底可能指向错误分支。
            // 已经能解析的 backing 说明 ③校正 rebase 已完成——绝不能按元数据再
            // rebase 一遍（会把正确的链 unsafe 重指到别的分支）。
            var tree = LoadTree(package);
            if (!Directory.Exists(package.SnapshotsPath)) return true;
            var state = VmState.Load(package);
            // 活动链的物理足迹（active 的 backing → … → 根）：半创建目录的"收编"
            // 判据 = 它的冻结文件真的在这条链上（数据层在被使用的链里，只是树不知道）
            var chainFootprint = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var adoptedThisRound = new List<KeyValuePair<string, string>>(); // uuid → 直接头冻结文件
            string? directHead = null;
            HashSet<string> chainTails = new(StringComparer.OrdinalIgnoreCase); // 各盘链基座（没有 backing 合法）
            try
            {
                var cfg = new ConfigStore(package).Load();
                // 足迹按【每张非外部盘】的链分别走：多盘 VM 各链长度不同、基座各异，
                // 只走第一张盘会把其他盘的合法基座当成"不在链上"
                var tails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var disk in cfg.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
                {
                    var cur = GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
                    for (var hops = 0; hops < 64 && cur is not null; hops++)
                    {
                        var b = ops.QueryBackingFile(cur);
                        if (b is null) break;
                        chainFootprint.Add(System.IO.Path.GetFullPath(b));
                        directHead ??= System.IO.Path.GetFullPath(b);
                        cur = b;
                    }
                    if (cur is not null) tails.Add(System.IO.Path.GetFullPath(cur));
                }
                chainTails = tails;
            }
            catch { /* 足迹拿不到：收编判据退化为"指针可解析"（更保守） */ }
            Snapshot? position = null;
            if (state.CurrentSnapshotUuid is not null)
                position = tree.All.FirstOrDefault(s =>
                    string.Equals(s.Uuid, state.CurrentSnapshotUuid, StringComparison.OrdinalIgnoreCase));
            position ??= tree.All.Where(s => !tree.ChildrenOf(s.Uuid).Any())
                .OrderByDescending(s => s.CreatedAt).FirstOrDefault();
            foreach (var dir in Directory.EnumerateDirectories(package.SnapshotsPath))
            {
                if (File.Exists(System.IO.Path.Combine(dir, "metadata.json"))) continue;
                // 只有意向、没建 disks/ 的目录（WriteFreezeIntent 与首盘操作之间崩溃）
                // 是合法现场——跳过。绝不因它抛 DirectoryNotFoundException 中止
                // 整个半创建处理（后面的目录里可能还有唯一副本等着救）
                var disksDir = System.IO.Path.Combine(dir, "disks");
                if (!Directory.Exists(disksDir)) continue;
                var dirUuid = System.IO.Path.GetFileName(dir);
                var intent = LoadFreezeIntent(package, dirUuid);
                // 只有【创建中断现场】才做半创建处理：freeze-intent.json（Create 落
                // 任何盘之前一定先写）或 rescue-pending.json 在场才算。链根基座
                // 删除保留的 disks/（metadata 与 intent 一并被清）不是中断现场——
                // 把它当半创建，会把链基座 unsafe-rebase 到它自己的后代上
                //（环形 backing 链，QEMU 拒绝打开），并把已删快照复活成倒挂节点
                if (!File.Exists(FreezeIntentPath(package, dirUuid))
                    && !File.Exists(RescuePendingPath(package, dirUuid)))
                    continue;
                foreach (var frozen in Directory.EnumerateFiles(disksDir, "disk-*.qcow2"))
                {
                    // 文件名形态 disk-<deviceId>.qcow2
                    var name = System.IO.Path.GetFileName(frozen);
                    var deviceId = name["disk-".Length..^".qcow2".Length];
                    // 意向父（创建时记录）；没有意向的旧目录才退化用位置快照引用
                    string? intendedParent = null;
                    if (intent?.ParentsByDeviceId is not null)
                        intendedParent = ResolveIntent(package, intent.ParentsByDeviceId, deviceId);
                    else if (position?.DiskOverlayRefs.TryGetValue(deviceId, out var posRef) == true)
                        intendedParent = System.IO.Path.Combine(package.SnapshotsPath, position.Uuid,
                            posRef.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    // 对应工作盘缺失且无暂存 = 回滚救援失败的盘：这是它唯一的数据副本，
                    // 救回工作路径（rebase 目标 = 意向父，按 disks/ 位置重算）
                    var activeCandidate = FindActivePathForDevice(package, deviceId);
                    if (activeCandidate is not null && !File.Exists(activeCandidate)
                        && !File.Exists(activeCandidate + StagedOverlaySuffix)
                        && !File.Exists(activeCandidate + RestorePrevSuffix))
                    {
                        try
                        {
                            File.Move(frozen, activeCandidate);
                            // 环形防线：意向父的祖先链若包含本文件（异常现场/倒挂
                            // 残留），rebase 会造出 QEMU 拒绝打开的环——宁可留着
                            // 悬空指针让 StartVm 报真实错误
                            if (intendedParent is not null && File.Exists(intendedParent)
                                && !BackingChainReaches(ops, intendedParent, frozen))
                                ops.RebaseOverlay(activeCandidate, intendedParent, relativeBacking: true, unsafeMode: true);
                            continue;
                        }
                        catch (IOException)
                        {
                            // 救不回来：继续做指针修复，至少链不断
                        }
                    }
                    // backing 已可解析 = ③校正 rebase 已完成（或本来就没有父）：
                    // 链是好的，不碰。只有解析不了（移动后悬空的 disks/ 相对指针）才修
                    if (ops.QueryBackingFile(frozen) is not null) continue;
                    if (intendedParent is null || !File.Exists(intendedParent)) continue;
                    if (BackingChainReaches(ops, intendedParent, frozen)) continue; // 环形防线（同上）
                    try
                    {
                        ops.RebaseOverlay(frozen, intendedParent, relativeBacking: true, unsafeMode: true);
                    }
                    catch (GrassCore.Qemu.QemuImgException)
                    {
                        // 尽力修复：失败留给后续 StartVm 的真实错误暴露
                    }
                }

                // 回滚救援登记：数据已回工作路径、指针还按冻结位置算（悬空）。
                // 按 freeze-intent 重算（这时文件在工作路径上，相对引用按 disks/ 位置）
                var pendingPath = RescuePendingPath(package, dirUuid);
                if (File.Exists(pendingPath))
                {
                    RescuePending? pending = null;
                    try { pending = JsonSerializer.Deserialize<RescuePending>(File.ReadAllText(pendingPath), JournalOpts); }
                    catch { /* 损坏：按无登记处理（保守不动） */ }
                    if (pending is not null && intent?.ParentsByDeviceId is not null)
                    {
                        var fixedAll = true;
                        foreach (var deviceId in pending.DeviceIds)
                        {
                            var activeAbs = FindActivePathForDevice(package, deviceId);
                            var parentAbs = ResolveIntent(package, intent.ParentsByDeviceId, deviceId);
                            if (activeAbs is null || parentAbs is null || !File.Exists(activeAbs)) { fixedAll = false; continue; }
                            if (BackingChainReaches(ops, parentAbs, activeAbs)) { fixedAll = false; continue; } // 环形防线
                            try
                            {
                                ops.RebaseOverlay(activeAbs, parentAbs, relativeBacking: true, unsafeMode: true);
                            }
                            catch (GrassCore.Qemu.QemuImgException)
                            {
                                fixedAll = false; // 下轮再试（标记保留）
                            }
                        }
                        if (fixedAll)
                        {
                            try { File.Delete(pendingPath); } catch { /* 下轮再删 */ }
                        }
                    }
                }

                // 收编：数据层完整在链上（swap 已由暂存阶段收尾、每张盘指针可解析），
                // 只是 metadata 没来得及写。不收编 = 永久"不可见层"：下次 Create 找
                // 不到物理头属主（树凭空多一个根）；再往后 Delete 底层快照时它不在
                // 依赖图里，在用基座被物理删掉，工作链当场悬空
                if (!File.Exists(pendingPath))
                {
                    try
                    {
                        var frozenFiles = Directory.EnumerateFiles(disksDir, "disk-*.qcow2")
                            .Select(f => System.IO.Path.GetFullPath(f)).ToList();
                        var onChain = frozenFiles.Count > 0
                            && (chainFootprint.Count == 0 || frozenFiles.All(chainFootprint.Contains));
                        // 指针健康 = 每张冻结文件要么 backing 可解析，要么它就是链基座
                        //（没有 backing 是合法形态——首个快照的冻结层就是基座。
                        //  之前一刀切要求"必须有 backing"，基座层永远收编不了）
                        var pointersOk = frozenFiles.All(f =>
                            ops.QueryBackingFile(f) is not null || chainTails.Contains(f));
                        if (onChain && pointersOk)
                        {
                            var refs = frozenFiles.ToDictionary(
                                f => System.IO.Path.GetFileName(f)["disk-".Length..^".qcow2".Length],
                                f => "disks/" + System.IO.Path.GetFileName(f));
                            // 父 = 第一张冻结文件的物理 backing 的属主（树上已有快照，
                            // 或本轮已收编的目录——两个半创建叠着崩溃的情况）
                            var parentAbs = ops.QueryBackingFile(frozenFiles[0]);
                            string? parentUuid = null;
                            if (parentAbs is not null)
                            {
                                parentAbs = System.IO.Path.GetFullPath(parentAbs);
                                parentUuid = tree.All.FirstOrDefault(s => s.DiskOverlayRefs.Values.Any(v =>
                                        PathEquals(System.IO.Path.Combine(package.SnapshotsPath, s.Uuid,
                                            v.Replace('/', System.IO.Path.DirectorySeparatorChar)), parentAbs)))?.Uuid
                                    ?? adoptedThisRound.FirstOrDefault(kv => PathEquals(kv.Value, parentAbs)).Key;
                            }
                            var adoptedSnap = new Snapshot
                            {
                                Uuid = dirUuid,
                                ParentSnapshotUuid = parentUuid,
                                Name = "恢复的快照",
                                Description = "创建过程中断，已自动收编。",
                                CreatedAt = DateTimeOffset.Now,
                                FullConfigSnapshot = File.ReadAllText(package.ConfigPath),
                                DiskOverlayRefs = refs,
                            };
                            WriteSnapshot(package, adoptedSnap);
                            adoptedThisRound.Add(new(dirUuid, frozenFiles[0]));
                            try { File.Delete(FreezeIntentPath(package, dirUuid)); } catch { /* 无害 */ }
                        }
                    }
                    catch { /* 收编失败：保持不可见，下轮修复再试 */ }
                }
            }

            // 本轮有收编 → 位置指向"链头冻结文件"所在的那个（物理事实优先）
            if (adoptedThisRound.Count > 0 && directHead is not null)
            {
                var headDir = adoptedThisRound.FirstOrDefault(kv => PathEquals(kv.Value, directHead));
                if (headDir.Key is not null && !string.Equals(state.CurrentSnapshotUuid, headDir.Key, StringComparison.OrdinalIgnoreCase))
                {
                    state.CurrentSnapshotUuid = headDir.Key;
                    state.Save(package);
                }
            }
        }
        catch
        {
            // 修复是尽力而为：任何结构性异常都不应阻断启动路径
        }
        return true;
    }

    public static Snapshot Create(GrassVmPackage package, VmConfiguration config, string name,
        string? description = null, bool isUpgradeProtection = false,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null,
        string? upgradeProtectionBackupDir = null)
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
            UpgradeProtectionBackupDir = upgradeProtectionBackupDir,
        };
        var tree = LoadTree(package);
        var state = VmState.Load(package);
        // 父 = 当前工作位置（恢复之后的位置）；没有位置记录时退回树上最新叶。
        // 大小写语义与树一致（OrdinalIgnoreCase）
        var parent = state.CurrentSnapshotUuid is not null
                     && tree.All.Any(s => string.Equals(s.Uuid, state.CurrentSnapshotUuid, StringComparison.OrdinalIgnoreCase))
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
        // 预检：物理层开启时，所有内部盘的工作文件必须在场——中途发现缺盘会做出
        // "半快照"（部分盘冻结、部分盘悬空），树上的引用不再可信
        if (diskOps is not null)
        {
            foreach (var disk in config.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
            {
                var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
                if (!File.Exists(activeAbs))
                    throw new InvalidOperationException(
                        $"磁盘文件缺失，无法创建快照：{disk.Path}。请先恢复该文件（例如从备份拷回），或删除后重建这台虚拟机。");
            }
        }

        // 全有或全无：任何一盘失败 → 逐盘逆操作回滚（新 overlay 删掉、冻结盘移回
        // 工作路径），快照目录整体不落树。
        // begun：已把工作盘移进快照目录、还没走完的盘——回滚时它们的数据在冻结路径上，
        // 不先救回来就整目录删除 = 把该盘唯一副本抹掉（数据丢失事故）
        var completed = new List<(string activeAbs, string frozenAbs, string? parentFrozenAbs, string deviceId)>();
        var begun = new List<(string activeAbs, string frozenAbs, string staged, string? parentFrozenAbs, string deviceId)>();
        // 首个内部盘的物理链头（用于树父归属；info 不可用时保持 null → 沿用元数据父）
        string? firstPhysicalBacking = null;
        try
        {
            // 冻结意向先落盘：per 盘的 rebase 目标（= 工作盘此刻的实际 backing）。
            // 崩溃在 ②移动 与 ③校正 rebase 之间时，修复用它拿到【正确的】父——
            // 否则只能靠元数据猜（最新叶兜底），在分支树上会 unsafe rebase 到
            // 错误分支（客户机读到别人家的数据层，无任何报错）
            var intent = new Dictionary<string, string?>();
            if (diskOps is not null)
            {
                foreach (var disk in config.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
                {
                    var activeAbs0 = GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
                    var backing0 = diskOps.QueryBackingFile(activeAbs0)
                        ?? (parent?.DiskOverlayRefs.TryGetValue(disk.DeviceId, out var pr0) == true
                            ? System.IO.Path.Combine(package.SnapshotsPath, parent.Uuid,
                                pr0.Replace('/', System.IO.Path.DirectorySeparatorChar))
                            : null);
                    intent[disk.DeviceId] = backing0 is null
                        ? null
                        : System.IO.Path.GetRelativePath(package.Path, backing0).Replace('\\', '/');
                }
                WriteFreezeIntent(package, snap.Uuid, intent);
            }
            foreach (var disk in config.DevicesOfType<DiskDevice>().Where(d => !d.IsExternal))
            {
                var frozenRel = $"disks/disk-{disk.DeviceId}.qcow2";
                if (diskOps is null)
                {
                    // 元数据模式（调用方明确关闭物理层）：引用照登记，Restore/Delete 对缺失
                    // 冻结文件有各自的保守处理（只回滚配置 / 保留物理文件）
                    snap.DiskOverlayRefs[disk.DeviceId] = frozenRel;
                }
                else
                {
                    var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid,
                        frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
                    snap.DiskOverlayRefs[disk.DeviceId] = frozenRel;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(frozenAbs)!);
                    // 顺序（崩溃安全）：① 生成暂存 overlay——backing（冻结点）此刻还不存在，
                    // 用 -u + 显式尺寸（真实 qemu-img 会拒绝打开不存在的 backing，-u 跳过打开）；
                    // ② 冻结：工作盘原子改名进快照；③ 换入：暂存改名为工作盘。
                    // ②③ 之间崩溃 → RepairStagedOverlays 幂等完成换入。
                    var staged = activeAbs + StagedOverlaySuffix;
                    diskOps.CreateOverlay(frozenAbs, staged, relativeBacking: true,
                        deferBackingVirtualSize: StagedOverlaySize(activeAbs, disk.SizeBytes));
                    // 冻结后的 rebase 目标 = 意向里记录的物理链头（与下方一致；
                    // 意向文件已先于任何移动落盘）
                    string? parentFrozenAbs = ResolveIntent(package, intent, disk.DeviceId)
                        ?? diskOps.QueryBackingFile(activeAbs);
                    firstPhysicalBacking ??= parentFrozenAbs;
                    begun.Add((activeAbs, frozenAbs, staged, parentFrozenAbs, disk.DeviceId));
                    File.Move(activeAbs, frozenAbs);
                    // 冻结文件的相对 backing 是按 disks/（它当工作盘时的家）算的——移到
                    // snapshots/<uuid>/disks/ 后按新位置解析会指向不存在的路径（真实
                    // qemu-img 打开链即失败）。立即 unsafe rebase 成按冻结位置算的引用。
                    if (parentFrozenAbs is not null)
                        diskOps.RebaseOverlay(frozenAbs, parentFrozenAbs, relativeBacking: true, unsafeMode: true);
                    File.Move(staged, activeAbs);
                    begun.RemoveAt(begun.Count - 1);
                    completed.Add((activeAbs, frozenAbs, parentFrozenAbs, disk.DeviceId));
                }
            }
            // UEFI 变量也是"时间点"的一部分（启动顺序、安全启动密钥状态）——一并冻结
            var activeVars = System.IO.Path.Combine(package.FirmwarePath, "VARS.fd");
            if (File.Exists(activeVars))
            {
                var frozenVars = System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid, "VARS.fd");
                var varsTmp = frozenVars + ".grass-tmp";
                File.Copy(activeVars, varsTmp, overwrite: true);
                File.Move(varsTmp, frozenVars);
            }
        }
        catch
        {
            var rescueFailed = false;
            var rescuePending = new List<string>();
            // 回滚（逆序）：
            // ① 换入已完成的盘：删新 overlay，冻结盘移回工作路径，并把已改成"按冻结位置"
            //    的相对 backing 改回"按 disks/ 位置"（否则移回去后指针失效）
            for (var i = completed.Count - 1; i >= 0; i--)
            {
                var (activeAbs, frozenAbs, parentFrozenAbs, deviceId) = completed[i];
                try
                {
                    File.Delete(activeAbs);
                    File.Move(frozenAbs, activeAbs);
                    try
                    {
                        if (parentFrozenAbs is not null && diskOps is not null)
                            diskOps.RebaseOverlay(activeAbs, parentFrozenAbs, relativeBacking: true, unsafeMode: true);
                    }
                    catch
                    {
                        // 数据已安全回到工作路径，但相对 backing 还是按冻结位置算的——
                        // 现在解析不到任何文件。登记待修（repair 按 intent 重算指针），
                        // 目录保留（intent/marker 都在里面）
                        rescuePending.Add(deviceId);
                        rescueFailed = true;
                    }
                }
                catch
                {
                    // 回滚失败：RepairStagedOverlays 兜底（暂存/冻结并存时完成换入），
                    // 这里不再向上抛——原始异常优先
                    rescueFailed = true;
                }
            }
            // ② 中途盘（数据还在冻结路径上）：丢暂存 overlay，冻结盘移回工作路径，
            //    指针改回按 disks/ 位置——先救人再删目录
            for (var i = begun.Count - 1; i >= 0; i--)
            {
                var (activeAbs, frozenAbs, staged, parentFrozenAbs, deviceId) = begun[i];
                try
                {
                    if (File.Exists(staged)) File.Delete(staged);
                    if (!File.Exists(activeAbs) && File.Exists(frozenAbs))
                    {
                        File.Move(frozenAbs, activeAbs);
                        try
                        {
                            if (parentFrozenAbs is not null && diskOps is not null)
                                diskOps.RebaseOverlay(activeAbs, parentFrozenAbs, relativeBacking: true, unsafeMode: true);
                        }
                        catch
                        {
                            // 同 ①：数据在场、指针悬空，登记待修
                            rescuePending.Add(deviceId);
                            rescueFailed = true;
                        }
                    }
                }
                catch
                {
                    // 救援失败：该盘唯一副本还躺在快照目录里——目录绝不能删。
                    // 留给 RepairStagedOverlays 的"冻结文件救回"分支处理
                    rescueFailed = true;
                }
            }
            if (rescuePending.Count > 0)
                WriteRescuePending(package, snap.Uuid, rescuePending);
            // 只有全部数据都确认回到工作路径才能删目录；否则宁可留下一个树上不可见
            // 的目录（元数据未写，下次启动的修复逻辑会把冻结文件救回工作路径）
            if (!rescueFailed)
            {
                try { Directory.Delete(System.IO.Path.Combine(package.SnapshotsPath, snap.Uuid), recursive: true); }
                catch { /* 目录残留无害：未写 metadata，树上不可见 */ }
            }
            throw;
        }
        // 顺序：位置先落，快照元数据后落。反过来的崩溃窗口（元数据在了、位置还是旧的）
        // 会让下一次 Create 把新快照挂到【错误的父】上并 unsafe rebase 到错误基座——
        // 链静默分叉。位置先行时，窗口内崩溃 = 位置指向树上不存在的快照 → 父选择
        // 自动退回最新叶（见上方 parent 解析），物理层由半创建修复收拾，两边自洽。
        // 树父 = 物理链头的属主：backing 落在哪个快照的冻结文件上就是谁的孩子；
        // 没有属主（链根删除后的未跟踪基座）→ 新根。物理链头被 info 读不到时
        // （qemu-img 不可用）沿用元数据推断的 parent。
        if (firstPhysicalBacking is not null)
        {
            var owner = tree.All.FirstOrDefault(s => s.DiskOverlayRefs.Values.Any(v =>
                string.Equals(
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(package.SnapshotsPath, s.Uuid,
                        v.Replace('/', System.IO.Path.DirectorySeparatorChar))),
                    System.IO.Path.GetFullPath(firstPhysicalBacking),
                    StringComparison.OrdinalIgnoreCase)));
            snap.ParentSnapshotUuid = owner?.Uuid;
        }
        state.CurrentSnapshotUuid = snap.Uuid;
        state.Save(package);
        WriteSnapshot(package, snap);
        // 意向文件随元数据一起落地（写完元数据它就没用了；半创建场景由上面的
        // WriteSnapshot 终结，残留意向文件只在无元数据目录里有意义）
        try { if (File.Exists(FreezeIntentPath(package, snap.Uuid))) File.Delete(FreezeIntentPath(package, snap.Uuid)); }
        catch { /* 无害：树上已有元数据，修复不会再碰这个目录 */ }
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
    /// 全有或全无：所有盘要么都回到快照点、要么都保持现状——中途失败/崩溃会把
    /// 旧工作盘从 .grass-restore-prev 救回（否则一半盘在旧时间点、一半在新时间点，
    /// 客户机看到的文件系统就是穿越的）。
    /// </summary>
    public static void Restore(GrassVmPackage package, string uuid,
        GrassCore.Qemu.TransactionalDiskOps? diskOps = null)
    {
        var tree = LoadTree(package);
        // 陈旧 UUID（快照列表刷新前被别处删掉）不该以裸 KeyNotFoundException
        // 冲到界面横幅里——与 Delete 的措辞一致，请用户刷新
        if (!tree.TryGet(uuid, out var snap) || snap is null)
            throw new GrassCoreException("快照不存在，请刷新列表。");
        var restored = ConfigJson.Deserialize(snap.FullConfigSnapshot);
        // 磁盘先行（物理操作成功后再改元数据——失败时树仍是旧世界的诚实描述）：
        // 丢弃当前工作 overlay（未快照的更改，调用方已警告），在快照冻结点上开全新 overlay。
        var swapped = new List<(string activeAbs, string prevAbs)>();
        var inFlight = new List<(string activeAbs, string prevAbs, string staged)>();
        // 本次事务【动过手】的暂存 overlay：CreateOverlay 成功但后续步骤抛出时，
        // 该盘还没进 inFlight（add 在 Move 之后）——不留这份清单，暂存会幸存到
        // 下次修复，被当成"换入中断"补完换入 = 一半盘旧时间点、一半盘新时间点
        var attemptedStaged = new List<string>();
        var activeVarsPath = System.IO.Path.Combine(package.FirmwarePath, "VARS.fd");
        var varsPrev = activeVarsPath + RestorePrevSuffix;
        var varsSwapped = false;
        if (diskOps is not null)
        {
            // 预检（与 Create 同标准）：工作文件缺失要在【写任何事务状态之前】发现——
            // 先写 pending 再失败会留下"有 pending 无 journal"的孤儿标志（虽然
            // 能被修复自愈，但承诺的次序是假的）；中途 File.Move 裸抛英文
            // FileNotFoundException 同样不可接受，用户需要"磁盘缺失，先在设置
            // 里移除设备"这样的可行动提示
            foreach (var (deviceId, frozenRel) in snap.DiskOverlayRefs)
            {
                var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(frozenAbs)) continue;
                var dev = restored.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId);
                if (dev is null) continue;
                var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, dev.Path);
                if (!File.Exists(activeAbs))
                    throw new GrassCoreException(
                        $"磁盘文件缺失，无法恢复快照：{dev.Path}。请先恢复该文件（例如从备份拷回），或删除后重建这台虚拟机。");
            }
            // 事务意向先落（state.PendingRestoreTxId），再落磁盘日志。提交判定 =
            // "state 里的 pending 已清空"——单一事实来源，恢复到当前位置也能区分
            // （只看"位置 == 目标"在那里有歧义）。
            var txId = Guid.NewGuid().ToString("N");
            var pendingState = VmState.Load(package);
            pendingState.PendingRestoreTxId = txId;
            pendingState.Save(package);
            // 事务日志：覆盖所有【计划要动】的盘——没开始动的盘 prev 不存在，回滚天然跳过
            var planned = snap.DiskOverlayRefs.Keys
                .Select(deviceId => restored.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId))
                .Where(d => d is not null)
                .Select(d => GrassCore.GrassVm.PathPolicy.Resolve(package, d!.Path))
                .ToList();
            WriteRestoreJournal(package, uuid, planned, vars: true, txId);
            try
            {
                foreach (var (deviceId, frozenRel) in snap.DiskOverlayRefs)
                {
                    var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, uuid,
                        frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (!File.Exists(frozenAbs)) continue; // 元数据先行时代的快照：保留只回滚配置
                    var activeAbs = GrassCore.GrassVm.PathPolicy.Resolve(package, restoredDiskPath(restored, deviceId));
                    if (string.Equals(activeAbs, frozenAbs, StringComparison.OrdinalIgnoreCase)) continue;
                    // 崩溃安全 + 可回滚顺序：① 暂存 overlay（指向快照冻结点）；② 旧工作盘
                    // 改名为 .grass-restore-prev（暂留——回滚还要用它）；③ 暂存换入。
                    // 全部盘成功后再统一删除 prev。
                    var staged = activeAbs + StagedOverlaySuffix;
                    var prev = activeAbs + RestorePrevSuffix;
                    var dev = restored.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId);
                    attemptedStaged.Add(staged);
                    diskOps.CreateOverlay(frozenAbs, staged, relativeBacking: true,
                        deferBackingVirtualSize: StagedOverlaySize(frozenAbs, dev is { SizeBytes: > 0 } ? dev.SizeBytes : 0));
                    DeleteIfExists(prev);
                    File.Move(activeAbs, prev);
                    inFlight.Add((activeAbs, prev, staged));
                    File.Move(staged, activeAbs);
                    inFlight.RemoveAt(inFlight.Count - 1);
                    swapped.Add((activeAbs, prev));
                }
                // UEFI 变量同样回滚到快照点（旧变量先暂留，回滚要救回）。
                // CopyAtomic（tmp+move）：半截写入的 NVRAM 会让 OVMF 变量区损坏，
                // 挂起/启动路径再读它就是硬故障。
                // active 缺失 = 没有可回滚的旧变量：单向恢复快照变量即可
                // （跳过的话磁盘/配置都回去了、NVRAM 预检却让机器永远开不了机）
                var frozenVars = System.IO.Path.Combine(package.SnapshotsPath, uuid, "VARS.fd");
                if (File.Exists(frozenVars) && !File.Exists(activeVarsPath))
                {
                    Directory.CreateDirectory(package.FirmwarePath);
                    CopyAtomic(frozenVars, activeVarsPath);
                }
                else if (File.Exists(frozenVars) && File.Exists(activeVarsPath))
                {
                    DeleteIfExists(varsPrev);
                    CopyAtomic(activeVarsPath, varsPrev);
                    varsSwapped = true;
                    CopyAtomic(frozenVars, activeVarsPath);
                }
            }
            catch
            {
                var rescueFailed = false;
                // 逆序回滚：已换入的盘把 prev 换回来；进行中的盘丢暂存、prev 归位
                for (var i = swapped.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var (a, p) = swapped[i];
                        File.Delete(a);
                        File.Move(p, a);
                    }
                    catch { rescueFailed = true; /* 修复逻辑（日志回滚）兜底 */ }
                }
                for (var i = inFlight.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var (a, p, s) = inFlight[i];
                        if (File.Exists(s)) File.Delete(s);
                        if (!File.Exists(a) && File.Exists(p)) File.Move(p, a);
                    }
                    catch { rescueFailed = true; /* 修复逻辑（日志回滚）兜底 */ }
                }
                // 未入账的暂存（CreateOverlay 成功后、Move 成功前抛出的盘）：一并清掉。
                // 已成功换入/已回滚的盘此路径已无文件，Exists 守卫下是无害空转；
                // 删不掉就 rescueFailed → 留日志给修复入口按"未提交"重放
                foreach (var s in attemptedStaged)
                {
                    try { if (File.Exists(s)) File.Delete(s); }
                    catch { rescueFailed = true; }
                }
                if (varsSwapped && File.Exists(varsPrev))
                {
                    // VARS 救回失败同样计入 rescueFailed：日志若被删掉，"磁盘/配置
                    // 已回滚、NVRAM 停在快照点"的撕裂态就永久固化（启动顺序/
                    // Secure Boot 密钥与磁盘内容不一致且无自愈入口）。修复入口
                    // 的日志回滚自带 VARS 处置，保留日志让它重试
                    try { CopyAtomic(varsPrev, activeVarsPath); }
                    catch { rescueFailed = true; }
                }
                // 全部救回才删日志；有任何一步失败就留着——修复入口会按日志重放回滚。
                // pending 同步清除（同步回滚成功 = 事务完结）
                if (!rescueFailed)
                {
                    DeleteRestoreJournal(package);
                    try
                    {
                        var st = VmState.Load(package);
                        if (st.PendingRestoreTxId is not null)
                        {
                            st.PendingRestoreTxId = null;
                            st.Save(package);
                        }
                    }
                    catch { /* pending 清不掉：修复入口兜底（journal 已删 → 视为已完结） */ }
                }
                throw;
            }
        }
        new ConfigStore(package).Save(restored);
        // 位置 + pending 清除同一次落盘（单一提交点）：此后崩溃 → 修复按"已提交"收尾
        var state = VmState.Load(package);
        state.CurrentSnapshotUuid = uuid;
        state.PendingRestoreTxId = null;
        state.Save(package);
        // 事务提交点已过（元数据全部落定）——删日志，之后残留的 prev 只是待清理垃圾
        DeleteRestoreJournal(package);
        foreach (var (a, p) in swapped)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* 残留 prev 由修复清理 */ }
        if (varsSwapped)
            try { if (File.Exists(varsPrev)) File.Delete(varsPrev); } catch { /* 同上 */ }
    }

    /// <summary>恢复事务日志：Restore 开始前写入、元数据全部落定后删除。在场即"未提交"。</summary>
    private static string RestoreJournalPath(GrassVmPackage package) =>
        System.IO.Path.Combine(package.SnapshotsPath, "restore-journal.json");

    /// <summary>日志体（公共类型 + 大小写不敏感：手工/恢复工具写出的日志同样能读）。
    /// PreRestoreConfigJson：事务开始前的 config.json 原文——回滚时连配置一起还原
    /// （否则崩溃在 ConfigStore.Save 与 state.Save 之间会留下"新配置 + 旧磁盘"的错配）。
    /// TxId：事务一次性随机标识。仅凭"位置 == 目标"判定已提交在恢复到当前位置时
    /// 有歧义（目标本来就是当前位置）——用 state.PendingRestoreTxId 配对才能区分。</summary>
    public sealed record RestoreJournal(
        string TargetSnapshotUuid, List<string> ActiveDiskRels, bool Vars,
        string? PreRestoreConfigJson = null, string? TxId = null);

    private static readonly JsonSerializerOptions JournalOpts = new() { PropertyNameCaseInsensitive = true };

    private static void WriteRestoreJournal(GrassVmPackage package, string uuid, List<string> activeAbsPaths, bool vars, string txId)
    {
        Directory.CreateDirectory(package.SnapshotsPath);
        var rels = activeAbsPaths
            .Select(p => System.IO.Path.GetRelativePath(package.Path, p).Replace('\\', '/'))
            .ToList();
        string? preConfig = null;
        try
        {
            var configPath = System.IO.Path.Combine(package.Path, "config.json");
            if (File.Exists(configPath)) preConfig = File.ReadAllText(configPath);
        }
        catch { /* 读不到就少一层回滚保险，磁盘层仍然完整 */ }
        AtomicFile.WriteJsonValidated(RestoreJournalPath(package),
            JsonSerializer.Serialize(new RestoreJournal(uuid, rels, vars, preConfig, txId), JournalOpts));
    }

    private static void DeleteRestoreJournal(GrassVmPackage package)
    {
        try { if (File.Exists(RestoreJournalPath(package))) File.Delete(RestoreJournalPath(package)); }
        catch { /* 删不掉：下次修复会再回滚一次（幂等、无害——盘已在快照点） */ }
    }

    /// <summary>
    /// 回滚被中断的 Restore（日志在场时）。
    /// 提交判定 = state.PendingRestoreTxId 已清空（提交点把它和位置一次性落盘）。
    /// TxId 配对：pending 与日志不匹配（跨事务残留/手工日志）按未提交处理——保守。
    /// 已提交：磁盘保持换入后状态，只清理 prev 残留（回滚一个已提交事务会让元数据
    /// 说"已恢复"、磁盘却是恢复前的数据）。
    /// 未提交规则（每个日志里的盘）：prev 在场 = 至少走到了"旧工作盘改名"——
    /// active 里是新 overlay（或空），删掉、prev 归位；prev 不在场 = 该盘没动过。
    /// 配置原文（若有）一并还原。
    /// 返回 true = 日志已完全处置（删除）；false = 中途中止（调用方不得再动
    /// staged/prev——它们的语义取决于本事务的最终走向，现在动会毁掉回滚数据）。
    /// </summary>
    private static bool RollbackInterruptedRestore(GrassVmPackage package)
    {
        var journalPath = RestoreJournalPath(package);
        if (!File.Exists(journalPath)) return true; // 无事务：nothing to do
        try
        {
            RestoreJournal? journal;
            try
            {
                journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(journalPath), JournalOpts);
            }
            catch (JsonException)
            {
                return false; // 日志损坏：不动任何数据（回滚错盘比留着更糟）
            }
            if (journal is null) return false;

            VmState state;
            // 日志在场时必须【严格】读 state：VmState.Load 对损坏的 state.json 静默
            // 返回全新实例（pending 为 null）→ 被误判成"已提交"→ prev（恢复前
            // 数据的唯一副本）当残留删掉 = 把不确定状态往毁数据的方向裁决。
            // 读不了就按未提交处理（同"日志损坏"的保守选择）
            try
            {
                if (!File.Exists(package.StatePath)) return false; // 没有可判定的提交点
                state = JsonSerializer.Deserialize<VmState>(File.ReadAllText(package.StatePath),
                          GrassCore.Config.VmState.ForRead)
                      ?? throw new JsonException();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return false; // state 缺失/损坏：事务状态不明，保守按未提交
            }
            // 提交判定 = pending 已清空。额外的 TxId 配对只在【pending 在场】时
            // 生效：pending 属于别的事务而日志是上一笔的残留（上一笔已提交、
            // 只是删日志失败）时，pending=null 路径照旧按已提交清理；而
            // pending≠日志 的错配意味着 pending 那笔在写日志前就中断、根本没动
            // 过盘。错配模式【绝不能】回放旧日志的动作：旧事务若已提交，它的
            // prev 残留（提交清理删失败）会被搬回去 = 把已提交的恢复悄悄回滚、
            // PreRestoreConfigJson 会把配置改回旧世界——只清 pending + 删陈旧
            // 日志，自愈到干净现场
            var txMismatch = state.PendingRestoreTxId is not null && journal.TxId is not null
                             && !string.Equals(state.PendingRestoreTxId, journal.TxId,
                                 StringComparison.OrdinalIgnoreCase);
            var committed = state.PendingRestoreTxId is null && !txMismatch; // 错配防御：永不按已提交清理
            if (txMismatch)
            {
                try
                {
                    state.PendingRestoreTxId = null;
                    state.Save(package);
                }
                catch (IOException)
                {
                    return false; // 清不掉：留日志下轮重试（幂等）
                }
                File.Delete(journalPath);
                return true;
            }
            if (committed)
            {
                foreach (var rel in journal.ActiveDiskRels)
                {
                    try
                    {
                        var activeAbs = System.IO.Path.Combine(package.Path,
                            rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                        DeleteIfExists(activeAbs + RestorePrevSuffix);
                    }
                    catch { /* 残留清理失败：无害 */ }
                }
                if (journal.Vars)
                    DeleteIfExists(System.IO.Path.Combine(package.FirmwarePath, "VARS.fd" + RestorePrevSuffix));
                File.Delete(journalPath);
                return true;
            }

            foreach (var rel in journal.ActiveDiskRels)
            {
                try
                {
                    var activeAbs = System.IO.Path.Combine(package.Path,
                        rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    var prev = activeAbs + RestorePrevSuffix;
                    var staged = activeAbs + StagedOverlaySuffix;
                    if (!File.Exists(prev))
                    {
                        // prev 不在 = 该盘没走到"旧盘暂存"那步。但暂存 overlay 可能
                        // 已经建出来了（CreateOverlay 成功后、Move 之前中断）：日志
                        // 在 + 入口修复保证无陈旧暂存 ⇒ 它必属本事务，是没提交的
                        // 换入半成品——留着会被"完成换入"分支补完 = torn 状态。
                        // active 也不在（工作盘被 Move 走了/本来就缺失）就保持
                        // 缺失——活动盘物理缺失另有救援路径处理
                        DeleteIfExists(staged);
                        continue;
                    }
                    if (File.Exists(activeAbs)) DeleteIfExists(activeAbs); // 新 overlay，丢弃
                    DeleteIfExists(staged);                               // 进行中的暂存
                    File.Move(prev, activeAbs);                           // 旧工作数据归位
                }
                catch (IOException)
                {
                    return false; // 单盘占用：整轮中止，下轮重来（不能删日志）
                }
            }
            // 逐步回滚；任何一步失败都必须留住日志（返回 false）——日志一删，
            // "配置/变量回到旧世界"的最后凭证就没了：新配置 + 旧磁盘的错配再也无法
            // 自愈，VARS 的旧世界唯一副本还会被随后的 prev 清扫当垃圾删掉
            var varsFailed = false;
            var configFailed = false;
            var pendingCleared = false;
            if (journal.Vars)
            {
                var varsAbs = System.IO.Path.Combine(package.FirmwarePath, "VARS.fd");
                var varsPrev = varsAbs + RestorePrevSuffix;
                if (File.Exists(varsPrev))
                {
                    try
                    {
                        if (File.Exists(varsAbs)) File.Delete(varsAbs);
                        File.Move(varsPrev, varsAbs);
                    }
                    catch (IOException) { varsFailed = true; /* 磁盘已归位，重试安全；日志留着重试 */ }
                }
            }
            // 配置一并还原（磁盘回到旧世界，配置也得回旧世界——否则崩溃在
            // ConfigStore.Save 与 state.Save 之间留下"新配置 + 旧磁盘"的错配）
            if (journal.PreRestoreConfigJson is not null)
            {
                try
                {
                    AtomicFile.WriteJsonValidated(
                        System.IO.Path.Combine(package.Path, "config.json"),
                        journal.PreRestoreConfigJson);
                }
                catch { configFailed = true; /* 日志保留，下轮重试 */ }
            }
            // pending 一并清除（事务以回滚完结）；清不掉则留日志下轮重试
            try
            {
                var st = VmState.Load(package);
                st.PendingRestoreTxId = null;
                st.Save(package);
                pendingCleared = true;
            }
            catch { }
            if (varsFailed || configFailed || !pendingCleared) return false;
            File.Delete(journalPath);
            return true;
        }
        catch
        {
            // 回滚整体失败：日志保留，下次再试
            return false;
        }
    }

    /// <summary>恢复期间旧工作盘的暂留后缀（全部盘换入成功后才删除；回滚时救回）。</summary>
    public const string RestorePrevSuffix = ".grass-restore-prev";

    private static bool PathEquals(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 <paramref name="from"/> 沿 backing 链向上走，判断会不会到达 <paramref name="target"/>。
    /// 把 X rebase 到 Y 之前必须确认 Y 的祖先链不包含 X——否则造出环形 backing 链
    ///（QEMU 拒绝打开，VM 无法启动）。跳数上限兜住已成的环与异常深的链。
    /// </summary>
    private static bool BackingChainReaches(GrassCore.Qemu.TransactionalDiskOps ops, string from, string target)
    {
        var cur = from;
        for (var hop = 0; hop < 64; hop++)
        {
            var next = ops.QueryBackingFile(cur);
            if (next is null) return false;
            if (PathEquals(next, target)) return true;
            cur = next;
        }
        return false;
    }

    private static string FreezeIntentPath(GrassVmPackage package, string uuid) =>
        System.IO.Path.Combine(package.SnapshotsPath, uuid, "freeze-intent.json");

    /// <summary>冻结意向：per 盘 rebase 目标（包相对路径；null = 链根/无父）。</summary>
    public sealed record FreezeIntent(Dictionary<string, string?> ParentsByDeviceId);

    private static void WriteFreezeIntent(GrassVmPackage package, string uuid, Dictionary<string, string?> intent)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(package.SnapshotsPath, uuid));
        AtomicFile.WriteJsonValidated(FreezeIntentPath(package, uuid),
            JsonSerializer.Serialize(new FreezeIntent(intent), JournalOpts));
    }

    private static FreezeIntent? LoadFreezeIntent(GrassVmPackage package, string uuid)
    {
        try
        {
            var p = FreezeIntentPath(package, uuid);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<FreezeIntent>(File.ReadAllText(p), JournalOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveIntent(GrassVmPackage package, Dictionary<string, string?> intent, string deviceId)
    {
        if (!intent.TryGetValue(deviceId, out var rel) || rel is null) return null;
        var abs = System.IO.Path.Combine(package.Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(abs) ? abs : null;
    }

    /// <summary>回滚救援中"数据已回工作路径、但相对 backing 还按冻结位置算（悬空）"的盘。
    /// repair 按 freeze-intent 重算这些盘的指针——没有这个标记，文件已离开快照目录，
    /// 任何修复逻辑都找不到它。</summary>
    public sealed record RescuePending(List<string> DeviceIds);

    private static string RescuePendingPath(GrassVmPackage package, string uuid) =>
        System.IO.Path.Combine(package.SnapshotsPath, uuid, "rescue-pending.json");

    private static void WriteRescuePending(GrassVmPackage package, string uuid, List<string> deviceIds)
    {
        try
        {
            var existing = File.Exists(RescuePendingPath(package, uuid))
                ? JsonSerializer.Deserialize<RescuePending>(File.ReadAllText(RescuePendingPath(package, uuid)), JournalOpts)
                : null;
            var all = (existing?.DeviceIds ?? new List<string>()).Union(deviceIds).ToList();
            Directory.CreateDirectory(System.IO.Path.Combine(package.SnapshotsPath, uuid));
            AtomicFile.WriteJsonValidated(RescuePendingPath(package, uuid),
                JsonSerializer.Serialize(new RescuePending(all), JournalOpts));
        }
        catch { /* 标记写不进去：rebase 失败本身已向上抛，修复兜底 */ }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>原子复制：先写 .grass-tmp 再改名（目标要么是旧内容要么是完整新内容）。</summary>
    private static void CopyAtomic(string from, string to)
    {
        var tmp = to + ".grass-tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }

    /// <summary>按 deviceId 在当前配置里找工作盘路径（找不到 = 设备已被移除，返回 null）。</summary>
    private static string? FindActivePathForDevice(GrassVmPackage package, string deviceId)
    {
        try
        {
            var config = new ConfigStore(package).Load();
            var disk = config.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId);
            return disk is null ? null : GrassCore.GrassVm.PathPolicy.Resolve(package, disk.Path);
        }
        catch
        {
            return null;
        }
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
        var dir = System.IO.Path.Combine(package.SnapshotsPath, uuid);
        var deleted = tree.All.FirstOrDefault(s =>
            string.Equals(s.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
        if (deleted is null)
        {
            // 上次删除中途失败后的【重试】：树里已无此快照（metadata 已清），
            // 直接 tree.Get 会抛裸 KeyNotFoundException——"稍后重删一次"的
            // 承诺就永远兑现不了。保守规则：目录里还有 qcow2 = 无法断定上次
            // 是否决定保留基座（链根/共享基座的后代还指着它）→ 只清非磁盘
            // 文件；没有 qcow2 → 整目录删除。宁可漏删（留无主文件占空间）
            // 也不能错删后代 overlay 的基座
            if (!Directory.Exists(dir)) throw new GrassCoreException("快照不存在。");
            try
            {
                var hasDisks = Directory.EnumerateFiles(dir, "*.qcow2", SearchOption.AllDirectories).Any();
                if (hasDisks)
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        var rel = System.IO.Path.GetRelativePath(dir, f);
                        if (rel != "disks" && !rel.StartsWith("disks" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            File.Delete(f);
                    }
                }
                else
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GrassCoreException("快照文件暂时被其他程序占用，清理未完成；稍后再重新删除一次即可。");
            }
            return;
        }
        var linkedClones = FindLinkedCloneReferences(package);
        var plan = SnapshotPlanner.PlanDelete(tree, uuid, linkedClones);
        var state = VmState.Load(package);
        // 与 SnapshotTree 同一大小写语义（OrdinalIgnoreCase）：位置判定若用 Ordinal，
        // 大小写变体 UUID 会被树当作"在位"删掉物理层、却漏掉工作 overlay 的 rebase——链悬空
        var positionWasHere = string.Equals(state.CurrentSnapshotUuid, uuid, StringComparison.OrdinalIgnoreCase);
        var parentUuid = deleted.ParentSnapshotUuid;

        // 任一设备落入"链根保留"分支 → 物理目录就不能整体删除（后代 overlay 还指着它）
        var anyDeviceKeptAsBase = false;
        if (diskOps is not null)
        {
            // 两阶段：先对【全部】设备完成依赖者/共享判定（此阶段不写任何文件——
            // 任何一张盘不合格立即抛，快照原样无损），再统一执行 commit/rebase。
            // 单循环交错执行的话，"第 1 张盘已 commit、第 2 张盘判定失败"会把父
            // 检查点污染了一半——重试又把已 commit 的盘当"无依赖者"直接丢弃，
            // 污染永远无法被发现或修复
            var devicePlan = new List<(string FrozenAbs, string ParentFrozen, List<string> Dependents)>();
            foreach (var (deviceId, frozenRel) in deleted.DiskOverlayRefs)
            {
                var frozenAbs = System.IO.Path.Combine(package.SnapshotsPath, uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!File.Exists(frozenAbs)) continue;
                // 父 UUID 可能指向已不存在的快照（其 metadata 损坏被 LoadTree 跳过）：
                // TryGet 解析不了就按链根处理（保留物理文件做基座）。tree.Get 在
                // 这里裸抛 KeyNotFoundException 会把这台 VM 的删除永久卡死
                string? parentFrozen = null;
                if (parentUuid is not null && tree.TryGet(parentUuid, out var parentNode))
                    parentFrozen = FrozenPathOrNull(package, parentNode!, deviceId);
                // 父没有这张盘（配置分歧/旧元数据）→ 按链根处理：保留物理文件，只动元数据

                // 物理依赖者 = backing 指向本层冻结文件的【任何】层：孩子的冻结文件 +
                // 活动 overlay（按物理指针判定——位置标记只反映"该挂哪"，不反映
                // "实际压在哪"；恢复到旧点后删"未来"快照时，活动盘根本不在这层上）
                var dependents = new List<string>();
                foreach (var childUuid in tree.ChildrenOf(uuid).Select(c => c.Uuid))
                {
                    var p = FrozenPathOrNull(package, tree.Get(childUuid), deviceId);
                    if (p is null || !File.Exists(p)) continue;
                    // 上一轮删除可能已把这个孩子 rebase 到父：物理改挂先于元数据改挂
                    //（逐个写 metadata），崩溃夹在中间 = 部分孩子"物理已就位、元数据
                    // 还挂在本层"。backing 已指向父 = 本事务上一轮自己的产物，不是
                    // 待办依赖者。不排除的话：它的兄弟（元数据未改挂）让设备照走
                    // commit+共享扫描，而这个孩子已被【另一份元数据】视作子树外
                    // 分支——扫描看到"外来层压着父基座"→ 永久误判"共享父"拒绝，
                    // 提示还会引导用户删掉一个想保留的检查点（重删承诺失效）
                    if (parentFrozen is not null && File.Exists(parentFrozen))
                    {
                        try
                        {
                            var cb = diskOps.QueryBackingFileStrict(p);
                            if (cb is not null && PathEquals(cb, parentFrozen))
                                continue; // 已被上一轮 rebase：只差元数据改挂（Rebindings 完成）
                        }
                        catch (GrassCore.Qemu.QemuImgException)
                        {
                            // 查询失败：保守计为依赖者（多一次幂等 rebase 无害）
                        }
                    }
                    dependents.Add(p);
                }
                string? activeBacking = null;
                var activeQueryFailed = false;
                {
                    var config = new ConfigStore(package).Load();
                    var diskNow = config.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == deviceId);
                    if (diskNow is not null)
                    {
                        var active = GrassCore.GrassVm.PathPolicy.Resolve(package, diskNow.Path);
                        if (File.Exists(active))
                        {
                            // 严格查询区分"查询失败"与"真的没有 backing"：宽松版把两者
                            // 都折叠成 null，查询失败（AV/索引器短暂锁文件）时若位置
                            // 标记又恰好不在本层（崩溃残留的分歧现场），活动盘会被漏判
                            // 成依赖者——无孩子 + 无依赖 = 物理基座被删，工作盘断链。
                            // 失败时保守计为依赖者：多一次幂等 rebase 无害，断链致命
                            try
                            {
                                activeBacking = diskOps.QueryBackingFileStrict(active);
                            }
                            catch (GrassCore.Qemu.QemuImgException)
                            {
                                activeQueryFailed = true;
                            }
                            if (activeBacking is not null && PathEquals(activeBacking, frozenAbs))
                                dependents.Add(active);
                            else if (activeQueryFailed)
                                // 查询失败：物理位置不可知——按最坏情形计为依赖者。
                                // 宁可多一次幂等 rebase，不可在"查询恰好失败"时
                                // 把可能压在本层的盘漏判成无依赖（无孩子时 = 物理基座
                                // 被直接删掉，工作盘从此断链）
                                dependents.Add(active);
                            else if (activeBacking is null && positionWasHere)
                            {
                                // 确认无 backing + 位置标记在本层：退回位置标记
                                //（保守——宁可多做一次幂等 rebase 也不漏 rebase 断链）
                                dependents.Add(active);
                            }
                        }
                    }
                    // 当前配置已没有该设备（增删盘后的分歧）：没有工作 overlay 需要 rebase
                }

                if (dependents.Count == 0)
                {
                    // 没有任何层指着它：这层的数据随删除【直接丢弃】。绝不 commit 进父——
                    // 父是"恢复点"（恢复语义承诺它分毫不差），也可能正被兄弟分支/当前
                    // 世界踩着；无依赖层的合并 = 静默改写别人的检查点
                    // 物理文件随目录删除（非根 + 非 keptAsBase 时 keepPhysical=false）
                    continue;
                }

                if (parentFrozen is not null && File.Exists(parentFrozen))
                {
                    // 合并合法性：commit 会改写父基座的内容。父基座若被【本事务之外】
                    // 的层引用（兄弟分支的冻结层、直接压在父上的活动盘），合并会污染
                    // 它们读到的数据——如实拒绝，绝不静默改写。
                    // 排除对象 = 被删节点的【全部后代】（不只它自己）：本事务正把
                    // 后代 rebase 到父上。阶段 2 中途失败后重删时，已 rebase 的孩子
                    // 物理上已坐在父基座上、元数据却仍挂在本子树（改挂在最后）——
                    // 不排除后代，重删会被自己上一轮的成果卡死（"共享"误判，永不成功）
                    var subtree = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uuid };
                    var stack = new Stack<string>();
                    stack.Push(uuid);
                    while (stack.Count > 0)
                    {
                        var cur = stack.Pop();
                        foreach (var c in tree.ChildrenOf(cur))
                            if (subtree.Add(c.Uuid))
                                stack.Push(c.Uuid);
                    }
                    var parentShared = tree.All.Any(s =>
                    {
                        if (subtree.Contains(s.Uuid)) return false; // 本子树（含已被 rebase 的后代）
                        // 父基座自己不可能"共享"自己：即便其元数据被指回自身
                        //（指针损坏/外物写入），也不能据此永远拒绝删除
                        if (parentUuid is not null
                            && string.Equals(s.Uuid, parentUuid, StringComparison.OrdinalIgnoreCase))
                            return false;
                        var f = FrozenPathOrNull(package, s, deviceId);
                        if (f is null || !File.Exists(f)) return false;
                        try
                        {
                            // 严格查询：失败≠"无 backing"。宽松版把 AV/索引器短暂
                            // 锁文件也折叠成"不共享"，真正共享父基座的兄弟分支会被
                            // 漏判——随后 commit 静默改写它脚下的检查点。失败按
                            // 【共享】拒绝删除：用户重试即可，改写无法撤销
                            var b = diskOps.QueryBackingFileStrict(f);
                            return b is not null
                                   && string.Equals(System.IO.Path.GetFullPath(b),
                                       System.IO.Path.GetFullPath(parentFrozen), StringComparison.OrdinalIgnoreCase);
                        }
                        catch (GrassCore.Qemu.QemuImgException) { return true; } // 查询失败：按共享拒绝
                    });
                    // 活动盘直接压在【父】上 = 子树外的共享者：合并会改写它脚下的基座。
                    // 例外：位置就在被删快照上（positionWasHere）——那是阶段 2 上一轮
                    // 已把它 rebase 到父的【本事务产物】（元数据位置未变，仍是判定依据）
                    if (activeBacking is not null && PathEquals(activeBacking, parentFrozen)
                        && !positionWasHere)
                        parentShared = true;
                    // 跨包共享者：基于【父】快照建立的链接克隆——它们的工作 overlay
                    // 直接压在父冻结文件上（另一个包里）。commit 改写父内容 = 悄悄
                    // 改写克隆脚下的基线，而 UI 的克隆警告只覆盖"删基线本身"这种
                    // 直接形态，这种"经由子层合并改写基线"的路径必须在这里拦下
                    if (parentUuid is not null && linkedClones.Any(lc =>
                            string.Equals(lc.ParentSnapshotUuid, parentUuid, StringComparison.OrdinalIgnoreCase)))
                        parentShared = true;
                    if (parentShared)
                        throw new GrassCoreException(
                            "无法删除此快照：合并会改写其父快照的磁盘内容，而父快照正被其他快照分支或链接克隆共享。" +
                            "请先删除/完整克隆共享父快照的其他分支与克隆，再删除此快照。");

                    devicePlan.Add((frozenAbs, parentFrozen, dependents));
                }
                else
                {
                    // 链根基座（无父）或父缺这张盘：物理文件必须保留（后代 overlay 的 backing），
                    // 只做元数据删除——目录级决策必须知道这件事
                    anyDeviceKeptAsBase = true;
                }
            }

            // 阶段 2：判定全部通过后才动文件。中途失败（qemu-img 错误/文件被占用）
            // 留下的是"部分已合并"的物理状态 + 未变的元数据——重删幂等（已合并的
            // 盘在新一轮里无依赖者→丢弃；未合并的盘重走判定）
            foreach (var (frozenAbs, parentFrozen, dependents) in devicePlan)
            {
                // 正常路径：commit + rebase
                // 被删层先并入父（客户机可见内容不变），后代改挂父
                diskOps.CommitOverlay(frozenAbs);
                foreach (var dep in dependents)
                    if (!string.Equals(dep, frozenAbs, StringComparison.OrdinalIgnoreCase))
                        diskOps.RebaseOverlay(dep, parentFrozen, relativeBacking: true);
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
        // 删除顺序不变式（崩溃/IO 失败后重删必须安全）：后代 overlay 的【物理
        // rebase】（上面的循环）先于【元数据改挂】完成——重试时 ChildrenOf 仍能
        // 算出未改挂的孩子（幂等 rebase），已改挂的孩子物理上也已不再指向本层。
        // 这里若因文件被占用（杀毒/索引器是常态）删不掉：树状态一致、链完好，
        // 把原始 IOException 翻译成"可重试"的明确提示，而不是裸抛
        if (Directory.Exists(dir))
        {
            var frozenFiles = Directory.EnumerateFiles(dir, "*.qcow2", SearchOption.AllDirectories).ToList();
            var keepPhysical = parentUuid is null || anyDeviceKeptAsBase
                || diskOps is null && frozenFiles.Count > 0;
            try
            {
                if (keepPhysical)
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        var rel = System.IO.Path.GetRelativePath(dir, f);
                        if (rel != "disks" && !rel.StartsWith("disks" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            File.Delete(f);
                    }
                }
                else
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GrassCoreException(
                    "快照已合并完成，但其文件暂时被其他程序占用，未能清理干净。稍后重新删除一次即可完成清理。" +
                    $"（{ex.Message}）");
            }
        }

        // 当前位置被删除 → 位置回到其父（保持"下一步快照挂哪"有确定答案）
        if (positionWasHere)
        {
            state.CurrentSnapshotUuid = parentUuid;
            state.Save(package);
        }
    }

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
