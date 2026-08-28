using System.Diagnostics;

namespace GrassCore.Tests;

/// <summary>
/// 跨平台假 qemu-img（测试专用）：
/// - POSIX：sh 脚本；Windows：qemu-img.bat 委托 PowerShell（CI 的 windows-latest 同样可跑）。
/// - create/convert：向最后一个 .qcow2/.vmd2 参数文件写入 QCOW2 魔数 + 参数行；
/// - commit/rebase：链维护命令不产出文件，直接成功。
/// </summary>
public static class FakeQemuImg
{
    public static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(dir, "fake-qemu-img.ps1");
            File.WriteAllText(ps1, """
                param([Parameter(ValueFromRemainingArguments=$true)]$Rest)
                if ($Rest[0] -in @('commit','rebase')) {
                    Add-Content -Path (Join-Path $PSScriptRoot 'chain-ops.log') -Value ($Rest -join ' ')
                    exit 0
                }
                $target = $Rest | Where-Object { "$_" -match '\.(qcow2|vmdk)' } | Select-Object -Last 1
                if ($target) {
                    if ("$target" -match '\.vmdk') {
                        [IO.File]::WriteAllText($target, "# Disk DescriptorFile`nfake-vmdk")
                    } else {
                        [IO.File]::WriteAllBytes($target, [byte[]](0x51,0x46,0x49,0xFB))
                    }
                    Add-Content -Path $target -Value ($Rest -join ' ')
                }
                exit 0
                """);
            var bat = Path.Combine(dir, "qemu-img.bat");
            File.WriteAllText(bat, $"@powershell -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
            return bat;
        }

        var sh = Path.Combine(dir, $"fake-qemu-img-{Guid.NewGuid():N}");
        File.WriteAllText(sh, """
            #!/bin/sh
            case "$1" in commit|rebase) printf '%s\n' "$*" >> "$0.chain-ops.log"; exit 0;; esac
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            case "$target" in
              *vmdk*) printf '# Disk DescriptorFile\nfake-vmdk' > "$target" ;;
              *)      printf 'QFI\373' > "$target" ;;
            esac
            printf '%s\n' "$*" >> "$target"
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }
}
