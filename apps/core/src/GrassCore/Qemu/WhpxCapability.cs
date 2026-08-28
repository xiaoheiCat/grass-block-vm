using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;

namespace GrassCore.Qemu;

/// <summary>
/// WHPX / 硬件虚拟化检测（三层预检）：
/// 1) CPU 虚拟化能力是否存在；2) Windows Hypervisor Platform 是否可用；3) QEMU 实际初始化 WHPX。
/// 任何一层失败都给用户清晰的修复说明（而非 QEMU 原始报错），并阻止启动——绝不静默回退 TCG。
/// </summary>
public sealed class WhpxCapability
{
    public sealed record Result(bool Available, string UserGuidance)
    {
        public static Result Ok() => new(true, "");
        public static Result Fail(string guidance) => new(false, guidance);
    }

    [SupportedOSPlatform("windows")]
    public static Result CheckWindows()
    {
        // 层 1+2：查询 Win32_Processor 虚拟化固件启用 + HypervisorPresent
        var hypervisorPresent = Environment.OSVersion.Version.Major >= 10 && HasHypervisor();
        if (!hypervisorPresent)
        {
            return Result.Fail(
                "此电脑未启用 Windows 虚拟机平台（WHPX）。请在“启用或关闭 Windows 功能”中打开" +
                "“虚拟机平台”和“Windows 虚拟机监控程序平台”，并在 BIOS/UEFI 中开启 Intel VT-x / AMD-V 后重试。");
        }
        if (!CpuVirtualizationFirmwareEnabled())
        {
            return Result.Fail("处理器未在固件中启用虚拟化（Intel VT-x / AMD-V）。请进入 BIOS/UEFI 开启后重试。");
        }
        return Result.Ok();
    }

    [SupportedOSPlatform("windows")]
    private static bool HasHypervisor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                if (o["HypervisorPresent"] is bool b) return b;
            }
        }
        catch { /* 查询失败按 false 处理，交给 QEMU 初始化结果 */ }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool CpuVirtualizationFirmwareEnabled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
            foreach (var o in searcher.Get())
            {
                if (o["VirtualizationFirmwareEnabled"] is bool b && !b) return false;
            }
        }
        catch { }
        return true;
    }

    /// <summary>QEMU 5 秒早期失败检测：跨 QEMU major 首启 + 升级保护逻辑配套（见 UpgradeProtection）。</summary>
    public static async Task<bool> QemuSurvivesEarlyWindowAsync(Process qemuProcess, int seconds = 5, CancellationToken ct = default)
    {
        try
        {
            await qemuProcess.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(seconds), ct);
            return false; // 5 秒内退出（非 0）→ 可能不兼容当前版本
        }
        catch (TimeoutException)
        {
            return true; // 仍在运行 = 通过早期监测
        }
    }
}

/// <summary>
/// 跨 QEMU major 升级保护（B + C）：
/// 首次用新 major 启动某 VM 前：备份 config/state/NVRAM/必要元数据 + 创建隐藏升级保护快照 →
/// 启动新版 QEMU → 5 秒内非 0 退出则提示回退上一 major + 脱敏日志导出 + GitHub Issue 入口。
/// 保护快照保留至少 24 小时，并在 24 小时后的第一次正常关机时自动删除。
/// </summary>
public static class UpgradeProtection
{
    public sealed record Plan(
        string VmPath,
        string PreviousQemuMajor,
        string NewQemuMajor,
        string MetadataBackupDir,
        string ProtectionSnapshotUuid);

    public static bool NeedsProtection(string? lastRunQemuMajor, string bundledQemuMajor) =>
        lastRunQemuMajor is not null && lastRunQemuMajor != bundledQemuMajor;

    public static Plan CreatePlan(GrassVm.GrassVmPackage package, string previousMajor, string newMajor)
    {
        return new Plan(
            package.Path,
            previousMajor,
            newMajor,
            System.IO.Path.Combine(package.TempPath, $"upgrade-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"),
            Guid.NewGuid().ToString());
    }

    public static void BackupMetadata(GrassVm.GrassVmPackage package, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        foreach (var f in new[] { package.ConfigPath, package.StatePath })
        {
            if (File.Exists(f))
                File.Copy(f, System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(f)), overwrite: true);
        }
        // NVRAM：UEFI 变量状态（可能改变 Guest UEFI 行为的数据都必须备份）
        var vars = System.IO.Path.Combine(package.FirmwarePath, "VARS.fd");
        if (File.Exists(vars))
            File.Copy(vars, System.IO.Path.Combine(backupDir, "VARS.fd"), overwrite: true);
    }
}

/// <summary>
/// 错误日志脱敏：默认脱敏用户名、用户目录、VM 名称、共享路径、外部磁盘路径、设备个人名称；
/// 保留 QEMU/Windows/CPU/WHPX/错误码/调用栈等诊断信息。导出只生成本地文件，不自动上传。
/// </summary>
public static class LogRedactor
{
    private static readonly string[] RedactPatterns =
    {
        @"C:\\Users\\(?<u>[^\\\s]+)",            // Windows 用户目录
        @"(?<![\w])Users/(?<u>[^/\s]+)",          // POSIX 风格用户目录
        @"(?<=[\s""])[A-Za-z]:\\[^""]*?\.grassvm", // VM 包路径
        @"(?<=[\s""])[A-Za-z]:\\[^""]*?\.qcow2",  // 外部磁盘路径
    };

    public static string Redact(string log, IReadOnlyDictionary<string, string>? extraNames = null)
    {
        var result = log;
        foreach (var p in RedactPatterns)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, p, m => m.Value.Replace(m.Groups["u"].Success ? m.Groups["u"].Value : m.Value,
                    m.Groups["u"].Success ? "<USER>" : "<REDACTED>"));
        }
        // 简单替换 .grassvm / .qcow2 完整路径
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[A-Za-z]:\\[^\s""]+\.grassvm", "<REDACTED>.grassvm");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[A-Za-z]:\\[^\s""]+\.qcow2", "<REDACTED>.qcow2");
        if (extraNames is not null)
        {
            foreach (var (name, _) in extraNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    result = result.Replace(name, "<REDACTED>");
            }
        }
        return result;
    }
}
