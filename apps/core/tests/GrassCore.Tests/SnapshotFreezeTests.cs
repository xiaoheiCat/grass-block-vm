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

    private static string WriteFakeQemuImg()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此测试当前仅在 POSIX CI 上运行假 qemu-img。");
        var sh = Path.Combine(Path.GetTempPath(), "fake-qemu-img-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(sh, """
            #!/bin/sh
            case "$1" in commit|rebase) exit 0;; esac
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            printf 'QFI\373' > "$target"
            printf '%s\n' "$*" >> "$target"
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }

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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { File.Delete(_fakeImg); } catch { }
    }
}
