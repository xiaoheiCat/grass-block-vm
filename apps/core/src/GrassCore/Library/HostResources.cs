namespace GrassCore.Library;

/// <summary>
/// 宿主资源探测：所有配置入口（创建向导/设置修改/OVF 导入）共用同一套钳制依据。
/// 导入尤其必要：OVF 的内存数量没有上限约束，恶意或单位错误的包会产出
/// -m 2147483647M 这种启动即崩的配置——宁可钳制不可拒绝（消费级产品原则）。
/// </summary>
internal static class HostResources
{
    /// <summary>宿主物理内存（MiB）。探测失败退回 8 GB 保守值。</summary>
    public static long TotalMemoryMiB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                var row = searcher.Get().Cast<System.Management.ManagementBaseObject>().First();
                return Convert.ToInt64(row["TotalVisibleMemorySize"]) / 1024;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/sbin/sysctl",
                    Arguments = "-n hw.memsize",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var bytes = long.TryParse(p.StandardOutput.ReadToEnd().Trim(), out var v) ? v : 0;
                p.WaitForExit(3000);
                if (bytes > 0) return bytes / (1024 * 1024);
            }
        }
        catch { /* 探测失败退回保守默认 */ }
        return 8 * 1024; // 8 GB 保守值
    }

    /// <summary>内存钳制：[512, 宿主一半]。探测失败（8GB 保守值）时上限 4GB。</summary>
    public static int ClampMemoryMiB(long requested) =>
        (int)Math.Clamp(requested, 512, Math.Max(512, TotalMemoryMiB() / 2));
}
