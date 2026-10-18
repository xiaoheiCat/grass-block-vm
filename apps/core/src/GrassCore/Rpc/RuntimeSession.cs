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
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>扫描 Library Root，返回所有带 vm.lock 的 VM 及其接管评估结果。</summary>
    public List<ReadoptResult> ScanAdoptable(string libraryRoot)
    {
        var results = new List<ReadoptResult>();
        foreach (var pkg in GrassVm.GrassVmPackage.ScanLibraryRoot(libraryRoot))
        {
            // 不读取 runtime/session.json 之前先验证包内固定目录；否则 runtime
            // junction/symlink 可把恢复扫描引到包外并泄露/改写外部会话文件。
            if (!pkg.FixedDirectoriesAreSafe() || !pkg.FixedFilesAreSafe()) continue;
            if (!pkg.HasLockFiles) continue;
            RuntimeSession? session = null;
            if (File.Exists(pkg.SessionPath))
            {
                try { session = RuntimeSession.Deserialize(File.ReadAllText(pkg.SessionPath)); }
                catch (System.Text.Json.JsonException) { /* 残留损坏：留给诊断，不自动清锁 */ }
                catch (IOException) { /* 单个包不可读：留给诊断，不中止其他 VM 接管 */ }
                catch (UnauthorizedAccessException) { /* 权限异常同样隔离到该包 */ }
            }
            if (session is null)
            {
                results.Add(new ReadoptResult(pkg, new RuntimeSession
                {
                    SessionId = "unknown", QmpPipe = "unknown", StartedAt = DateTimeOffset.UnixEpoch,
                }, QemuAlive: false, SessionValid: false));
                continue;
            }
            bool alive;
            try { alive = _isProcessAlive(session.QemuPid); }
            catch (ArgumentException) { alive = false; }
            catch (InvalidOperationException) { alive = false; }
            catch (System.ComponentModel.Win32Exception) { alive = false; }
            catch (UnauthorizedAccessException) { alive = false; }
            var valid = false;
            if (alive)
            {
                try { valid = _sessionValidator?.Invoke(session) ?? true; }
                catch (Exception)
                {
                    // 校验器属于单个会话的附加约束；其异常不能让一个坏包
                    // 中止整个 Library 的恢复扫描，当前会话按无效处理。
                    valid = false;
                }
            }
            results.Add(new ReadoptResult(pkg, session, alive, SessionValid: valid));
        }
        return results;
    }
}
