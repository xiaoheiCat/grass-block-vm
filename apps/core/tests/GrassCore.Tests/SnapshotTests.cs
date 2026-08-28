using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Profiles;
using GrassCore.Rpc;
using GrassCore.Snapshots;
using Xunit;

namespace GrassCore.Tests;

public class SnapshotTreeTests
{
    private static Snapshot Snap(string uuid, string? parent, string name = "", bool memory = false, bool protection = false) => new()
    {
        Uuid = uuid,
        ParentSnapshotUuid = parent,
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
        HasMemoryState = memory,
        FullConfigSnapshot = "{}",
        IsUpgradeProtection = protection,
        UpgradeProtectionCreatedAt = protection ? DateTimeOffset.UtcNow : null,
    };

    /// <summary>树状结构：安装完成 → [Office 测试 → Office 更新后, 显卡驱动测试 → 驱动 2.0]</summary>
    private static SnapshotTree BuildSampleTree()
    {
        var t1 = Snap("s1", null, "安装完成");
        var t2 = Snap("s2", "s1", "Office 测试");
        var t3 = Snap("s3", "s2", "Office 更新后", memory: true); // 运行中快照含内存状态
        var t4 = Snap("s4", "s1", "显卡驱动测试");
        var t5 = Snap("s5", "s4", "驱动 2.0");
        return new SnapshotTree(new[] { t1, t2, t3, t4, t5 });
    }

    [Fact]
    public void Tree_SupportsBranches()
    {
        var tree = BuildSampleTree();
        Assert.Single(tree.Root);                       // 安装完成是唯一根
        Assert.Equal(2, tree.ChildrenOf("s1").Count()); // 根下两个分支
        Assert.Single(tree.ChildrenOf("s2"));
    }

    [Fact]
    public void ChainToRoot_FollowsParents()
    {
        var tree = BuildSampleTree();
        Assert.Equal(new[] { "s3", "s2", "s1" }, tree.ChainToRoot("s3").Select(s => s.Uuid));
    }

    [Fact]
    public void DescendantsOf_IncludesWholeSubtree()
    {
        var tree = BuildSampleTree();
        Assert.Equal(new HashSet<string> { "s1", "s2", "s3", "s4", "s5" }, tree.DescendantsOf("s1").ToHashSet());
        Assert.Equal(new HashSet<string> { "s4", "s5" }, tree.DescendantsOf("s4").ToHashSet());
    }

    [Fact]
    public void DeleteNonLeaf_NoCloneDeps_PlansMergeRebinding()
    {
        // 删除"安装完成"（非叶子）：后代自动重挂到其父级（这里是根 → 各自成为新根）
        var tree = BuildSampleTree();
        var plan = SnapshotPlanner.PlanDelete(tree, "s1", linkedClones: Array.Empty<LinkedCloneReference>());

        Assert.False(plan.HasLinkedCloneDependencies);
        Assert.True(plan.RequiresMerge);
        Assert.Contains(("s2", "__root__"), plan.Rebindings);
        Assert.Contains(("s4", "__root__"), plan.Rebindings);
    }

    [Fact]
    public void DeleteNonLeaf_MiddleNode_RebindsChildrenToGrandparent()
    {
        var tree = BuildSampleTree();
        var plan = SnapshotPlanner.PlanDelete(tree, "s2", Array.Empty<LinkedCloneReference>());
        // 删除 s2：s3 重挂到 s1
        Assert.Equal(new[] { ("s3", "s1") }, plan.Rebindings);
    }

    [Fact]
    public void DeleteWithLinkedCloneDeps_WarnsButAllows()
    {
        var tree = BuildSampleTree();
        var clones = new[]
        {
            new LinkedCloneReference("Win11 Test.grassvm", "/vms/Win11 Test.grassvm", "s2"),
            new LinkedCloneReference("Office Sandbox.grassvm", "/vms/Office Sandbox.grassvm", "s2"),
        };
        var plan = SnapshotPlanner.PlanDelete(tree, "s2", clones);

        Assert.True(plan.HasLinkedCloneDependencies);
        Assert.Equal(2, plan.AffectedLinkedClones.Count);
        // 用户确认后仍可删除（不抛异常，不自动重定向链接克隆到别的基线）
        Assert.DoesNotContain(plan.Rebindings, r => r.ChildUuid == "clone");
    }

    [Fact]
    public void Restore_WarnsUnsnapshotedWorkLoss()
    {
        var tree = BuildSampleTree();
        var plan = SnapshotPlanner.PlanRestore(tree, "s2");
        Assert.Contains(plan.Warnings, w => w.Contains("永久丢失"));
        Assert.Contains(plan.Warnings, w => w.Contains("硬件配置"));
        Assert.Contains(plan.Warnings, w => w.Contains("包外"));
    }

    [Fact]
    public void UpgradeProtectionSnapshot_Kept24h_UntilFirstCleanShutdown()
    {
        var s = Snap("sp", null, protection: true);
        var created = (DateTimeOffset)s.UpgradeProtectionCreatedAt!;

        bool ShouldDeleteAt(DateTimeOffset now, bool clean)
            => SnapshotPlanner.ShouldDeleteUpgradeProtection(s, now, clean);

        // 23 小时 + 正常关机：不删
        Assert.False(ShouldDeleteAt(created.AddHours(23), true));
        // 25 小时 + 非正常关机（强制关机/崩溃/QEMU 异常退出）：不删
        Assert.False(ShouldDeleteAt(created.AddHours(25), false));
        // 25 小时 + 正常关机：删除
        Assert.True(ShouldDeleteAt(created.AddHours(25), true));
        // 用户创建普通快照不影响保护快照（IsUpgradeProtection 独立标记）
        var userSnap = Snap("su", null);
        Assert.False(SnapshotPlanner.ShouldDeleteUpgradeProtection(userSnap, created.AddHours(1), true));
    }

    [Fact]
    public void RunningSnapshot_HasMemoryState_ShutdownSnapshot_DoesNot()
    {
        var tree = BuildSampleTree();
        Assert.True(tree.Get("s3").HasMemoryState);
        Assert.False(tree.Get("s1").HasMemoryState);
    }
}

public class SnapshotServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));

    public SnapshotServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Create_StoresFullConfigCopy_AndOverlayRefsForInternalDisksOnly()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Snap VM");
        var config = GrassCore.Profiles.OsProfileLibrary.CreateDefaultConfig("windows-11", "Snap VM");
        config.Devices.Add(new GrassCore.Config.DiskDevice { Path = "disks/system.qcow2", CreatedOrder = 10 });
        // 包外磁盘（绝对路径；在当前 OS 上以真实绝对路径表达，保证 IsExternal 判定跨平台一致）
        var externalDisk = Path.Combine(_dir, "outside-data.qcow2");
        config.Devices.Add(new GrassCore.Config.DiskDevice { Path = externalDisk, CreatedOrder = 11 });

        var snap = GrassCore.Rpc.SnapshotService.Create(pkg, config, "安装完成");

        Assert.Single(snap.DiskOverlayRefs); // 包外磁盘不进入快照链
        var snapDir = Path.Combine(pkg.SnapshotsPath, snap.Uuid);
        Assert.Contains("config.json", Directory.EnumerateFiles(snapDir).Select(Path.GetFileName));
        // 每个快照保存完整 config 副本（不是差异）
        var stored = File.ReadAllText(Path.Combine(pkg.SnapshotsPath, snap.Uuid, "config.json"));
        Assert.Contains("windows-11", stored);
    }

    [Fact]
    public void Delete_RewritesChildrenParentLinks()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Chain VM");
        var config = GrassCore.Profiles.OsProfileLibrary.CreateDefaultConfig("ubuntu", "Chain VM");
        var s1 = GrassCore.Rpc.SnapshotService.Create(pkg, config, "one");
        var s2 = GrassCore.Rpc.SnapshotService.Create(pkg, config, "two");
        var s3 = GrassCore.Rpc.SnapshotService.Create(pkg, config, "three");

        var tree = GrassCore.Rpc.SnapshotService.LoadTree(pkg);
        Assert.Equal(s2.Uuid, tree.Get(s3.Uuid).ParentSnapshotUuid);

        // 删除中间节点 s2：s3 重挂到 s1
        GrassCore.Rpc.SnapshotService.Delete(pkg, s2.Uuid);
        var after = GrassCore.Rpc.SnapshotService.LoadTree(pkg);
        Assert.Equal(2, after.All.Count);
        Assert.Equal(s1.Uuid, after.Get(s3.Uuid).ParentSnapshotUuid);
    }

    [Fact]
    public void Restore_RollsBackFullConfig()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Restore VM");
        var config = GrassCore.Profiles.OsProfileLibrary.CreateDefaultConfig("ubuntu", "Restore VM");
        config.CpuCores = 4;
        var snap = GrassCore.Rpc.SnapshotService.Create(pkg, config, "4 核");

        // 之后改配置
        config.CpuCores = 8;
        new GrassCore.Config.ConfigStore(pkg).Save(config);

        // 恢复快照：硬件配置一起回滚
        GrassCore.Rpc.SnapshotService.Restore(pkg, snap.Uuid);
        var restored = new GrassCore.Config.ConfigStore(pkg).Load();
        Assert.Equal(4, restored.CpuCores);
    }

    [Fact]
    public void Create_AfterRestore_ParentsToRestoredPosition_NotLatestLeaf()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "位置机");
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "位置机");
        var s1 = SnapshotService.Create(pkg, config, "s1");
        SnapshotService.Create(pkg, config, "s2");
        SnapshotService.Create(pkg, config, "s3");

        // 恢复到 s1 后继续工作 → 新快照必须是 s1 的孩子（不是"最新叶 s3"）
        SnapshotService.Restore(pkg, s1.Uuid);
        var s4 = SnapshotService.Create(pkg, config, "恢复后的工作");

        var tree = SnapshotService.LoadTree(pkg);
        Assert.Equal(s1.Uuid, tree.Get(s4.Uuid).ParentSnapshotUuid);
        // 删除当前快照后位置回到其父
        SnapshotService.Delete(pkg, s4.Uuid);
        Assert.Equal(s1.Uuid, Config.VmState.Load(pkg).CurrentSnapshotUuid);
    }
}
