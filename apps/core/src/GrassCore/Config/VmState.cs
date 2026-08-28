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

    public DateTimeOffset? LastStartedAt { get; set; }
    /// <summary>记录创建/最后运行时的 QEMU major（升级保护判定用；1.4 = 一套确定版本的 Core+QEMU+Helper+驱动）。</summary>
    public string? LastQemuMajor { get; set; }
    /// <summary>最近一次窗口尺寸等本机展示状态（.grassvm.zip 导出时不带走这些痕迹）。</summary>
    public WindowState? LastDisplayWindow { get; set; }

    /// <summary>
    /// 挂起状态文件（包内）。存在即 VM 处于"已挂起"：直接 startVm 会被拒绝（必须 resume），
    /// 因为 QEMU 需要 -incoming 才能回到保存的瞬间。恢复完成或正常启动后清空。
    /// </summary>
    public string? SuspendedStatePath { get; set; }
    /// <summary>挂起时的宿主环境指纹：只保证相同宿主 CPU 环境 + 同一 QEMU major 下恢复。</summary>
    public string? SuspendFingerprint { get; set; }

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
