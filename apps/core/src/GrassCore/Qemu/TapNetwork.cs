namespace GrassCore.Qemu;

/// <summary>
/// Windows TAP-Windows6 的宿主适配器约定。
///
/// 安装器通过 tap0901 驱动包创建适配器，并把它重命名为 AdapterName。
/// QEMU 的 ifname 必须是已存在的 TAP 适配器名称，不能使用 VM 配置中的
/// BridgeAdapter（那是宿主物理网卡选择），也不能按 DeviceId 临时生成一个
/// 安装器从未创建过的名称。
/// </summary>
public static class TapNetwork
{
    /// <summary>安装器创建 TAP-Windows6 设备时使用的硬件 ID。</summary>
    public const string DriverHardwareId = "tap0901";

    /// <summary>
    /// 安装后统一使用的 Windows 网络适配器名称。桥接/Host-only 的网络
    /// 后端都先接入这个 TAP 设备；BridgeAdapter 仍保留在配置中，供宿主
    /// 桥接配置层选择物理网卡，绝不直接当作 QEMU ifname。
    /// </summary>
    public const string AdapterName = "GrassVM-Tap";
}
