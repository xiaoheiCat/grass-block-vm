using System.Text.Json;
using System.Text.Json.Serialization;
using GrassCore.GrassVm;

namespace GrassCore.Config;

/// <summary>
/// state.json：可随 VM 移动的非关键状态（不保存运行期 secret）。
/// runtime/session.json 才是运行期瞬时信息所在（包内 runtime/ 目录）。
/// </summary>
public sealed class VmState
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 与 Load/Save 同源的读取选项。供需要【严格读】的调用方共用（损坏即抛，
    /// 而不是像 Load 那样静默重建——Restore 事务的提交判定依赖它区分
    /// "pending 已清"与"state 读不了"）。
    /// </summary>
    internal static JsonSerializerOptions ForRead => Opts;

    public DateTimeOffset? LastStartedAt { get; set; }
    /// <summary>记录创建/最后运行时的 QEMU major（升级保护判定用；1.4 = 一套确定版本的 Core+QEMU+Helper+驱动）。</summary>
    public string? LastQemuMajor { get; set; }
    /// <summary>最近一次窗口尺寸等本机展示状态（.grassvm.zip 导出时不带走这些痕迹）。</summary>
    public WindowState? LastDisplayWindow { get; set; }

    /// <summary>
    /// 当前快照工作位置（恢复到某个快照后继续工作的位置）。新快照挂到它下面，
    /// 而不是盲目挂到"最新叶子"——否则恢复 s1 后新建的快照会被记成 s3 的孩子，树会说谎。
    /// </summary>
    public string? CurrentSnapshotUuid { get; set; }

    /// <summary>
    /// 挂起状态文件（包内）。存在即 VM 处于"已挂起"：直接 startVm 会被拒绝（必须 resume），
    /// 因为 QEMU 需要 -incoming 才能回到保存的瞬间。恢复完成或正常启动后清空。
    /// </summary>
    public string? SuspendedStatePath { get; set; }
    /// <summary>挂起时的宿主环境指纹：只保证相同宿主 CPU 环境 + 同一 QEMU major 下恢复。</summary>
    public string? SuspendFingerprint { get; set; }

    /// <summary>
    /// 进行中的 Restore 事务一次性标识（与 snapshots/restore-journal.json 的 TxId 配对）。
    /// 事务开始前写入；提交点（位置 + 清除）一次落盘。修复用它判定"已提交/未提交"——
    /// 只看"位置 == 目标"在恢复到当前位置时无法区分（目标本来就是当前位置）。
    /// </summary>
    public string? PendingRestoreTxId { get; set; }

    public sealed record WindowState(int Width, int Height);

    public static VmState Load(GrassVmPackage package)
    {
        if (!File.Exists(package.StatePath)) return new VmState();
        try
        {
            return JsonSerializer.Deserialize<VmState>(File.ReadAllText(package.StatePath), Opts) ?? new VmState();
        }
        catch (JsonException)
        {
            return new VmState(); // state.json 损坏 = 确定无损，直接重建
        }
    }

    public void Save(GrassVmPackage package)
    {
        AtomicFile.WriteJsonValidated(package.StatePath, JsonSerializer.Serialize(this, Opts));
    }
}
