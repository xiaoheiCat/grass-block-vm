using System.Text.Json;
using GrassCore.Library;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

public sealed class LibraryOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-library-gate-" + Guid.NewGuid().ToString("N"));

    public LibraryOperationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task SetLibraryRoot_WaitsForCreate_AndCreateKeepsCoreBusy()
    {
        var entered = Path.Combine(_dir, "entered");
        var release = Path.Combine(_dir, "release");
        var qemuImg = WriteBlockingQemuImg(entered, release);
        var oldRoot = Path.Combine(_dir, "old");
        var newRoot = Path.Combine(_dir, "new");
        var fw = Path.Combine(_dir, "fw");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(fw);
        File.WriteAllText(Path.Combine(fw, "OVMF_VARS.fd"), "vars");

        using var db = new HostDb(Path.Combine(_dir, "grass.db"));
        db.SetPreference("libraryRoot", oldRoot);
        var service = new GrassCoreService(db, "qemu-system-x86_64", qemuImg, fw, "11");

        var create = Task.Run(() => (GrassCoreService.CreateVmResult)service.CreateVm(
            JsonSerializer.SerializeToElement(new
            {
                name = "gate-test", profileId = "ubuntu", diskGiB = 8,
                isoPath = (string?)null, startAfterCreate = false,
            })));

        Assert.True(SpinWait.SpinUntil(() => File.Exists(entered), TimeSpan.FromSeconds(10)),
            "假 qemu-img 未进入阻塞点");
        Assert.True(service.HasInFlightDiskJobs,
            "长创建操作期间 Core 必须保持 busy，不能触发 idle 退出");

        var switchRoot = Task.Run(() => service.SetLibraryRoot(newRoot));
        await Task.Delay(200);
        Assert.False(switchRoot.IsCompleted,
            "SetLibraryRoot 不得在旧根目录长操作中途切换全局根目录");

        File.WriteAllText(release, "go");
        var created = await create.WaitAsync(TimeSpan.FromSeconds(10));
        await switchRoot.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith(Path.GetFullPath(oldRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(created.Path), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(newRoot), service.LibraryRoot);
        Assert.False(service.HasInFlightDiskJobs);
    }

    private string WriteBlockingQemuImg(string entered, string release)
    {
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(_dir, "blocking-qemu-img.ps1");
            File.WriteAllText(ps1, $$"""
                [IO.File]::WriteAllText('{{entered}}', 'entered')
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                while (-not (Test-Path '{{release}}') -and [DateTime]::UtcNow -lt $deadline) {
                    Start-Sleep -Milliseconds 50
                }
                $target = $args | Where-Object { "$_" -match '\.qcow2' } | Select-Object -Last 1
                if ($target) {
                    $parent = Split-Path -Parent $target
                    if ($parent) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
                    [IO.File]::WriteAllBytes($target, [byte[]](0x51,0x46,0x49,0xFB))
                }
                """);
            var bat = Path.Combine(_dir, "blocking-qemu-img.bat");
            File.WriteAllText(bat,
                $"@powershell -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
            return bat;
        }

        var sh = Path.Combine(_dir, "blocking-qemu-img");
        File.WriteAllText(sh, $$"""
            #!/bin/sh
            : > '{{entered}}'
            i=0
            while [ ! -e '{{release}}' ] && [ "$i" -lt 200 ]; do sleep .05; i=$((i+1)); done
            for a in "$@"; do case "$a" in *.qcow2*) target="$a";; esac; done
            mkdir -p "$(dirname "$target")"
            printf 'QFI\373' > "$target"
            """);
        File.SetUnixFileMode(sh,
            UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return sh;
    }
}
