using System.Text.Json.Serialization;

namespace GrassCore.Config;

/// <summary>
/// VmConfiguration 描述"一台虚拟电脑"，而不是 QEMU CLI。
/// QemuCommandBuilder 负责把抽象模型转换为 QEMU 参数。
/// </summary>
public sealed class VmConfiguration
{
    /// <summary>当前 Schema 版本。配置升级只向前，不保证降级兼容；升级前备份 config。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>显示名（默认取包名，可独立修改）。描述/备注属于 VM 持久配置。</summary>
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>封面/图标：包内相对路径（artwork/...），随包移动不丢失。</summary>
    public string? Artwork { get; set; }

    public string OsProfileId { get; set; } = "other";

    public int CpuCores { get; set; } = 2;

    /// <summary>内存，MiB。</summary>
    public int MemoryMiB { get; set; } = 2048;

    public FirmwareSettings Firmware { get; set; } = new();

    /// <summary>默认启动顺序：硬盘 → CD/DVD → 网络启动。设置页可调整。</summary>
    public List<BootClass> BootOrder { get; set; } = new() { BootClass.Disk, BootClass.Cd, BootClass.Network };

    /// <summary>设备列表。内部以 deviceId(UUID) 为真实标识；显示名按添加时间稳定生成（磁盘 #1 / 网络 #2），不可手动排序。</summary>
    public List<VmDevice> Devices { get; set; } = new();

    /// <summary>链接克隆信息：只记录父 .grassvm 位置 + 父快照 UUID。父子在同一目录树时优先保存相对路径。</summary>
    public CloneInfo? CloneInfo { get; set; }

    // ---- 派生规则 ----

    public IEnumerable<T> DevicesOfType<T>() where T : VmDevice => Devices.OfType<T>();

    /// <summary>每台 VM 必须有显示设备（1.0 不支持 Headless）。</summary>
    public bool HasDisplayDevice => Devices.Any(d => d is DisplayDevice);

    /// <summary>向导要求至少一个可启动来源；底层模型允许无盘裸机开机（向导规则不污染 Core 模型）。</summary>
    public bool HasBootableSource => Devices.Any(d => d is DiskDevice or CdromDevice { IsoPath: not null });

    /// <summary>运行中锁定判断：所有非热插拔硬件运行时不可编辑，UI 不存在"待应用配置"。</summary>
    public static bool IsHotPluggable(VmDevice device) => device is CdromDevice;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BootClass
{
    Disk,
    Cd,
    Network,
}

/// <summary>固件：支持 UEFI 与传统 BIOS；现代系统默认 UEFI；Windows 11 Profile 默认 TPM 2.0 + Secure Boot。</summary>
public sealed class FirmwareSettings
{
    public FirmwareKind Kind { get; set; } = FirmwareKind.Uefi;
    public bool SecureBoot { get; set; }
    /// <summary>TPM 以"安全芯片"设备呈现，这里只保存全局偏好由 Profile 决定默认值。</summary>
    public bool Tpm { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FirmwareKind
{
    Uefi,
    Bios,
}

/// <summary>链接克隆必须基于已有快照创建。父路径尽量使用相对路径，失效时要求重新定位并永久写回。</summary>
public sealed class CloneInfo
{
    public string ParentVmPath { get; set; } = "";
    public string ParentSnapshotUuid { get; set; } = "";
}

/// <summary>
/// 设备基类：内部 UUID（用户不可见）+ createdOrder（稳定展示顺序）。
/// 设备列表按添加时间稳定显示；编号一旦分配，在 VM 生命周期内保持稳定（删除 #1 不改 #2 的名字）。
/// </summary>
public abstract class VmDevice
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>添加顺序（全局递增，不回收）。用于稳定展示与设备命名。</summary>
    public int CreatedOrder { get; set; }

    /// <summary>设备类型标记名（JSON 多态鉴别器）。</summary>
    public abstract string DeviceType { get; }
}

/// <summary>硬盘：全部按可写设备处理（1.0 无"只读硬盘"）；包内磁盘参与快照，包外不参与数据回滚。</summary>
public sealed class DiskDevice : VmDevice
{
    public override string DeviceType => "disk";
    /// <summary>包内相对路径或包外绝对路径（PathPolicy 规范化后存储）。</summary>
    public string Path { get; set; } = "";
    /// <summary>虚拟容量（字节）。扩容只增大容器，不自动扩 Guest 分区。</summary>
    public long SizeBytes { get; set; }
    public bool IsExternal => System.IO.Path.IsPathRooted(Path);
}

/// <summary>CD/DVD：只读介质；运行中允许"选择镜像/弹出"热插拔。</summary>
public sealed class CdromDevice : VmDevice
{
    public override string DeviceType => "cdrom";
    /// <summary>当前插入的 ISO；null = 空光驱。包内相对 / 包外绝对。</summary>
    public string? IsoPath { get; set; }
}

/// <summary>
/// 网卡：NAT / 桥接 / Host-only / 断开。允许多网卡，每张独立选择模式。
/// Host-only 连接的是宿主级"虚拟网络"一级资源（SQLite 管理），不是散落在 VM 里的配置。
/// </summary>
public sealed class NetworkDevice : VmDevice
{
    public override string DeviceType => "network";
    public NetworkMode Mode { get; set; } = NetworkMode.Nat;
    /// <summary>Host-only 模式下连接的虚拟网络 ID（宿主级一级资源）。</summary>
    public string? VirtualNetworkId { get; set; }
    /// <summary>桥接模式：null = 自动选择宿主网卡；或指定 Wi-Fi/以太网/USB 网卡。</summary>
    public string? BridgeAdapter { get; set; }
    /// <summary>稳定 MAC（QEMU 参数按 deviceId 派生，保证跨启动一致）。</summary>
    public string MacAddress { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkMode
{
    Nat,
    Bridged,
    HostOnly,
    Disconnected,
}

/// <summary>显示器：单显示器（1.0 不做多虚拟显示器）；每台 VM 必须有且只需一个显示设备。</summary>
public sealed class DisplayDevice : VmDevice
{
    public override string DeviceType => "display";
    /// <summary>共享剪贴板默认开启；文件拖放默认开启（能力缺失时静默失效）。</summary>
    public bool ClipboardSharing { get; set; } = true;
    public bool FileDragAndDrop { get; set; } = true;
}

/// <summary>声音：向导默认创建并启用；底层后端自动选择，用户只见"启用声音"开关。</summary>
public sealed class AudioDevice : VmDevice
{
    public override string DeviceType => "audio";
    public bool Enabled { get; set; } = true;
}

/// <summary>USB 控制器：宿主设备默认属于 Host；用户从显示器设备栏主动"连接到此虚拟机"（运行期 QMP 管理）。</summary>
public sealed class UsbControllerDevice : VmDevice
{
    public override string DeviceType => "usb";
    public bool Enabled { get; set; } = true;
}

/// <summary>共享文件夹：默认可读写，可勾选只读。底层走 SPICE WebDAV 等机制，config 不记录协议。</summary>
public sealed class SharedFolderDevice : VmDevice
{
    public override string DeviceType => "sharedFolder";
    public string HostPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool ReadOnly { get; set; }
}

/// <summary>摄像头：功能纳入 1.0，默认关闭。</summary>
public sealed class CameraDevice : VmDevice
{
    public override string DeviceType => "camera";
    public bool Enabled { get; set; }
    /// <summary>null = 跟随宿主默认设备；或指定具体设备名。</summary>
    public string? HostDevice { get; set; }
}

/// <summary>麦克风：功能纳入 1.0，默认关闭。</summary>
public sealed class MicrophoneDevice : VmDevice
{
    public override string DeviceType => "microphone";
    public bool Enabled { get; set; }
    public string? HostDevice { get; set; }
}

/// <summary>安全芯片（TPM）：Windows 11 Profile 默认开启 TPM 2.0 + Secure Boot。</summary>
public sealed class TpmDevice : VmDevice
{
    public override string DeviceType => "tpm";
    public bool Enabled { get; set; }
    public string Version { get; set; } = "2.0";
}

/// <summary>
/// OVF/OVA 导入的 Raw/兼容设备：无法安全映射为原生设备时忠实保留原始参数，
/// 只读展示，只允许删除后重新添加。
/// </summary>
public sealed class RawDevice : VmDevice
{
    public override string DeviceType => "raw";
    public string Label { get; set; } = "";
    /// <summary>原始参数（例如 ["-device", "ich9-ahci,id=sata"]），经安全校验后原样追加。</summary>
    public List<string> Arguments { get; set; } = new();
    public string Source { get; set; } = "ovf-import";
    /// <summary>QEMU 无法表达的设备：导入时标记不可用（用户选择"仍然导入"后保留）。</summary>
    public bool Unsupported { get; set; }
}
