using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Profiles;
using GrassCore.Qemu;
using GrassCore.Rpc;
using GrassCore.Snapshots;
using System.Diagnostics;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// 快照数据冻结语义（外部 QCOW2 overlay 链）：
/// 创建 = 工作盘移入快照（冻结）+ 原路径开新 overlay；
/// 恢复 = 丢弃工作 overlay，在冻结点上开新 overlay；
/// 删除 = commit 并入父 + 后代 rebase（链根基座保留物理文件）。
/// </summary>
public class SnapshotFreezeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fakeImg;
    private readonly GrassVmPackage _pkg;

    public SnapshotFreezeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "grassvm-snapfreeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fakeImg = WriteFakeQemuImg();
        _pkg = GrassVmPackage.CreateNew(_dir, "冻结机");
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "冻结机");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 1024 * 1024, CreatedOrder = 10 });
        new ConfigStore(_pkg).Save(config);
        Directory.CreateDirectory(Path.Combine(_pkg.Path, "disks"));
        // 原始工作盘写入可识别内容（不是 qemu-img 产物：直接放哨兵字节）
        File.WriteAllBytes(Path.Combine(_pkg.Path, "disks/system.qcow2"), "BASE-V1"u8.ToArray());
    }

    private string WriteFakeQemuImg() => FakeQemuImg.Create(Path.Combine(_dir, "fakes"));

    private TransactionalDiskOps Ops => new(_fakeImg);

    private string ActiveDisk => Path.Combine(_pkg.Path, "disks/system.qcow2");

    private static string FrozenDisk(GrassVmPackage pkg, Snapshot snap)
        => Path.Combine(pkg.SnapshotsPath, snap.Uuid,
            snap.DiskOverlayRefs.Values.First().Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Create_FreezesBase_ThenWritesGoToNewOverlay()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);

        // 冻结点持有原始内容（时间点被固定）；原路径变成全新 overlay（写永远进不了冻结点）
        Assert.Equal("BASE-V1"u8.ToArray(), File.ReadAllBytes(FrozenDisk(_pkg, s1)));
        var newOverlay = File.ReadAllBytes(ActiveDisk);
        Assert.NotEqual("BASE-V1"u8.ToArray(), newOverlay); // 是假 qemu-img 的新 overlay，不是旧文件
    }

    [Fact]
    public void Restore_DiscardsUnsavedWork_AndReturnsToFrozenPoint()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);

        // 快照之后的写入（模拟客户机写工作 overlay）
        File.WriteAllBytes(ActiveDisk, "WRITES-AFTER-SNAPSHOT"u8.ToArray());

        SnapshotService.Restore(_pkg, s1.Uuid, Ops);

        // 工作 overlay 被重建（丢弃未快照写入），冻结点内容原封不动
        Assert.Equal("BASE-V1"u8.ToArray(), File.ReadAllBytes(FrozenDisk(_pkg, s1)));
        Assert.NotEqual("WRITES-AFTER-SNAPSHOT"u8.ToArray(), File.ReadAllBytes(ActiveDisk));
        // 工作位置回到 s1
        Assert.Equal(s1.Uuid, VmState.Load(_pkg).CurrentSnapshotUuid);
    }

    [Fact]
    public void Delete_Middle_CommitsIntoParent_AndRebasesPositionOverlay()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops); // 位置在 s3

        SnapshotService.Delete(_pkg, s2.Uuid, Ops);

        // 中间层删除：目录移除，树重绑（s3 的父变 s1），位置回到有效快照
        Assert.False(Directory.Exists(Path.Combine(_pkg.SnapshotsPath, s2.Uuid)));
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(s1.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
        // 链根基座 s1 与孙辈 s3 的冻结文件都还在
        Assert.True(File.Exists(FrozenDisk(_pkg, s1)));
        Assert.True(File.Exists(FrozenDisk(_pkg, s3)));

        // 链维护真发生了：commit(s2 冻结) + rebase(依赖者 → s1 冻结)都记录在案，
        // 且 rebase 的目标文件此刻真实存在（backing 可解析 = 链没断）。
        // 每个 overlay 只看【最后一条】rebase：历史条目的目标（如 s3 原来挂 s2）
        // 在后续删除中合法消失了，现役指针才是链的现状。
        var log = File.ReadAllText(_fakeImg + ".chain-ops.log");
        Assert.Contains("commit", log);
        Assert.Contains(s1.Uuid, log);
        var latestBackingByOverlay = new Dictionary<string, string>();
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Contains("rebase") && line.Contains("-b "))
            {
                var overlay = line.Trim().Split(' ')[^1].Trim();
                latestBackingByOverlay[overlay] = line.Split("-b ")[1].Split(' ')[0].Trim();
            }
        foreach (var (overlay, backing) in latestBackingByOverlay)
        {
            // backing 是相对引用（相对 overlay 自身目录）；按 overlay 位置解析后必须真实存在
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(overlay))!, backing));
            Assert.True(File.Exists(resolved), $"overlay {overlay} 的现役 backing 不存在：{backing}");
        }
    }

    [Fact]
    public void Delete_LeafWithPositionElsewhere_DiscardsWithoutCommittingIntoParent()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);

        // 恢复到 s1 后删"未来"快照 s2：活动盘不在 s2 上、s2 也没有孩子——
        // 它的数据应随删除直接【丢弃】，绝不 commit 进 s1（s1 是恢复点，
        // 承诺分毫不差；commit 会把 s2 时代的写入静默塞进 s1 的检查点）
        SnapshotService.Restore(_pkg, s1.Uuid, Ops);
        var s1BytesBefore = File.ReadAllBytes(FrozenDisk(_pkg, s1));

        SnapshotService.Delete(_pkg, s2.Uuid, Ops);

        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Single(tree.All); // 只剩 s1
        Assert.Equal(s1BytesBefore, File.ReadAllBytes(FrozenDisk(_pkg, s1))); // 检查点未被污染
        // 没有 commit 发生（丢弃 ≠ 合并）
        Assert.DoesNotContain("commit", File.ReadAllText(_fakeImg + ".chain-ops.log"));
        // 位置仍在 s1、活动盘链完好（还压在 s1 冻结点上）
        Assert.Equal(s1.Uuid, VmState.Load(_pkg).CurrentSnapshotUuid);
    }

    [Fact]
    public void Delete_WithSiblingBranchOnParent_DoesNotPolluteSharedBase()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        // 回到 s1 再建 s3 = s1 的另一个分支（s3 的冻结层压在 s1 的基座上）
        SnapshotService.Restore(_pkg, s1.Uuid, Ops);
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops);
        var baseBefore = File.ReadAllBytes(FrozenDisk(_pkg, s1));

        // 删 s2：它没有孩子、活动盘也在别处 → 丢弃即可。共享基座 s1 分毫不动
        SnapshotService.Delete(_pkg, s2.Uuid, Ops);

        Assert.Equal(baseBefore, File.ReadAllBytes(FrozenDisk(_pkg, s1)));
        Assert.DoesNotContain("commit", File.ReadAllText(_fakeImg + ".chain-ops.log"));
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(2, tree.All.Count); // s1 + s3
        Assert.Equal(s1.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Delete_MiddleWithSiblingBranch_RefusesSharedParentMerge()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops); // s2 的孩子
        // 回到 s1 再建 s2b = s1 的另一个分支：s1 的基座现在被两条链共享
        SnapshotService.Restore(_pkg, s1.Uuid, Ops);
        var s2b = SnapshotService.Create(_pkg, config, "sibling", diskOps: Ops);
        var baseBefore = File.ReadAllBytes(FrozenDisk(_pkg, s1));

        // 删 s2 需要把 s2 的数据 commit 进 s1 的基座——那会改写 s2b 脚下的内容。
        // 必须如实拒绝，而不是静默污染
        var ex = Assert.Throws<GrassCore.Rpc.GrassCoreException>(() => SnapshotService.Delete(_pkg, s2.Uuid, Ops));

        Assert.Contains("共享", ex.Message);
        Assert.Equal(baseBefore, File.ReadAllBytes(FrozenDisk(_pkg, s1)));
        Assert.DoesNotContain("commit", File.ReadAllText(_fakeImg + ".chain-ops.log"));
        // 树原样（s2 还在）：拒绝 = 无副作用
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(4, tree.All.Count);
        Assert.Equal(s2.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Delete_RetryAfterPartialRebase_CompletesInsteadOfFalseShared()
    {
        // 阶段 2 中途崩溃的形态：s2 已 commit 进 s1、孩子 s3 已 rebase 到 s1、
        // s4 还没动、元数据全部未改。重删必须完成——否则"已 rebase 的孩子
        // 物理上坐在父基座上"会被当成"父被共享"永远拒绝（重删承诺失效）
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        SnapshotService.Restore(_pkg, s2.Uuid, Ops);
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops); // s2 的孩子
        SnapshotService.Restore(_pkg, s2.Uuid, Ops);
        var s4 = SnapshotService.Create(_pkg, config, "s4", diskOps: Ops); // s2 的另一个孩子

        // 手工制造"阶段 2 走了一半"的物理状态（元数据不动）
        Ops.CommitOverlay(FrozenDisk(_pkg, s2));
        Ops.RebaseOverlay(FrozenDisk(_pkg, s3), FrozenDisk(_pkg, s1), relativeBacking: true);

        SnapshotService.Delete(_pkg, s2.Uuid, Ops); // 不抛"共享"误判

        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(3, tree.All.Count); // s1 + s3 + s4
        Assert.Equal(s1.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
        Assert.Equal(s1.Uuid, tree.Get(s4.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Delete_RetryAfterPartialRehang_CompletesInsteadOfFalseShared()
    {
        // 崩溃点更靠后的形态：物理已全部完成（s2 已 commit 进 s1、两个孩子 s3/s4
        // 都已 rebase 到 s1），元数据改挂只写了 s3、s4 仍挂在 s2 下。重删时 s4
        // 让设备照走共享扫描，而 s3 在"当前元数据"里已是子树外分支、物理又压在
        // s1 基座上——必须识别为本事务上一轮的产物，否则永久误判"共享父"拒绝，
        // 错误提示还会引导用户去删一个他们想保留的检查点
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        SnapshotService.Restore(_pkg, s2.Uuid, Ops);
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops); // s2 的孩子
        SnapshotService.Restore(_pkg, s2.Uuid, Ops);
        var s4 = SnapshotService.Create(_pkg, config, "s4", diskOps: Ops); // s2 的另一个孩子

        // 手工制造"改挂写了一半"：物理先全部完成
        Ops.CommitOverlay(FrozenDisk(_pkg, s2));
        Ops.RebaseOverlay(FrozenDisk(_pkg, s3), FrozenDisk(_pkg, s1), relativeBacking: true);
        Ops.RebaseOverlay(FrozenDisk(_pkg, s4), FrozenDisk(_pkg, s1), relativeBacking: true);
        // s3 的元数据已改挂到 s1（s4 未改——崩溃窗口）
        var tree0 = SnapshotService.LoadTree(_pkg);
        var s3node = tree0.Get(s3.Uuid);
        s3node.ParentSnapshotUuid = s1.Uuid;
        SnapshotService.WriteSnapshot(_pkg, s3node);

        SnapshotService.Delete(_pkg, s2.Uuid, Ops); // 不抛"共享"误判

        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(3, tree.All.Count); // s1 + s3 + s4
        Assert.Equal(s1.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
        Assert.Equal(s1.Uuid, tree.Get(s4.Uuid).ParentSnapshotUuid);
        // s2 的物理目录也完成了清理
        Assert.False(Directory.Exists(System.IO.Path.Combine(_pkg.SnapshotsPath, s2.Uuid)));
    }

    [Fact]
    public void Repair_AfterChainRootDelete_DoesNotCycleOrResurrect()
    {
        // 删除链根基座保留 disks/（metadata 与 freeze-intent 一并清掉）——
        // 修复绝不能把这份残留当"半创建现场"：那会把基座 unsafe-rebase 到
        // 它自己的后代上（环形 backing 链）并把已删快照复活成倒挂节点
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        SnapshotService.Delete(_pkg, s1.Uuid, Ops); // 链根基座：物理保留

        Assert.True(SnapshotService.RepairStagedOverlays(_pkg, Ops));

        // 基座仍是链尾（无 backing）——没有被改挂到自己的后代上
        Assert.Null(Ops.QueryBackingFile(FrozenDisk(_pkg, s1)));
        // 已删快照不被复活：树上只有 s2
        Assert.Single(SnapshotService.LoadTree(_pkg).All);
        // 链健康：新快照照常构建在当前位置上
        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops);
        Assert.Equal(s2.Uuid, SnapshotService.LoadTree(_pkg).Get(s3.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Delete_RootBase_KeepsPhysicalDisks_DescendantsStillWork()
    {
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);

        SnapshotService.Delete(_pkg, s1.Uuid, Ops); // 链根基座

        // 基座物理文件保留（后代 overlay 的 backing 仍指向它），元数据消失
        Assert.True(File.Exists(FrozenDisk(_pkg, s1)), "链根基座被物理删除——后代 overlay 的 backing 悬空");
        Assert.False(File.Exists(Path.Combine(_pkg.SnapshotsPath, s1.Uuid, "metadata.json")));
        Assert.True(File.Exists(FrozenDisk(_pkg, s2)));
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Null(tree.Get(s2.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Create_MidDiskFailure_RollsBackAllDisks_NoDataLoss()
    {
        // 双盘配置：第二张盘的数据在中途失败时正躺在冻结路径上——回滚必须先把它
        // 救回工作路径再删快照目录（否则该盘唯一副本被 Directory.Delete 抹掉）
        var config = new ConfigStore(_pkg).Load();
        config.Devices.Add(new DiskDevice
        {
            Path = "disks/data.qcow2", SizeBytes = 1024 * 1024,
            DeviceId = Guid.NewGuid().ToString(), CreatedOrder = 11,
        });
        new ConfigStore(_pkg).Save(config);
        var dataDisk = Path.Combine(_pkg.Path, "disks/data.qcow2");
        File.WriteAllBytes(dataDisk, "DATA-BASE"u8.ToArray());

        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops); // 首个快照：无父 rebase

        // 第二个快照走"冻结 + 位置校正 rebase"路径；注入 rebase 失败 = 中途 IO 错误
        // （marker 文件落在假件自己的目录里：进程级环境变量会串到并行的其他测试类）
        var failMarker = Path.Combine(_dir, "fakes", "fail-rebase.marker");
        File.WriteAllText(failMarker, "1");
        GrassCore.Qemu.QemuImgException? thrown = null;
        try
        {
            SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        }
        catch (GrassCore.Qemu.QemuImgException e)
        {
            thrown = e;
        }
        finally
        {
            File.Delete(failMarker);
        }
        Assert.NotNull(thrown);

        // 两张盘的工作文件都活着（内容 = 冻结层被救回），s2 目录不留尸，树上只有 s1
        Assert.True(File.Exists(ActiveDisk), "主盘工作文件丢失");
        Assert.True(File.Exists(dataDisk), "数据盘工作文件丢失——唯一副本被回滚删除");
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Single(tree.All);
        Assert.Equal(s1.Uuid, tree.All.First().Uuid);
    }

    [Fact]
    public void Repair_RescuePending_Marker_RewiresDanglingPointer()
    {
        // 现场：Create 回滚把数据救回了工作路径，但盘内容里的相对 backing 还按
        // （已删除的）快照位置解析——rescue-pending.json 记住了这台盘，freeze-intent
        // 记着正确的 rebase 目标。修复必须重接指针并清掉标记，否则这台 VM 永远
        // 启动不了（"找不到 backing 文件"），用户侧无解
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);

        var disk = config.Devices.OfType<DiskDevice>().First();
        var dirUuid = Guid.NewGuid().ToString(); // 模拟"半创建"目录（无 metadata.json）
        var dirPath = Path.Combine(_pkg.SnapshotsPath, dirUuid);
        Directory.CreateDirectory(Path.Combine(dirPath, "disks"));

        // freeze-intent：该盘本应 rebase 到 s1 的冻结点（包相对路径）
        var frozenRel = Path.GetRelativePath(_pkg.Path, FrozenDisk(_pkg, s1)).Replace('\\', '/');
        File.WriteAllText(Path.Combine(dirPath, "freeze-intent.json"),
            "{\"ParentsByDeviceId\":{\"" + disk.DeviceId + "\":\"" + frozenRel + "\"}}");
        // rescue-pending：数据已回工作路径、指针悬空
        File.WriteAllText(Path.Combine(dirPath, "rescue-pending.json"),
            "{\"DeviceIds\":[\"" + disk.DeviceId + "\"]}");

        // 悬空指针：工作盘最后一条 -b 指向不存在的快照位置（按工作路径解析必失败）
        var dangling = $"../snapshots/{Guid.NewGuid():N}/disks/disk-gone.qcow2";
        File.AppendAllText(ActiveDisk, $"rebase -f qcow2 -u -F qcow2 -b {dangling} {ActiveDisk}\n");
        Assert.Null(Ops.QueryBackingFile(ActiveDisk)); // 前置：指针确实悬空

        SnapshotService.RepairStagedOverlays(_pkg, Ops);

        var fixedBacking = Ops.QueryBackingFile(ActiveDisk);
        Assert.NotNull(fixedBacking);
        Assert.Equal(Path.GetFullPath(FrozenDisk(_pkg, s1)), Path.GetFullPath(fixedBacking));
        Assert.False(File.Exists(Path.Combine(dirPath, "rescue-pending.json")),
            "修复完成后标记必须清除（否则每轮重复 rebase）");
    }

    [Fact]
    public void Repair_HalfCreatedDir_IsAdoptedIntoTree_NotInvisibleLayer()
    {
        // 崩溃窗口：冻结完成 + 换入完成 + 校正 rebase 完成，但 metadata 没写。
        // 不收编 = 永久"不可见层"：下次 Create 找不到物理头属主（树凭空多根），
        // Delete 底层快照时把在用基座物理删掉。修复必须把它收编成树上快照
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var disk = config.Devices.OfType<DiskDevice>().First();
        var frozen1 = FrozenDisk(_pkg, s1);

        // 手工构造 s2 的"只差 metadata"现场（模拟 Create 在 ③校正后、收尾前崩溃）
        var uuid2 = Guid.NewGuid().ToString();
        var dir2 = Path.Combine(_pkg.SnapshotsPath, uuid2);
        var frozen2 = Path.Combine(dir2, "disks", $"disk-{disk.DeviceId}.qcow2");
        Directory.CreateDirectory(Path.GetDirectoryName(frozen2)!);
        File.Move(ActiveDisk, frozen2);                                  // 冻结
        Ops.RebaseOverlay(frozen2, frozen1, relativeBacking: true, unsafeMode: true); // 位置校正
        var staged = ActiveDisk + ".grass-overlay-staged";
        Ops.CreateOverlay(frozen2, staged, relativeBacking: true,
            deferBackingVirtualSize: 1024 * 1024);                        // 换入暂存
        File.Move(staged, ActiveDisk);                                   // 换入完成
        var frozenRel = Path.GetRelativePath(_pkg.Path, frozen1).Replace('\\', '/');
        File.WriteAllText(Path.Combine(dir2, "freeze-intent.json"),
            "{\"ParentsByDeviceId\":{\"" + disk.DeviceId + "\":\"" + frozenRel + "\"}}");

        SnapshotService.RepairStagedOverlays(_pkg, Ops);

        // 收编：树上可见、父挂到 s1、位置指向它
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(2, tree.All.Count);
        var adopted = tree.All.Single(s => s.Uuid == uuid2);
        Assert.Equal(s1.Uuid, adopted.ParentSnapshotUuid);
        Assert.Equal(uuid2, Config.VmState.Load(_pkg).CurrentSnapshotUuid);
        // 链仍闭合：active → 收编冻结点 → s1 冻结点
        var head = Ops.QueryBackingFile(ActiveDisk);
        Assert.NotNull(head);
        Assert.Equal(Path.GetFullPath(frozen2), Path.GetFullPath(head));
    }

    [Fact]
    public void Repair_HalfCreatedBaseSnapshot_IsAdoptedAsRoot()
    {
        // 首个快照创建到一半崩溃（冻结 + 换入完成、metadata 没写）：它的冻结层
        // 就是【链基座】——没有 backing 是合法形态，不能因此拒收（否则首个快照
        // 永远变成不可见层，"恢复的快照"承诺失效）
        var config = new ConfigStore(_pkg).Load();
        var disk = config.Devices.OfType<DiskDevice>().First();

        var uuid = Guid.NewGuid().ToString();
        var dir = Path.Combine(_pkg.SnapshotsPath, uuid);
        var frozen = Path.Combine(dir, "disks", $"disk-{disk.DeviceId}.qcow2");
        Directory.CreateDirectory(Path.GetDirectoryName(frozen)!);
        File.Move(ActiveDisk, frozen);   // 冻结（这个文件就是基座：没有 backing）
        var staged = ActiveDisk + ".grass-overlay-staged";
        Ops.CreateOverlay(frozen, staged, relativeBacking: true,
            deferBackingVirtualSize: 1024 * 1024);
        File.Move(staged, ActiveDisk);   // 换入完成
        File.WriteAllText(Path.Combine(dir, "freeze-intent.json"),
            "{\"ParentsByDeviceId\":{}}");

        SnapshotService.RepairStagedOverlays(_pkg, Ops);

        // 收编为树根（无父），位置指向它，链闭合：active → 基座冻结点
        var tree = SnapshotService.LoadTree(_pkg);
        var adopted = Assert.Single(tree.All);
        Assert.Equal(uuid, adopted.Uuid);
        Assert.Null(adopted.ParentSnapshotUuid);
        Assert.Equal(uuid, Config.VmState.Load(_pkg).CurrentSnapshotUuid);
        var head = Ops.QueryBackingFile(ActiveDisk);
        Assert.NotNull(head);
        Assert.Equal(Path.GetFullPath(frozen), Path.GetFullPath(head));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { File.Delete(_fakeImg); } catch { }
    }

    [Fact]
    public void Delete_WithoutDiskOps_KeepsPhysicalFrozenFiles()
    {
        // 升级保护自动删除曾以无 diskOps 调 Delete：物理目录被无条件删除 → 链断 + 数据丢失。
        // 兜底语义：没有链维护工具时只删元数据，冻结文件一个都不许动。
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        var s2 = SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);

        SnapshotService.Delete(_pkg, s1.Uuid); // 注意：不传 diskOps

        // 元数据消失（树不再显示），物理冻结文件保留（工作 overlay 的 backing 仍可解析）
        Assert.False(File.Exists(Path.Combine(_pkg.SnapshotsPath, s1.Uuid, "metadata.json")));
        Assert.True(File.Exists(FrozenDisk(_pkg, s1)), "无 diskOps 的删除把冻结文件物理删除了——链断/数据丢失");
        Assert.True(File.Exists(FrozenDisk(_pkg, s2)));
    }

    [Fact]
    public void RepairStagedOverlays_CompletesInterruptedSwap()
    {
        // 崩溃窗口：新 overlay 已生成（暂存名）、工作盘已被冻结移走 → 工作路径缺失。
        // 修复 = 把暂存换入工作路径（幂等）。
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);

        // 人为复现"换入中断"：工作盘改名回暂存
        var active = ActiveDisk;
        File.Move(active, active + SnapshotService.StagedOverlaySuffix);
        Assert.False(File.Exists(active));

        SnapshotService.RepairStagedOverlays(_pkg);

        Assert.True(File.Exists(active));
        Assert.False(File.Exists(active + SnapshotService.StagedOverlaySuffix));
        Assert.True(File.Exists(FrozenDisk(_pkg, s1))); // 冻结点不受影响
    }

    [Fact]
    public void Create_WithDanglingMarker_ParentsByPhysicalBacking_NotMetadata()
    {
        // 分支树：物理链头 ≠ 最新叶。s1→s2 后恢复到 s1（物理头回到 s1 的冻结点，
        // s2 仍是树上的叶）。再把位置标记打成悬空（复现崩溃窗口）。这时元数据兜底
        // （最新叶）会选 s2，物理事实（工作盘实际 backing）是 s1——必须选 s1，
        // 否则 s1 之后新写的数据层会被 unsafe rebase 旁路掉（静默丢失）。
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);
        SnapshotService.Restore(_pkg, s1.Uuid, Ops); // 位置 = s1；树的最新叶仍是 s2

        var broken = VmState.Load(_pkg);
        broken.CurrentSnapshotUuid = "crash-window-dangling-uuid";
        broken.Save(_pkg);

        var s3 = SnapshotService.Create(_pkg, config, "s3", diskOps: Ops);

        // 树父 = 物理链头的属主（s1），不是"最新叶"兜底（s2）也不是悬空标记
        var tree = SnapshotService.LoadTree(_pkg);
        Assert.Equal(s1.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);
        // 物理链没断：s3 冻结文件的现役 backing 解析后 = s1 的冻结文件
        var content = File.ReadAllText(FrozenDisk(_pkg, s3));
        var lastB = content.Split("-b ", StringSplitOptions.RemoveEmptyEntries)[^1].Split(' ')[0].Trim();
        var resolved = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(FrozenDisk(_pkg, s3))!, lastB));
        Assert.True(File.Exists(resolved), $"s3 冻结盘 backing 悬空：{lastB}");
        Assert.Equal(
            Path.GetFullPath(FrozenDisk(_pkg, s1)),
            resolved,
            ignoreCase: true);
    }

    [Fact]
    public void Repair_AfterCrashedMultiDiskRestore_RollsBackWholeTransaction()
    {
        // 崩溃窗口：盘 1 已换入（active = 新 overlay、prev 在场）、盘 2 还没动
        // （active = 快照后的新写入），日志在场（state.Save 之前）。修复必须把
        // 【整个事务】回滚：盘 1 的 prev 归位、盘 2 原样保留——否则多盘客户机
        // 一半在快照时间点、一半在当前时间点（穿越的文件系统 = 静默损坏）。
        var config = new ConfigStore(_pkg).Load();
        config.Devices.Add(new DiskDevice
        {
            Path = "disks/data.qcow2", SizeBytes = 1024 * 1024,
            DeviceId = Guid.NewGuid().ToString(), CreatedOrder = 11,
        });
        new ConfigStore(_pkg).Save(config);
        var main = ActiveDisk;
        var data = Path.Combine(_pkg.Path, "disks/data.qcow2");
        File.WriteAllBytes(data, "DATA-BASE"u8.ToArray());

        // 两个快照：位置在 s2，崩溃的恢复目标是 s1（标记 ≠ 目标 = 未提交；
        // 若目标与当前位置相同，修复的"已提交"判定会正当地选择不回滚）
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        SnapshotService.Create(_pkg, config, "s2", diskOps: Ops);

        // 人为复现"Restore 到 s1 的中途崩溃"：日志在场 + 盘 1 完成换入（prev 暂留）。
        // pending 与日志配对（未提交的判定依据——只看"位置==目标"在恢复到当前位置
        // 时无法区分）
        var st0 = VmState.Load(_pkg);
        st0.PendingRestoreTxId = "tx-crash-1";
        st0.Save(_pkg);
        var journalPath = Path.Combine(_pkg.SnapshotsPath, "restore-journal.json");
        File.WriteAllText(journalPath,
            $$"""{"targetSnapshotUuid":"{{s1.Uuid}}","activeDiskRels":["disks/system.qcow2","disks/data.qcow2"],"vars":true,"txId":"tx-crash-1"}""");
        var mainPrev = main + SnapshotService.RestorePrevSuffix;
        Ops.CreateOverlay(
            Path.Combine(_pkg.SnapshotsPath, s1.Uuid,
                s1.DiskOverlayRefs.Values.First().Replace('/', Path.DirectorySeparatorChar)),
            main + SnapshotService.StagedOverlaySuffix,
            relativeBacking: true);
        File.Move(main, mainPrev);                                    // ② 旧工作盘 → prev
        File.Move(main + SnapshotService.StagedOverlaySuffix, main);  // ③ 暂存 → active
        File.WriteAllBytes(data, "WRITES-AFTER-SNAPSHOT"u8.ToArray()); // 盘 2 的未快照写入
        // VARS 也走到一半：旧变量已暂存为 prev（日志 vars:true 声明要回滚它）
        Directory.CreateDirectory(_pkg.FirmwarePath);
        var varsActive = Path.Combine(_pkg.FirmwarePath, "VARS.fd");
        var varsPrev = varsActive + SnapshotService.RestorePrevSuffix;
        File.WriteAllBytes(varsActive, "VARS-NEW-WORLD"u8.ToArray());
        File.WriteAllBytes(varsPrev, "VARS-OLD-WORLD"u8.ToArray());

        var positionBefore = VmState.Load(_pkg).CurrentSnapshotUuid;

        SnapshotService.RepairStagedOverlays(_pkg);

        // 盘 1：prev 归位（旧工作数据回来），换入的新 overlay 被丢弃
        Assert.True(File.Exists(main));
        Assert.False(File.Exists(mainPrev), "prev 被删——回滚方向永久丢失");
        // 盘 2：未被事务触碰（快照后的写入原样保留）
        Assert.Equal("WRITES-AFTER-SNAPSHOT"u8.ToArray(), File.ReadAllBytes(data));
        // VARS：同样回到旧世界
        Assert.Equal("VARS-OLD-WORLD"u8.ToArray(), File.ReadAllBytes(varsActive));
        Assert.False(File.Exists(varsPrev));
        // 元数据世界未变（事务未提交），日志清理
        Assert.Equal(positionBefore, VmState.Load(_pkg).CurrentSnapshotUuid);
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void Repair_AfterCommittedRestoreCrash_KeepsDisksAtRestoredPoint()
    {
        // 崩溃窗口在 state.Save 之后、日志删除之前：位置标记已指向目标快照 =
        // 事务已提交。修复绝不能回滚磁盘（否则元数据说"已恢复"、磁盘却是恢复前
        // 数据——用户以为回到快照点，实际跑的是未保存写入），只清理 prev 残留
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        SnapshotService.Restore(_pkg, s1.Uuid, Ops); // 正常完成（位置 = s1，无 prev 残留）

        // 人为复现：日志又出现在场（Save 后、删除前崩溃）+ prev 残留在场
        var journalPath = Path.Combine(_pkg.SnapshotsPath, "restore-journal.json");
        File.WriteAllText(journalPath,
            $$"""{"targetSnapshotUuid":"{{s1.Uuid}}","activeDiskRels":["disks/system.qcow2"],"vars":false}""");
        var mainPrev = ActiveDisk + SnapshotService.RestorePrevSuffix;
        File.Copy(ActiveDisk, mainPrev); // 假装 prev 还没来得及删

        var activeContentBefore = File.ReadAllBytes(ActiveDisk);
        SnapshotService.RepairStagedOverlays(_pkg);

        // 已提交：active 保持换入后的内容（不回滚），prev 按残留垃圾清理，日志删除
        Assert.Equal(activeContentBefore, File.ReadAllBytes(ActiveDisk));
        Assert.False(File.Exists(mainPrev));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void Repair_StaleJournalWithMismatchedPending_SelfHealsWithoutCommittingCleanup()
    {
        // 意向（state.PendingRestoreTxId）先于日志落盘——两者之间的崩溃窗口留下
        // "陈旧日志（上一笔）+ 没动过盘的新 pending"。修复必须自愈：对旧日志的
        // 回滚是无操作，随后清 pending、删陈旧日志；绝不能因配对缺失走"已提交
        // 清理"（那会把可能属于新事务的 prev 当残留删掉）
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);
        SnapshotService.Restore(_pkg, s1.Uuid, Ops);

        var journalPath = Path.Combine(_pkg.SnapshotsPath, "restore-journal.json");
        // 陈旧日志带上一笔的 PreRestoreConfigJson：错配处置绝不能回放它
        //（那会把已提交恢复换上的配置悄悄改回旧世界）
        var currentConfig = File.ReadAllText(Path.Combine(_pkg.Path, "config.json"));
        File.WriteAllText(journalPath,
            "{\"targetSnapshotUuid\":\"" + s1.Uuid +
            "\",\"activeDiskRels\":[\"disks/system.qcow2\"],\"vars\":false,\"txId\":\"stale-tx\"," +
            "\"preRestoreConfigJson\":\"{\\\"name\\\":\\\"OLD-WORLD\\\"}\"}");
        var st = GrassCore.Config.VmState.Load(_pkg);
        st.PendingRestoreTxId = "newer-tx";
        st.Save(_pkg);
        var activeBefore = File.ReadAllBytes(ActiveDisk);

        Assert.True(SnapshotService.RepairStagedOverlays(_pkg, Ops));

        Assert.Equal(activeBefore, File.ReadAllBytes(ActiveDisk)); // 盘没被碰
        Assert.False(File.Exists(journalPath));                    // 陈旧日志已清
        Assert.Null(GrassCore.Config.VmState.Load(_pkg).PendingRestoreTxId); // pending 已清
        Assert.Equal(currentConfig, File.ReadAllText(Path.Combine(_pkg.Path, "config.json"))); // 配置未被旧日志回放
    }

    [Fact]
    public void Repair_HalfCreatedSnapshot_RescuesFrozenWhenActiveMissing()
    {
        // 回滚救援失败留下的极端现场：冻结文件躺在无元数据的快照目录里，
        // 对应工作盘缺失且无暂存/prev——那是该盘唯一副本，修复必须救回工作路径
        var config = new ConfigStore(_pkg).Load();
        var s1 = SnapshotService.Create(_pkg, config, "s1", diskOps: Ops);

        // 人为复现：半创建目录（无 metadata.json）+ 冻结文件在场 + 工作盘缺失。
        // 真实的创建中断现场必有 freeze-intent.json（意向先于任何盘操作落盘）——
        // 修复用它区分"中断现场"与"链根基座删除残留"（后者 disks/ 保留但
        // intent 已被清，绝不能再当半创建处理）
        var halfDir = Path.Combine(_pkg.SnapshotsPath, "half-created-uuid");
        var deviceId = config.Devices.OfType<GrassCore.Config.DiskDevice>().First().DeviceId;
        var frozenInHalf = Path.Combine(halfDir, "disks", $"disk-{deviceId}.qcow2");
        Directory.CreateDirectory(Path.GetDirectoryName(frozenInHalf)!);
        File.WriteAllText(Path.Combine(halfDir, "freeze-intent.json"),
            "{\"ParentsByDeviceId\":{\"" + deviceId + "\":\"disks/system.qcow2\"}}");
        File.Move(FrozenDisk(_pkg, s1), frozenInHalf); // 把 s1 的冻结文件搬进去当"唯一副本"
        // 让位置快照的引用指向一个存在的父（s1 的引用还挂在 config 里）
        File.Delete(ActiveDisk);

        SnapshotService.RepairStagedOverlays(_pkg, Ops);

        Assert.True(File.Exists(ActiveDisk), "唯一数据副本没有被救回工作路径");
        Assert.False(File.Exists(frozenInHalf));
    }
}
