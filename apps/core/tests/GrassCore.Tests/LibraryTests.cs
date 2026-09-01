using GrassCore.GrassVm;
using GrassCore.Library;
using GrassCore.Qemu;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

public class PathPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));

    public PathPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void InPackageChoice_IsStoredAsRelative()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Path VM");
        // 用户即使选择了绝对路径，检测到位于包内 → 自动改写为相对
        var abs = Path.Combine(pkg.DisksPath, "system.qcow2");
        Assert.Equal("disks/system.qcow2", PathPolicy.NormalizeReference(pkg, abs));
    }

    [Fact]
    public void ExternalChoice_IsStoredAsAbsolute()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Path VM2");
        var external = Path.Combine(_dir, "outside.qcow2");
        Assert.Equal(external, PathPolicy.NormalizeReference(pkg, external));
    }

    [Fact]
    public void Resolve_InPackageRelative_PackageExternalAbsolute()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Path VM3");
        Assert.Equal(Path.Combine(pkg.Path, "disks", "d.qcow2"), PathPolicy.Resolve(pkg, "disks/d.qcow2"));
        Assert.Equal("/mnt/data/d.qcow2", PathPolicy.Resolve(pkg, "/mnt/data/d.qcow2"));
    }

    [Fact]
    public void Resolve_RejectsRelativePathEscapingPackage()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Path VM4");

        Assert.False(PathPolicy.IsInsidePackage(pkg, "../outside.qcow2"));
        Assert.Throws<ArgumentException>(() => PathPolicy.Resolve(pkg, "../outside.qcow2"));
    }

    [Fact]
    public void Relocation_StillValid_WhenPathExists()
    {
        var f = Path.Combine(_dir, "here.vmdk");
        File.WriteAllText(f, "x");
        var r = ResourceRelocator.Relocate(f, "here.vmdk", new[] { Path.Combine(_dir, "there.vmdk") });
        Assert.Equal(RelocationOutcome.StillValid, r.Outcome);
    }

    [Fact]
    public void Relocation_UniqueMatch_AutoMigrates()
    {
        var f = Path.Combine(_dir, "gone.vmdk"); // 不存在
        var moved = Path.Combine(_dir, "search", "gone.vmdk");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        var r = ResourceRelocator.Relocate(f, "gone.vmdk", new[] { moved });
        Assert.Equal(RelocationOutcome.AutoRelocated, r.Outcome);
        Assert.Equal(moved, r.NewPath);
    }

    [Fact]
    public void Relocation_MultipleCandidates_NeedsUserDecision()
    {
        var f = Path.Combine(_dir, "gone.vmdk");
        var c1 = Path.Combine(_dir, "a", "gone.vmdk");
        var c2 = Path.Combine(_dir, "b", "gone.vmdk");
        var r = ResourceRelocator.Relocate(f, "gone.vmdk", new[] { c1, c2 });
        Assert.Equal(RelocationOutcome.NeedsUserDecision, r.Outcome);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Relocation_NoCandidates_Missing()
    {
        var f = Path.Combine(_dir, "gone.vmdk");
        var r = ResourceRelocator.Relocate(f, "gone.vmdk", Array.Empty<string>());
        Assert.Equal(RelocationOutcome.Missing, r.Outcome);
    }
}

public class LibraryAndHostDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryAndHostDbTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string DbPath => Path.Combine(_dir, "grass.db");

    [Fact]
    public void ScanLibrary_FindsGrassVmPackages()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        GrassVmPackage.CreateNew(root, "Windows 11");
        GrassVmPackage.CreateNew(root, "Ubuntu");
        Directory.CreateDirectory(Path.Combine(root, "not-a-vm"));

        var found = GrassVmPackage.ScanLibraryRoot(root).ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.EndsWith(GrassVmPackage.Extension, p.Path));
    }

    [Fact]
    public void Preferences_Roundtrip()
    {
        using var db = new HostDb(DbPath);
        db.SetPreference("updateChannel", "github-releases");
        Assert.Equal("github-releases", db.GetPreference("updateChannel"));
    }

    [Fact]
    public void DefaultHostOnlyNetwork_CreatedOnce_WithDhcp()
    {
        using var db = new HostDb(DbPath);
        var nets = db.ListNetworks();
        var def = Assert.Single(nets, n => n.IsDefault);
        Assert.True(def.DhcpEnabled); // 自带用户态 DHCP，不依赖 Windows ICS
        Assert.Matches(@"\d+\.\d+\.\d+\.0/24", def.Subnet);
    }

    [Fact]
    public void ChangeLibraryRoot_OnlyAffectsFuture_DoesNotMigrateOldVms()
    {
        using var db = new HostDb(DbPath);
        var root1 = Path.Combine(_dir, "root1");
        var root2 = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);
        GrassVmPackage.CreateNew(root1, "Old VM");

        db.SetPreference("libraryRoot", root1);
        db.RebuildIndexCache(root1);
        Assert.Single(db.GetIndexCache());

        // 更改存档位置只影响之后创建/导入；旧目录 VM 不迁移、不再出现在主界面
        db.ChangeLibraryRoot(root2);
        Assert.Equal(Path.GetFullPath(root2), db.LibraryRoot);
        Assert.Empty(db.GetIndexCache()); // 缓存针对新 root 重建前为空
        db.RebuildIndexCache(root2);
        Assert.Empty(db.GetIndexCache());
    }

    [Fact]
    public void Autostart_OrderAndInterval()
    {
        using var db = new HostDb(DbPath);
        var a = Path.Combine(_dir, "a.grassvm");
        var b = Path.Combine(_dir, "b.grassvm");
        var c = Path.Combine(_dir, "c.grassvm");
        db.SetAutostart(a, true);
        db.SetAutostart(b, true);
        db.SetAutostart(c, false);

        // 自动启动间隔默认 10 秒，可在全局设置调整（0–60）
        Assert.Equal(10, db.AutostartIntervalSeconds);
        db.AutostartIntervalSeconds = 65;
        Assert.Equal(60, db.AutostartIntervalSeconds); // clamp

        // 用户拖拽排序：GrassCore 严格按此顺序启动
        db.SetAutostartOrder(new[] { c, b, a });
        var order = db.GetAutostartList();
        Assert.Equal(new[] { c, b, a }, order.Select(e => e.VmPath));
        Assert.False(order.First(e => e.VmPath == c).Enabled); // 宿主级属性，与 VM 包无关
    }

    [Fact]
    public void SuggestFreeSubnet_AvoidsConflicts()
    {
        var suggested = HostDb.SuggestFreeSubnet(new[] { "192.168.16.0/24", "192.168.24.0/24" });
        // 建议的网段不能与已有网段冲突
        Assert.StartsWith("192.168.", suggested);
        Assert.NotEqual("192.168.16.0/24", suggested);
        Assert.NotEqual("192.168.24.0/24", suggested);
    }

    [Fact]
    public void CoreCrashRecovery_DetectsLockedVms_AndValidatesSession()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        var pkg = GrassVmPackage.CreateNew(root, "Crash VM");
        new VmLock(pkg).Acquire();
        Directory.CreateDirectory(pkg.RuntimePath);
        File.WriteAllText(pkg.SessionPath, new RuntimeSession
        {
            SessionId = "sid1", QmpPipe = @"\\.\pipe\grassvm-qmp-sid1",
            StartedAt = DateTimeOffset.UtcNow, QemuPid = 4242,
        }.Serialize());

        var recovery = new CoreCrashRecovery(isProcessAlive: pid => pid == 4242);
        var results = recovery.ScanAdoptable(root);

        var r = Assert.Single(results);
        Assert.True(r.SessionValid);
        Assert.True(r.QemuAlive);
        Assert.Equal(4242, r.Session.QemuPid);

        // PID 已死 → 会话无效，不自动清锁，留给用户诊断
        var dead = new CoreCrashRecovery(isProcessAlive: _ => false);
        Assert.False(dead.ScanAdoptable(root).Single().SessionValid);
    }

    [Fact]
    public void CoreCrashRecovery_RejectsForeignMachineSession_WhenValidatorProvided()
    {
        var root = Path.Combine(_dir, "root-machine");
        Directory.CreateDirectory(root);
        var pkg = GrassVmPackage.CreateNew(root, "Foreign VM");
        new VmLock(pkg).Acquire();
        Directory.CreateDirectory(pkg.RuntimePath);
        File.WriteAllText(pkg.SessionPath, new RuntimeSession
        {
            SessionId = "foreign", QmpPipe = @"\\.\pipe\grassvm-qmp-foreign",
            StartedAt = DateTimeOffset.UtcNow, QemuPid = 4242, MachineId = "other-machine",
        }.Serialize());

        var recovery = new CoreCrashRecovery(
            isProcessAlive: pid => pid == 4242,
            sessionValidator: s => s.MachineId == "this-machine");

        var result = Assert.Single(recovery.ScanAdoptable(root));
        Assert.True(result.QemuAlive);
        Assert.False(result.SessionValid);
    }

    [Fact]
    public void IntegrityCheck_MissingDirsAreSafeRepairs_UnknownFilesUntouched()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        var pkgPath = Path.Combine(root, "Broken.grassvm");
        Directory.CreateDirectory(pkgPath);
        File.WriteAllText(Path.Combine(pkgPath, "config.json"), "{}");
        File.WriteAllText(Path.Combine(pkgPath, "user-notes.txt"), "keep me"); // 未知文件：保留
        var pkg = new GrassVmPackage(pkgPath);

        var report = pkg.CheckIntegrity();
        Assert.False(report.IsFatal);
        Assert.Contains(report.SafeRepairs, r => r.Description.Contains("disks"));
        report.ApplySafeRepairs(); // 保守修复：只自动修确定无损的问题
        Assert.True(Directory.Exists(pkg.DisksPath));
        Assert.True(File.Exists(Path.Combine(pkgPath, "user-notes.txt"))); // 绝不主动删除未知文件
    }

    [Fact]
    public void LogRedactor_HidesUserInfo_KeepsDiagnostics()
    {
        var log = """
            GrassCore 0.1.0 startup
            VM package: C:\Users\Anna\Documents\Grass Block VM\工作电脑.grassvm
            External disk: E:\VMData\data.qcow2
            QEMU 11.1.1 WHPX init failed: error 0x80070002
            CPU: Intel(R) Core(TM) i7-13700K
            at GrassCore.Qemu.WhpxCapability.CheckWindows()
            """;
        var redacted = LogRedactor.Redact(log, new Dictionary<string, string> { ["工作电脑"] = "" });

        Assert.DoesNotContain("Anna", redacted);
        Assert.DoesNotContain("工作电脑", redacted); // VM 名称脱敏
        Assert.DoesNotContain(@"E:\VMData\data.qcow2", redacted); // 外部磁盘路径脱敏
        Assert.Contains("QEMU 11.1.1", redacted); // 诊断信息保留
        Assert.Contains("0x80070002", redacted);
        Assert.Contains("i7-13700K", redacted);
        Assert.Contains("WhpxCapability", redacted);
    }
}
