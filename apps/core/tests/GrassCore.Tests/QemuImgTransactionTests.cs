using System.Diagnostics;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// qemu-img 事务测试：成功、取消、进程崩溃、.grass-tmp 清理。
/// 用一个假的 qemu-img 可执行文件模拟真实行为（POSIX shell 脚本 / Windows batch）。
/// </summary>
public class QemuImgTransactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _fakeQemuImg;

    public QemuImgTransactionTests()
    {
        Directory.CreateDirectory(_dir);
        _fakeQemuImg = CreateFakeQemuImg();
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string CreateFakeQemuImg()
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(_dir, "qemu-img.bat");
            File.WriteAllText(bat, """
                @echo off
                if /i "%1"=="commit" exit /B 0
                if /i "%1"=="rebase" exit /B 0
                echo %* | findstr /C:"crash-mode" >nul && exit /B 1
                echo %* | findstr /C:"slow-mode" >nul && (ping -n 30 127.0.0.1 >nul)
                for %%a in (%*) do set LAST=%%~a
                echo QFI> "%LAST%"
                exit /B 0
                """);
            return bat;
        }
        var sh = Path.Combine(_dir, "qemu-img");
        File.WriteAllText(sh, """
            #!/bin/sh
            # 假 qemu-img：崩溃退出 1；慢模式 sleep；否则把最后一个镜像路径写成带格式魔数的文件
            case "$*" in
              *crash-mode*) exit 1 ;;
              *slow-mode*) sleep 30 ;;
            esac
            case "$1" in commit|rebase) exit 0;; esac
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            case "$target" in
              *vmdk*) printf '# Disk DescriptorFile\nfake-vmdk' > "$target" ;;
              *)      printf 'QFI\373' > "$target" ;;
            esac
            printf '%s\n' "$*" >> "$target"   # 把参数写进产物，让不同命令产出的字节不同
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }

    private TransactionalDiskOps Ops() => new(_fakeQemuImg);

    [Fact]
    public async Task CreateSparseQcow2_Success_WritesVerifiedFile_NoTempResidue()
    {
        var target = Path.Combine(_dir, "disk.qcow2");
        await Ops().CreateSparseQcow2Async(target, 80L * 1024 * 1024 * 1024);

        Assert.True(File.Exists(target));
        using var fs = File.OpenRead(target);
        var magic = new byte[4];
        fs.ReadExactly(magic);
        Assert.Equal(new byte[] { (byte)'Q', (byte)'F', (byte)'I', 0xfb }, magic);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*" + TransactionalDiskOps.TempSuffix));
    }

    [Fact]
    public async Task ProcessFailure_LeavesNoTargetAndNoTemp()
    {
        var ops = Ops();
        var target = Path.Combine(_dir, "crash.qcow2");
        // 崩溃模拟：qemu-img 以非 0 退出 → 只清理临时产物，目标文件不出现
        using var op = ops.Run(["create", "-f", "qcow2", target + ".grass-tmp", "1G", "crash-mode"], target + ".grass-tmp");
        await Assert.ThrowsAsync<QemuImgException>(() => TransactionalDiskOps.WaitForAsync(op, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*" + TransactionalDiskOps.TempSuffix));
    }

    [Fact]
    public async Task Cancel_KillsProcess_OriginalIntact_NoTempResidue()
    {
        var target = Path.Combine(_dir, "cancel.qcow2");
        await Ops().CreateSparseQcow2Async(target, 1024); // 先有一个完好的原文件
        var before = await File.ReadAllBytesAsync(target);

        var ops = Ops();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        using var op = ops.Run(["resize", target + ".grass-tmp", "1G", "slow-mode"], target + ".grass-tmp");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => TransactionalDiskOps.WaitForAsync(op, cts.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(target)); // 原文件未动
        Assert.False(File.Exists(target + TransactionalDiskOps.TempSuffix)); // 临时产物已清理
    }

    [Fact]
    public async Task Resize_GrowsContainer_Atomically()
    {
        var target = Path.Combine(_dir, "resize.qcow2");
        await Ops().CreateSparseQcow2Async(target, 1024);
        var before = await File.ReadAllBytesAsync(target);
        await Ops().ResizeQcow2Async(target, 2048);
        Assert.True(File.Exists(target));
        Assert.NotEqual(before, await File.ReadAllBytesAsync(target)); // 已替换为 resize 后的产物
        Assert.Empty(Directory.EnumerateFiles(_dir, "*" + TransactionalDiskOps.TempSuffix));
    }

    [Fact]
    public void ResidualGrassTmp_CleanedAtStartup()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Residue VM");
        var residue = Path.Combine(pkg.DisksPath, "half-written.qcow2.grass-tmp");
        File.WriteAllText(residue, "half");
        // 应用启动时自动删除残留 .grass-tmp，不尝试断点续传
        var removed = pkg.CleanupResidualTempFiles();
        Assert.Equal(1, removed);
        Assert.False(File.Exists(residue));
    }
}
