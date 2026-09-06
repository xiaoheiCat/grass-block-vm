using System.Diagnostics;
using GrassCore.Library;
using GrassCore.Qemu;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// 进程级集成测试：真实 OS 进程 + 真启动器（QemuProcessLauncher）/ 真服务路径。
/// 覆盖：stderr 排水与 qemu.log 落盘（管道塞满会冻结 QEMU 整机）、
/// AdoptRunningVms 的幂等（不重复开 QMP 连接）。
/// </summary>
public class LauncherAndAdoptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-launcher-" + Guid.NewGuid().ToString("N"));

    public LauncherAndAdoptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>伪 QEMU 可执行体：往 stderr 倾泻大量输出（模拟固件警告刷屏）后正常退出。
    /// 文件名含 "qemu" 以通过 AdoptRunningVms 的 PID 复用防护（进程名必须像 QEMU）。</summary>
    private string WriteFakeQemu(string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(_dir, $"qemu-fake-{mode}.bat");
            File.WriteAllText(bat, mode == "flood"
                ? "@echo off\r\nfor /L %%i in (1,1,2000) do echo SENTINEL-STDERR-WARN some firmware warning %%i 1>&2\r\nexit /b 0\r\n"
                : "@echo off\r\npause\r\n");
            return bat;
        }
        var sh = Path.Combine(_dir, $"qemu-fake-{mode}");
        File.WriteAllText(sh, mode == "flood"
            ? """
              #!/bin/sh
              i=0
              while [ $i -lt 2000 ]; do
                echo "SENTINEL-STDERR-WARN some firmware warning $i" >&2
                i=$((i+1))
              done
              exit 0
              """
            : """
              #!/bin/sh
              sleep 600
              """);
        File.SetUnixFileMode(sh, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
            | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return sh;
    }

    [Fact]
    public void QemuProcessLauncher_DrainsStderr_AndWritesQemuLog()
    {
        // 回归：stdout 未重定向时 BeginOutputReadLine 抛异常、被外层 catch 吞掉，
        // BeginErrorReadLine 从不执行——stderr 管道无人读，写满后 QEMU 冻结且
        // logs/qemu.log（早期退出诊断的唯一线索）一个字节都不会有
        var pkgRoot = Path.Combine(_dir, "Pkg.grassvm");
        Directory.CreateDirectory(pkgRoot);
        var qemu = WriteFakeQemu("flood");

        var launcher = new QemuProcessLauncher(qemu);
        using var proc = launcher.Start(new QemuCommandLine
        {
            Args = new[] { "-accel", "whpx" },
            PackageRoot = pkgRoot,
            QmpPipeName = "grassvm-qmp-test",
        });

        Assert.True(proc.WaitForExit(30_000), "伪 QEMU 被 stderr 管道背压卡死（排水失效）");
        var logPath = Path.Combine(pkgRoot, "logs", "qemu.log");
        Assert.True(File.Exists(logPath), "logs/qemu.log 没有落盘——stderr 排水从未启动");
        Assert.Contains("SENTINEL-STDERR-WARN", File.ReadAllText(logPath));
    }

    [Fact]
    public void AdoptRunningVms_IsIdempotent_NoSecondQmpConnection()
    {
        // UI 每次重启都重发 adoptRunningVms：已在 _running 里的 VM 必须直接跳过。
        // 回归点：守护放在新 QMP 连接【之后】——每次重发泄漏一个客户端并和活连接
        // 争抢同一根管道（QEMU 的 QMP chardev 是单实例）。
        // 注意进程形态：接管有 PID 复用防护（进程名必须含 qemu）。shebang 脚本
        // exec 后 comm 是 "sh"——必须复制真正的二进制（sleep）并以 qemu 命名。
        if (OperatingSystem.IsWindows()) return; // Windows 上 .bat 经 cmd 启动，进程名语义不同

        var sleepCopy = Path.Combine(_dir, "qemu-fake-idle-sleep");
        File.Copy("/bin/sleep", sleepCopy);
        File.SetUnixFileMode(sleepCopy,
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
            | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (OperatingSystem.IsMacOS())
        {
            try { Process.Start("codesign", new[] { "-s", "-", "-f", sleepCopy })?.WaitForExit(); } catch { }
        }

        using var fakeQmp = new FakeQmpServer();
        fakeQmp.OnCommand = (cmd, _) => Task.FromResult(cmd switch
        {
            "query-status" => """{"return":{"status":"running","running":true}}""",
            _ => """{"return":{}}""",
        });
        var acceptTask = fakeQmp.AcceptAsync();
        var connectCount = 0;

        var launcher = new FakeQemuLauncher(_ => sleepCopy);
        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        db.SetPreference("libraryRoot", root);
        Directory.CreateDirectory(Path.Combine(_dir, "fw"));
        File.WriteAllText(Path.Combine(_dir, "fw", "OVMF_VARS.fd"), "vars-template");

        var service = new GrassCoreService(db, "qemu-system-x86_64", FakeQemuImg.Create(_dir),
            Path.Combine(_dir, "fw"), "11", launcher,
            qmpTransportFactory: _ =>
            {
                Interlocked.Increment(ref connectCount);
                return new TcpTransport("127.0.0.1", fakeQmp.Port);
            });

        // 宿主环境偶尔会在测试中途杀掉伪装 QEMU 的长命进程（Exited 收尾随之放锁清
        // runtime，接管就合理地报告空——那不是幂等性的问题）。这里对"假 QEMU 活着"
        // 这一环境前提做显式校验 + 有限重试：若系统性死亡（比如被我们自己的代码误杀），
        // 三次全死 → 失败并指明真因，而不是误报成接管缺陷
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var vmPath = ((GrassCoreService.CreateVmResult)service.CreateVm(System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                name = $"Adopt-Me-{attempt}", // ASCII：默认 JSON 序列化会把非 ASCII 转义成 \uXXXX，路径断言就没法直接 Contains 了
                profileId = "ubuntu",
                diskGiB = 8,
                isoPath = (string?)null,
                cpuCores = 2,
                memoryMiB = 2048,
                startAfterCreate = false,
            }))).Path;
            service.StartVm(vmPath);
            var connectsAfterStart = Volatile.Read(ref connectCount);
            Assert.True(connectsAfterStart >= 1, "StartVm 应至少建立一次 QMP 连接");

            var pid = RuntimeSession.Deserialize(
                File.ReadAllText(new GrassVm.GrassVmPackage(vmPath).SessionPath))!.QemuPid;
            try
            {
                using var check = Process.GetProcessById(pid);
                if (check.HasExited) throw new InvalidOperationException();
            }
            catch (ArgumentException) { continue; }   // 假 QEMU 已死：环境问题，换一台重试
            catch (InvalidOperationException) { continue; }

            // 连续两次接管：都成功上报 adopted，但不再多开任何 QMP 连接
            // （JSON 把 "/" 转义成 "\/"——还原后再比对路径）
            var adopted1 = System.Text.Json.JsonSerializer.Serialize(service.AdoptRunningVms()).Replace("\\/", "/");
            Assert.Contains(vmPath, adopted1);
            // 第二次调用前再校验一次活性：假 QEMU 也可能恰好死在这两次调用之间
            // （死了接管就【合理地】报空——不是幂等性缺陷，别误报）
            try
            {
                using var check2 = Process.GetProcessById(pid);
                if (check2.HasExited) throw new InvalidOperationException();
            }
            catch (ArgumentException) { continue; }
            catch (InvalidOperationException) { continue; }
            var adopted2 = System.Text.Json.JsonSerializer.Serialize(service.AdoptRunningVms()).Replace("\\/", "/");
            Assert.Contains(vmPath, adopted2);
            Assert.Equal(connectsAfterStart, Volatile.Read(ref connectCount));
            return;
        }
        Assert.Fail("伪装 QEMU 的长命进程三次都在启动后立刻退出——疑似被系统性误杀（不是接管逻辑的问题），请单独排查环境。");
    }

    /// <summary>RecordingLauncher 的变体：起一个名字含 qemu 的真实长命进程（过 PID 复用防护）。</summary>
    private sealed class FakeQemuLauncher(Func<string, string> fileNameOf) : IQemuProcessLauncher
    {
        public Process Start(QemuCommandLine cmd)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileNameOf(cmd.QmpPipeName),
                Arguments = "600",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(psi)!;
        }
    }
}
