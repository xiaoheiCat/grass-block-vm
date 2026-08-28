using System.Diagnostics;
using GrassCore.Library;
using GrassCore.Qemu;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// GrassCoreService 级别的挂起/恢复集成测试（假 QEMU 进程 + 假 QMP 服务 + 假 qemu-img）。
/// 验证：startVm → powerAction(suspend) 的 stop/migrate/quit 序列、suspend.state 与指纹落盘、
/// vm.lock 释放、resumeVm 的 -incoming 参数与状态清理。
/// </summary>
public class SuspendResumeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));

    public SuspendResumeServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class RecordingLauncher : IQemuProcessLauncher
    {
        public List<QemuCommandLine> Commands { get; } = new();
        public Process Start(QemuCommandLine cmd)
        {
            lock (Commands) Commands.Add(cmd);
            // 假 QEMU 进程：真实 OS 进程（sleep），保证 Process API 语义一致
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "sleep",
                Arguments = OperatingSystem.IsWindows() ? "/c pause" : "600",
                UseShellExecute = false,
                CreateNoWindow = true,
                // 不继承测试宿主的输出管道（否则子进程会撑住 vstest 的 EOF 造成挂起）
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(psi)!;
        }
    }

    private string CreateFakeQemuImg()
    {
        var sh = Path.Combine(_dir, "qemu-img");
        File.WriteAllText(sh, """
            #!/bin/sh
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            printf 'QFI\373' > "$target"
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }

    [Fact]
    public async Task Suspend_SavesStateAndFingerprint_ThenResumeUsesIncoming()
    {
        using var fakeQmp = new FakeQmpServer();
        fakeQmp.OnCommand = (cmd, doc) =>
        {
            // 真实 QEMU 的 migrate file: 会把完整状态写入目标文件；假服务模拟这一副作用
            if (cmd == "migrate" && doc.RootElement.TryGetProperty("arguments", out var args))
            {
                var uri = args.TryGetProperty("uri", out var u) ? u.GetString() : null;
                if (uri?.StartsWith("file:") == true)
                    File.WriteAllBytes(uri["file:".Length..], "saved-state"u8);
            }
            return Task.FromResult(cmd switch
            {
                "query-migrate" => """{"return":{"status":"completed"}}""",
                _ => """{"return":{}}""",
            });
        };
        var acceptTask = fakeQmp.AcceptAsync();

        var launcher = new RecordingLauncher();
        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        db.SetPreference("libraryRoot", root);
        Directory.CreateDirectory(Path.Combine(_dir, "fw"));

        var service = new GrassCoreService(db, "qemu-system-x86_64", CreateFakeQemuImg(),
            Path.Combine(_dir, "fw"), "11", launcher,
            qmpTransportFactory: _ => new TcpTransport("127.0.0.1", fakeQmp.Port));

        var vmPath = ((GrassCoreService.CreateVmResult)service.CreateVm(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "挂起测试",
            profileId = "ubuntu",
            diskGiB = 16,
            isoPath = (string?)null,
            cpuCores = 2,
            memoryMiB = 2048,
            startAfterCreate = false,
        }))).Path;

        var pkg = new GrassVm.GrassVmPackage(vmPath);
        service.StartVm(vmPath);
        // QMP 已在 StartVm 内完成握手（否则挂起动作会失败）
        Assert.True(File.Exists(pkg.LockPath));

        // 挂起：stop → migrate file: → query-migrate(completed) → quit
        service.PowerAction(vmPath, "suspend");
        Assert.Contains("stop", fakeQmp.ExecutedCommands);
        Assert.Contains("migrate", fakeQmp.ExecutedCommands);
        Assert.Contains("quit", fakeQmp.ExecutedCommands);

        // 挂起后：状态文件在包内、state.json 标记 + 指纹、vm.lock 已释放
        var stateFile = Path.Combine(pkg.Path, "suspend.state");
        Assert.True(File.Exists(stateFile));
        var state = Config.VmState.Load(pkg);
        Assert.NotNull(state.SuspendedStatePath);
        Assert.False(Path.IsPathRooted(state.SuspendedStatePath!)); // 包内相对引用
        Assert.NotNull(state.SuspendFingerprint);
        Assert.False(File.Exists(pkg.LockPath));
        // Library 显示"已挂起"（UI 的主操作随之变为"恢复"）
        Assert.Equal("suspended", service.ScanLibrary().Vms.Single(v => v.Path == vmPath).State);

        // 已挂起状态：直接 startVm 被拒绝（必须恢复）
        var ex = Assert.Throws<GrassCoreException>(() => service.StartVm(vmPath));
        Assert.Contains("恢复", ex.Message);

        // 恢复：QEMU 以 -incoming file: 启动，状态标记清除
        service.ResumeVm(vmPath);
        var resumeCmd = launcher.Commands.Last();
        var i = resumeCmd.Args.ToList().IndexOf("-incoming");
        Assert.True(i >= 0, "恢复命令必须带 -incoming");
        Assert.StartsWith("file:", resumeCmd.Args[i + 1]);
        Assert.Contains("suspend.state", resumeCmd.Args[i + 1]);
        var after = Config.VmState.Load(pkg);
        Assert.Null(after.SuspendedStatePath);
        Assert.Null(after.SuspendFingerprint);
    }

    [Fact]
    public async Task Start_WhileSuspended_IsRejected_BeforeLockAcquire()
    {
        using var fakeQmp = new FakeQmpServer();
        fakeQmp.OnCommand = (_, _) => Task.FromResult("""{"return":{}}""");
        var acceptTask = fakeQmp.AcceptAsync(); // 本测试不实际连接（startVm 被拒绝）
        var launcher = new RecordingLauncher();
        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        var root = Path.Combine(_dir, "root2");
        Directory.CreateDirectory(root);
        db.SetPreference("libraryRoot", root);
        Directory.CreateDirectory(Path.Combine(_dir, "fw"));

        var service = new GrassCoreService(db, "qemu-system-x86_64", CreateFakeQemuImg(),
            Path.Combine(_dir, "fw"), "11", launcher,
            qmpTransportFactory: _ => new TcpTransport("127.0.0.1", fakeQmp.Port));

        var vmPath = ((GrassCoreService.CreateVmResult)service.CreateVm(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "锁测试", profileId = "ubuntu", diskGiB = 8, isoPath = (string?)null,
            cpuCores = 1, memoryMiB = 1024, startAfterCreate = false,
        }))).Path;
        var pkg = new GrassVm.GrassVmPackage(vmPath);

        // 人为制造挂起状态
        var state = Config.VmState.Load(pkg);
        state.SuspendedStatePath = "suspend.state";
        state.Save(pkg);

        Assert.Throws<GrassCoreException>(() => service.StartVm(vmPath));
        Assert.False(File.Exists(pkg.LockPath)); // 拒绝发生在获取锁之前，不留残留锁
    }

    [Fact]
    public void QemuProcessExit_ReleasesLockAndClearsRuntime()
    {
        using var fakeQmp = new FakeQmpServer();
        fakeQmp.OnCommand = (_, _) => Task.FromResult("""{"return":{}}""");
        var acceptTask = fakeQmp.AcceptAsync();
        var launcher = new RecordingLauncher();
        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        var root = Path.Combine(_dir, "root3");
        Directory.CreateDirectory(root);
        db.SetPreference("libraryRoot", root);
        Directory.CreateDirectory(Path.Combine(_dir, "fw"));

        var service = new GrassCoreService(db, "qemu-system-x86_64", CreateFakeQemuImg(),
            Path.Combine(_dir, "fw"), "11", launcher,
            qmpTransportFactory: _ => new TcpTransport("127.0.0.1", fakeQmp.Port));

        var vmPath = ((GrassCoreService.CreateVmResult)service.CreateVm(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "退出监视", profileId = "ubuntu", diskGiB = 8, isoPath = (string?)null,
            cpuCores = 1, memoryMiB = 1024, startAfterCreate = false,
        }))).Path;
        var pkg = new GrassVm.GrassVmPackage(vmPath);
        service.StartVm(vmPath);
        Assert.True(File.Exists(pkg.LockPath));
        Assert.True(File.Exists(pkg.SessionPath));

        // 客户机内关机 / ACPI 关机最终都表现为 QEMU 进程退出：这里直接结束假进程
        var session = RuntimeSession.Deserialize(File.ReadAllText(pkg.SessionPath));
        Process.GetProcessById(session.QemuPid).Kill();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (File.Exists(pkg.LockPath) && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        // 干净关机记账：vm.lock 释放、runtime/ 清空、不再显示运行中
        Assert.False(File.Exists(pkg.LockPath));
        Assert.Empty(Directory.EnumerateFiles(pkg.RuntimePath));
        Assert.Equal("stopped", service.ScanLibrary().Vms.Single(v => v.Path == vmPath).State);
    }

    [Fact]
    public void UpdateConfig_ClampsValuesAndRejectsWhileRunning()
    {
        using var fakeQmp = new FakeQmpServer();
        fakeQmp.OnCommand = (_, _) => Task.FromResult("""{"return":{}}""");
        var acceptTask = fakeQmp.AcceptAsync();
        var launcher = new RecordingLauncher();
        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        var root = Path.Combine(_dir, "root4");
        Directory.CreateDirectory(root);
        db.SetPreference("libraryRoot", root);
        Directory.CreateDirectory(Path.Combine(_dir, "fw"));

        var service = new GrassCoreService(db, "qemu-system-x86_64", CreateFakeQemuImg(),
            Path.Combine(_dir, "fw"), "11", launcher,
            qmpTransportFactory: _ => new TcpTransport("127.0.0.1", fakeQmp.Port));

        var vmPath = ((GrassCoreService.CreateVmResult)service.CreateVm(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            name = "设置钳制", profileId = "ubuntu", diskGiB = 8, isoPath = (string?)null,
            cpuCores = 1, memoryMiB = 1024, startAfterCreate = false,
        }))).Path;
        var pkg = new GrassVm.GrassVmPackage(vmPath);

        // 超范围值被钳制到安全范围（消费级产品：宁钳制不拒绝）
        var json = (string)service.GetConfig(vmPath)!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["cpuCores"] = 9999;
        node["memoryMiB"] = 16;
        service.UpdateConfig(vmPath, node.ToJsonString());
        var after = new Config.ConfigStore(pkg).Load();
        Assert.Equal(Math.Min(9999, Environment.ProcessorCount), after.CpuCores);
        Assert.True(after.MemoryMiB >= 512);

        // 非法名称拒绝
        node["name"] = "";
        Assert.Throws<GrassCoreException>(() => service.UpdateConfig(vmPath, node.ToJsonString()));

        // 运行中拒绝修改（唯一例外 CD/DVD 走 changeMedium）
        service.StartVm(vmPath);
        Assert.Throws<GrassCoreException>(() => service.UpdateConfig(vmPath, json));
    }
}
