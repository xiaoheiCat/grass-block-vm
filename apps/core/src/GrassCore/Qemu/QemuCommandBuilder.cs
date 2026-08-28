using System.Security.Cryptography;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Profiles;

namespace GrassCore.Qemu;

/// <summary>QEMU 启动命令（qemu-system-x86_64.exe 的完整参数 + 会话端点元数据）。</summary>
public sealed record QemuCommandLine
{
    public required IReadOnlyList<string> Args { get; init; }
    /// <summary>包根目录（日志/会话产物定位用；可为空以兼容单测）。</summary>
    public string? PackageRoot { get; init; }
    /// <summary>本次会话的 QMP Windows Named Pipe（\\.\pipe\grassvm-qmp-&lt;sessionId&gt;）。</summary>
    public required string QmpPipeName { get; init; }
    /// <summary>SPICE 服务绑定 127.0.0.1，端口由 QEMU 自动分配（port=0），经 QMP query-spice 查询。</summary>
    public const string SpiceHost = "127.0.0.1";
    public override string ToString() => "qemu-system-x86_64.exe " + string.Join(' ', Args.Select(AutoQuote));
    private static string AutoQuote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}

/// <summary>
/// QemuCommandBuilder：把 VmConfiguration（"虚拟电脑"）转换为 QEMU 参数。
/// UI 永远不碰 QEMU 字符串；QEMU 参数变化只改这里。
/// 硬性规则：只使用硬件虚拟化（-accel whpx），初始化失败由启动预检阻止，绝不回退 TCG。
/// </summary>
public sealed class QemuCommandBuilder
{
    private readonly string _ovmfDir; // bundled firmware（安装包自带，readonly pflash 代码卷）
    private readonly OsProfile _profile;

    public QemuCommandBuilder(VmConfiguration config, string ovmfDirectory)
    {
        Config = config;
        _profile = OsProfileLibrary.ById(config.OsProfileId);
        _ovmfDir = ovmfDirectory;
    }

    public VmConfiguration Config { get; }

    /// <summary>TPM 模拟器宿主是否就绪（默认否：宿主进程随 Windows 实机阶段交付）。</summary>
    public bool TpmHostReady { get; init; }

    public QemuCommandLine Build(string packageRoot, string? runtimeSessionId = null, string? incomingStateFile = null)
    {
        var sessionId = runtimeSessionId ?? RandomHex(12);
        var args = new List<string>
        {
            "-name", Config.Name,
            "-machine", _profile.Machine switch
            {
                MachineKind.Q35 => "q35",
                MachineKind.Pc => "pc",
                _ => throw new InvalidOperationException(),
            },
            // 只使用硬件虚拟化；失败即阻止启动（WhpxCapability 预检 + QEMU 打开磁盘结果双重把关）
            "-accel", "whpx",
            // CPU 默认尽可能使用宿主 CPU 能力（挂起状态因此有明确宿主环境边界）
            "-cpu", "max",
            "-smp", Config.CpuCores.ToString(),
            "-m", Config.MemoryMiB.ToString() + "M",
            "-rtc", "base=localtime",
        };

        AddFirmware(args, packageRoot);
        var nextDiskPort = 0;
        foreach (var device in Config.Devices.OrderBy(d => d.CreatedOrder))
        {
            AddDevice(args, device, packageRoot, ref nextDiskPort);
        }
        AddRawDevices(args);
        AddDisplayAndSpice(args);

        // 挂起恢复：从保存的完整运行状态（内存/CPU/设备）回到挂起瞬间的唯一方式
        if (incomingStateFile is not null)
            args.AddRange(new[] { "-incoming", $"file:{incomingStateFile}" });

        // QMP：Windows Named Pipe（-mon mode=control）。Core 崩溃后凭 session.json + 此管道无损接管。
        var qmpPipe = $@"\\.\pipe\grassvm-qmp-{sessionId}";
        args.AddRange(new[] { "-chardev", $"pipe,id=qmpchar,name={qmpPipe}", "-mon", "chardev=qmpchar,mode=control" });
        return new QemuCommandLine { Args = args, QmpPipeName = qmpPipe, PackageRoot = packageRoot };
    }

    private void AddFirmware(List<string> args, string packageRoot)
    {
        if (_profile.Firmware == FirmwareKind.Uefi)
        {
            // Secure Boot 用 OVMF.secboot 变体；NVRAM 变量卷为每 VM 独立文件（firmware/ 下，随包移动）
            var code = Config.Firmware.SecureBoot ? "OVMF_CODE.secboot.fd" : "OVMF_CODE.fd";
            args.AddRange(new[] { "-drive", $"if=pflash,format=raw,readonly=on,file={Combine(_ovmfDir, code)}" });
            var nvram = Combine(packageRoot, GrassVmPackage.FirmwareDir, "VARS.fd");
            args.AddRange(new[] { "-drive", $"if=pflash,format=raw,file={nvram}" });
        }
        // 传统 BIOS：SeaBIOS 内置于 QEMU，无需参数

        // TPM 2.0：需要 GrassCore 先把 TPM 模拟器宿主进程拉起（管道就绪）才能生成这三组参数；
        // 模拟器宿主随 GrassSpiceHelper/Windows 实机阶段交付。宿主未就绪时跳过 TPM 参数
        // （配置里的用户意图保留，将来宿主可用即恢复），否则 QEMU 初始化即失败。
        if (Config.Firmware.Tpm && Config.DevicesOfType<TpmDevice>().Any(t => t.Enabled) && TpmHostReady)
        {
            args.AddRange(new[] { "-chardev", "pipe,id=chrtpm,name=\\\\.\\pipe\\grassvm-tpm-emulator" });
            args.AddRange(new[] { "-tpmdev", "emulator,id=tpm0,chardev=chrtpm" });
            args.AddRange(new[] { "-device", "tpm-tis,tpmdev=tpm0" });
        }
    }

    private void AddDevice(List<string> args, VmDevice device, string packageRoot, ref int sataPort)
    {
        switch (device)
        {
            case DiskDevice disk:
            {
                var abs = PathPolicy.Resolve(new GrassVmPackage(packageRoot), disk.Path);
                var id = "disk" + disk.CreatedOrder;
                args.AddRange(new[] { "-drive", $"file={abs},if=none,format=qcow2,id={id}" });
                switch (_profile.SystemDiskBus)
                {
                    case DiskBus.Sata:
                        // q35 内建 ich9-ahci：每控制器 6 口（ahci0.0–ahci0.5），超限必须报错而非回绕
                        if (sataPort >= 6)
                            throw new InvalidOperationException("SATA 设备数量超出上限（6）。请移除一些设备后再启动。");
                        args.AddRange(new[] { "-device", $"ide-hd,drive={id},bus=ahci0.{sataPort},bootindex={BootIndex(BootClass.Disk)}" });
                        sataPort++;
                        break;
                    case DiskBus.Ide:
                        // pc(i440fx) 内建 IDE：ide.0/ide.1 两条总线，unit 0/1（共 4 设备）
                        if (sataPort >= 4)
                            throw new InvalidOperationException("IDE 设备数量超出上限（4）。请移除一些设备后再启动。");
                        args.AddRange(new[] { "-device", $"ide-hd,drive={id},bus=ide.{sataPort / 2},unit={sataPort % 2},bootindex={BootIndex(BootClass.Disk)}" });
                        sataPort++;
                        break;
                    case DiskBus.Virtio:
                        args.AddRange(new[] { "-device", $"virtio-blk-pci,drive={id},bootindex={BootIndex(BootClass.Disk)}" });
                        break;
                }
                break;
            }
            case CdromDevice cd:
            {
                var id = "cd" + cd.CreatedOrder;
                var media = cd.IsoPath is null
                    ? "media=cdrom"
                    : $"media=cdrom,file={PathPolicy.Resolve(new GrassVmPackage(packageRoot), cd.IsoPath)}";
                args.AddRange(new[] { "-drive", $"if=none,{media},id={id},readonly=on" });
                // 光驱接到 SATA/IDE；热插拔换盘由 QMP blockdev-change-medium 完成
                if (_profile.SystemDiskBus == DiskBus.Virtio || _profile.Machine == MachineKind.Q35)
                {
                    if (sataPort >= 6)
                        throw new InvalidOperationException("SATA 设备数量超出上限（6）。请移除一些设备后再启动。");
                    args.AddRange(new[] { "-device", $"ide-cd,drive={id},bus=ahci0.{sataPort},bootindex={BootIndex(BootClass.Cd)}" });
                }
                else
                {
                    if (sataPort >= 4)
                        throw new InvalidOperationException("IDE 设备数量超出上限（4）。请移除一些设备后再启动。");
                    args.AddRange(new[] { "-device", $"ide-cd,drive={id},bus=ide.{sataPort / 2},unit={sataPort % 2},bootindex={BootIndex(BootClass.Cd)}" });
                }
                sataPort++;
                break;
            }
            case NetworkDevice net:
            {
                var id = "net" + net.CreatedOrder;
                var mac = string.IsNullOrEmpty(net.MacAddress) ? DeriveMac(net.DeviceId) : net.MacAddress;
                // "断开"= 不生成任何网络参数（QEMU 没有 none 后端；-netdev none 是非法参数，
                // 会让 QEMU 初始化即退出）。设备保留在配置里，用户随时可以再接上。
                if (net.Mode == NetworkMode.Disconnected)
                    break;
                var netdev = net.Mode switch
                {
                    NetworkMode.Nat => $"user,id={id}",
                    // 桥接 / Host-only：TAP-Windows6 适配器（安装器已部署 Grass Block VM Virtual Ethernet Adapter）
                    // 桥接目标宿主网卡默认"自动选择"，用户可手动指定；该选择保存在宿主级配置中
                    NetworkMode.Bridged => $"tap,id={id},ifname={TapName(net)},script=no,downscript=no",
                    NetworkMode.HostOnly => $"tap,id={id},ifname={TapName(net)},script=no,downscript=no",
                    _ => throw new InvalidOperationException(),
                };
                args.AddRange(new[] { "-netdev", netdev });
                var model = _profile.Nic switch
                {
                    NicModel.E1000 => "e1000",       // Windows 安装程序原生可识别
                    NicModel.Virtio => "virtio-net-pci", // 现代内核自带驱动
                    _ => throw new InvalidOperationException(),
                };
                // 网络启动位于默认启动顺序第三位
                args.AddRange(new[] { "-device", $"{model},netdev={id},mac={mac},bootindex={BootIndex(BootClass.Network)}" });
                break;
            }
            case DisplayDevice:
                // qxl-vga 在 AddDisplayAndSpice 统一处理（保证唯一显示设备）
                break;
            case AudioDevice audio when audio.Enabled:
                // 稳定 2D/音频路径：intel-hda + hda-duplex，宿主后端由 QEMU 自动选择
                args.AddRange(new[] { "-device", "intel-hda", "-device", "hda-duplex" });
                break;
            case UsbControllerDevice usb when usb.Enabled:
                args.AddRange(new[] { "-device", "qemu-xhci" });
                break;
            case TpmDevice:
            case CameraDevice:
            case MicrophoneDevice:
            case SharedFolderDevice:
                // 摄像头/麦克风默认关闭；启用时经 USB passthrough 由运行期 QMP 接入，不进启动参数
                // 共享文件夹走 SPICE WebDAV（Guest 帮助程序能力），不是 QEMU 启动参数
                break;
        }
    }

    private static string TapName(NetworkDevice net) => $"GrassVM-Tap-{Math.Abs(net.DeviceId.GetHashCode()) % 10000}";

    private int BootIndex(BootClass @class)
    {
        var i = Config.BootOrder.IndexOf(@class);
        return i < 0 ? int.MaxValue / 2 : i + 1;
    }

    private void AddRawDevices(List<string> args)
    {
        // Raw/兼容设备：忠实保留导入的原始参数，只读展示、只能删除后重新添加。
        // 安全校验：不允许原始参数劫持控制通道或替换关键全局参数。
        foreach (var raw in Config.DevicesOfType<RawDevice>())
        {
            for (var i = 0; i < raw.Arguments.Count; i++)
            {
                var a = raw.Arguments[i];
                if (i == 0 || !raw.Arguments[i - 1].StartsWith('-'))
                {
                    if (RawArgumentBlocklist.Contains(a))
                        throw new InvalidOperationException($"兼容设备的参数不被允许：{a}");
                }
            }
            args.AddRange(raw.Arguments);
        }
    }

    private static readonly HashSet<string> RawArgumentBlocklist = new()
    {
        "-qmp", "-mon", "-chardev", "-serial", "-parallel", "-monitor", "-daemonize", "-snapshot",
        "-spice", "-display", "-vnc", "-add-fd", "-pidfile", "-incoming", "-accel", "-machine",
    };

    private void AddDisplayAndSpice(List<string> args)
    {
        // 每台 VM 必须有显示设备；一台 VM 同时只允许一个"显示器"窗口。
        if (!Config.HasDisplayDevice)
            throw new InvalidOperationException("此虚拟机没有显示设备。Grass Block VM 不支持无显示器虚拟机。");
        // QXL 2D：Windows 旧版/新版 Guest 兼容性最好的稳定路径（3D 加速完全隐藏，不做实验性开关）
        args.AddRange(new[] { "-device", "qxl-vga" });
        // SPICE 只监听本机；端口自动分配（port=0），由 Core 经 QMP query-spice 获取后交给显示器窗口/桥
        args.AddRange(new[] { "-spice", "addr=127.0.0.1,port=0,disable-ticketing=on" });
        // 不加 -display 时 QEMU 会打开自己编译的默认 UI（GTK/SDL）——用户绝不能看见 QEMU。
        // SPICE 是唯一显示路径。
        args.AddRange(new[] { "-display", "none" });
    }

    private static string DeriveMac(string deviceId)
    {
        // 每设备稳定 MAC：QEMU 常用本地管理地址段 52:54:00
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deviceId));
        return $"52:54:00:{hash[0]:x2}:{hash[1]:x2}:{hash[2]:x2}";
    }

    private static string Combine(params string[] parts) => System.IO.Path.Combine(parts);

    private static string RandomHex(int bytes)
    {
        var b = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}
