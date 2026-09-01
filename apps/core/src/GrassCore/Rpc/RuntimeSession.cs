using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrassCore.Rpc;

/// <summary>
/// runtime/session.json：PID、QMP pipe、Helper PID/port、会话版本等。
/// 放在包内（.grassvm/runtime/）而不是 LocalAppData——包体自包含是最终决策。
/// 正常关机后清空 runtime/ 并删除 vm.lock；异常崩溃则留存供 Core 无损接管。
/// 运行期 secret（Helper token）只存在于进程内存，不写盘。
/// </summary>
public sealed class RuntimeSession
{
    public int SessionVersion { get; set; } = 1;
    public required string SessionId { get; init; }
    public int QemuPid { get; set; }
    public required string QmpPipe { get; init; }
    public int? HelperPid { get; set; }
    public int? HelperPort { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    /// <summary>创建挂起状态时的宿主环境指纹（挂起只保证相同宿主 CPU 环境 + 同一 QEMU major 下恢复）。</summary>
    public HostEnvironmentFingerprint? SuspendEnvironment { get; set; }
    public string? QemuMajorAtStart { get; set; }
    /// <summary>创建会话的机器标识：重接管/收尾只处理【本机】的会话——
    /// 库可能在 NAS/同步盘上被另一台电脑持有，别机的 PID 在本机必然"不存在"，
    /// 按"死了"清 runtime/放锁会把别人正在运行的 VM 解锁（双开=盘损坏）。</summary>
    public string? MachineId { get; set; }
    /// <summary>启动意图：Core 在 QEMU 启动后、PID 回写前崩溃时用于候选进程匹配。</summary>
    public string? QemuExecutablePath { get; set; }
    public string? CommandLineFingerprint { get; set; }
    /// <summary>本次 SPICE 会话 ticket。仅存在于 Core 进程内存，不写入 session.json。</summary>
    [JsonIgnore]
    public string? SpicePassword { get; set; }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Opts);
    public static RuntimeSession? Deserialize(string json) =>
        JsonSerializer.Deserialize<RuntimeSession>(json, Opts);
}

/// <summary>挂起状态兼容性指纹：CPU vendor/model/特性 + QEMU major + 挂起格式版本。</summary>
public sealed record HostEnvironmentFingerprint(
    string CpuVendor,
    string CpuModel,
    string CpuFeaturesHash,
    string QemuMajor,
    int SuspendFormatVersion);

/// <summary>
/// Core 崩溃恢复（硬性承诺）：新 GrassCore 启动后扫描 Library 内带 vm.lock 的 VM，
/// 读取 runtime/session.json，校验 PID / QMP Pipe 与进程仍存在，重新接管；
/// 旧 Helper 无法安全复用时终止/替换 Helper，不触碰 QEMU。
/// </summary>
public sealed class CoreCrashRecovery
{
    public sealed record ReadoptResult(GrassVm.GrassVmPackage Package, RuntimeSession Session, bool QemuAlive, bool SessionValid);

    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<RuntimeSession, bool>? _sessionValidator;

    public CoreCrashRecovery(Func<int, bool>? isProcessAlive = null,
        Func<RuntimeSession, bool>? sessionValidator = null)
    {
        _isProcessAlive = isProcessAlive ?? DefaultIsAlive;
        _sessionValidator = sessionValidator;
    }

    private static bool DefaultIsAlive(int pid)
    {
        try { System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>扫描 Library Root，返回所有带 vm.lock 的 VM 及其接管评估结果。</summary>
    public List<ReadoptResult> ScanAdoptable(string libraryRoot)
    {
        var results = new List<ReadoptResult>();
        foreach (var pkg in GrassVm.GrassVmPackage.ScanLibraryRoot(libraryRoot))
        {
            if (!File.Exists(pkg.LockPath)) continue;
            RuntimeSession? session = null;
            if (File.Exists(pkg.SessionPath))
            {
                try { session = RuntimeSession.Deserialize(File.ReadAllText(pkg.SessionPath)); }
                catch (System.Text.Json.JsonException) { /* 残留损坏：留给诊断，不自动清锁 */ }
            }
            if (session is null)
            {
                results.Add(new ReadoptResult(pkg, new RuntimeSession
                {
                    SessionId = "unknown", QmpPipe = "unknown", StartedAt = DateTimeOffset.UnixEpoch,
                }, QemuAlive: false, SessionValid: false));
                continue;
            }
            var alive = _isProcessAlive(session.QemuPid);
            var valid = alive && (_sessionValidator?.Invoke(session) ?? true);
            results.Add(new ReadoptResult(pkg, session, alive, SessionValid: valid));
        }
        return results;
    }
}
