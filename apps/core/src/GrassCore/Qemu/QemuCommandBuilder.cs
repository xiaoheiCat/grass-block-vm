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

    public QemuCommandLine Build(string packageRoot, string? runtimeSessionId = null, string? incomingStateFile = null,
        string? spicePassword = null)
    {
        // Builder 实例可能被设置预览、重试或测试复用；bootindex 只描述本次
        // Build 的设备序列，不能把上一次构建的计数带入下一次，否则同一配置
        // 的命令行会随调用次数漂移。
        _bootSeq.Clear();
        _unrankedSlot.Clear();
        var sessionId = runtimeSessionId ?? RandomHex(12);
        var args = new List<string>
        {
            "-name", Esc(Config.Name),
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
        AddDisplayAndSpice(args, string.IsNullOrWhiteSpace(spicePassword) ? RandomHex(24) : spicePassword);

        // 挂起恢复：从保存的完整运行状态（内存/CPU/设备）回到挂起瞬间的唯一方式。
        // 注意 -incoming 的值是普通字符串（迁移 URI），不经 QemuOpts 解析、不按逗号
        // 切分——与 migrate 命令的 JSON 参数保持一致：都原样传，不做 Esc
        if (incomingStateFile is not null)
            args.AddRange(new[] { "-incoming", $"file:{incomingStateFile}" });

        // 全部网卡"断开"（或没有网卡）：显式 -nic none。否则 QEMU 会静默补一张默认
        // 用户态 NAT 网卡——用户以为断网了，实际上网照样通（消费级产品的安全性不能靠默认值）。
        // 只有【后端】选项（-netdev/-nic/-net）才会让 QEMU 不补默认网卡；裸 -device <网卡型号>
        // 不带后端 = 一张没有网络的网卡，不影响默认网卡的判定（不能因为它而漏加
        // -nic none——那会重新放开隐式 NAT，正好违背上面要堵的洞）。后端选项本身
        // 在 RawArgumentBlocklist 里到不了这里，这里再查一遍是纵深防御
        bool HasNicBackendArg(IReadOnlyList<string> a)
        {
            string prevNorm = "";
            for (var i = 0; i < a.Count; i++)
            {
                // 连字前缀归一化（与 AddRawDevices 的黑名单同一理由：--netdev 与 -netdev 同义）
                var norm = "-" + a[i].TrimStart('-');
                // 值位跳过：-name 的值是用户自由文本（合法 VM 名可以就叫 "-nic"——
                // 名称校验只挡逗号/等号/非法字符）。把值当选项名会错判"已有网卡
                // 后端"而漏发 -nic none = 静默复活 QEMU 默认 NAT 网卡
                var isValueOfName = prevNorm == "-name";
                prevNorm = norm;
                if (isValueOfName) continue;
                if (norm is "-netdev" or "-nic" or "-network") return true;
                // -net 的别名形态：-net user/tap/bridge…（独立 token 以 -net 开头但不是 -netdev）
                if (norm == "-net" && i + 1 < a.Count
                    && (a[i + 1] is "none" || a[i + 1].StartsWith("user", StringComparison.Ordinal)
                        || a[i + 1].StartsWith("tap", StringComparison.Ordinal)
                        || a[i + 1].StartsWith("bridge", StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }
        if (!HasNicBackendArg(args))
            args.AddRange(new[] { "-nic", "none" });

        // QMP：Windows Named Pipe（-mon mode=control）。Core 崩溃后凭 session.json + 此管道无损接管。
        // pipe 后端只认 path= 选项（name= 会让 QEMU 参数解析即退出）；且 QEMU 自己在
        // Windows 上补 \\.\pipe\ 前缀——这里传裸名。客户端仍用全限定名连接。
        var qmpPipe = $@"\\.\pipe\grassvm-qmp-{sessionId}";
        args.AddRange(new[]
        {
            "-chardev", $"pipe,id=qmpchar,path=grassvm-qmp-{sessionId}",
            "-mon", "chardev=qmpchar,mode=control",
        });
        return new QemuCommandLine { Args = args, QmpPipeName = qmpPipe, PackageRoot = packageRoot };
    }

    private void AddFirmware(List<string> args, string packageRoot)
    {
        if (_profile.Firmware == FirmwareKind.Uefi)
        {
            // Secure Boot 用 OVMF.secboot 变体；NVRAM 变量卷为每 VM 独立文件（firmware/ 下，随包移动）
            var code = Config.Firmware.SecureBoot ? "OVMF_CODE.secboot.fd" : "OVMF_CODE.fd";
            args.AddRange(new[] { "-drive", $"if=pflash,format=raw,readonly=on,file={Esc(Combine(_ovmfDir, code))}" });
            var nvram = Combine(packageRoot, GrassVmPackage.FirmwareDir, "VARS.fd");
            args.AddRange(new[] { "-drive", $"if=pflash,format=raw,file={Esc(nvram)}" });
        }
        // 传统 BIOS：SeaBIOS 内置于 QEMU，无需参数

        // TPM 2.0：需要 GrassCore 先把 TPM 模拟器宿主进程拉起（管道就绪）才能生成这三组参数；
        // 模拟器宿主随 GrassSpiceHelper/Windows 实机阶段交付。宿主未就绪时跳过 TPM 参数
        // （配置里的用户意图保留，将来宿主可用即恢复），否则 QEMU 初始化即失败。
        if (Config.Firmware.Tpm && Config.DevicesOfType<TpmDevice>().Any(t => t.Enabled) && TpmHostReady)
        {
            args.AddRange(new[] { "-chardev", "pipe,id=chrtpm,path=grassvm-tpm-emulator" });
            args.AddRange(new[] { "-tpmdev", "emulator,id=tpm0,chardev=chrtpm" });
            args.AddRange(new[] { "-device", "tpm-tis,tpmdev=tpm0" });
        }
    }


    /// <summary>QEMU 选项串里 "," 是参数分隔符：值内逗号必须写成 ",,"（如含逗号的用户目录路径）。</summary>
    private static string Esc(string value) => value.Replace(",", ",,");

    private void AddDevice(List<string> args, VmDevice device, string packageRoot, ref int sataPort)
    {
        switch (device)
        {
            case DiskDevice disk:
            {
                var abs = PathPolicy.Resolve(new GrassVmPackage(packageRoot), disk.Path);
                var id = "disk" + disk.CreatedOrder;
                args.AddRange(new[] { "-drive", $"file={Esc(abs)},if=none,format=qcow2,id={id}" });
                switch (_profile.SystemDiskBus)
                {
                    case DiskBus.Sata:
                        // q35 内建 ich9-ahci：QEMU 对外暴露为 ide.0–ide.5 六个 SATA 端口，
                        // 超限必须报错而非回绕。
                        if (sataPort >= 6)
                            throw new InvalidOperationException("SATA 设备数量超出上限（6）。请移除一些设备后再启动。");
                        args.AddRange(new[] { "-device", $"ide-hd,drive={id},bus=ide.{sataPort},bootindex={BootIndex(BootClass.Disk)}" });
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
                // 安装镜像缺失 = 空光驱启动（预检把缺 ISO 归为非致命，承诺"启动后
                // 可在显示器窗口更换介质"）。这里若照发 file=<不存在路径>，QEMU
                // 打不开文件【启动即退】——"非致命"变成"永远开不了机的死路"，
                // 显示器换介质恰恰需要一台已经跑起来的 VM
                var isoPath = cd.IsoPath is null ? null : PathPolicy.Resolve(new GrassVmPackage(packageRoot), cd.IsoPath);
                if (isoPath is not null
                    && (!File.Exists(isoPath)
                        || !string.Equals(Path.GetExtension(isoPath), ".iso", StringComparison.OrdinalIgnoreCase)))
                    isoPath = null;
                var media = isoPath is null
                    ? "media=cdrom"
                    // format=raw：省略时 QEMU 会做整盘格式探测——guest 能喂给它一个
                    // 看起来像 qcow2 的"ISO"（VMDK/VHDI 均可），probe 结果变成可写后端
                    : $"media=cdrom,file={Esc(isoPath)},format=raw";
                args.AddRange(new[] { "-drive", $"if=none,{media},id={id},readonly=on" });
                // 光驱接到 SATA/IDE；热插拔换盘由 QMP blockdev-change-medium 完成
                if (_profile.SystemDiskBus == DiskBus.Virtio || _profile.Machine == MachineKind.Q35)
                {
                    if (sataPort >= 6)
                        throw new InvalidOperationException("SATA 设备数量超出上限（6）。请移除一些设备后再启动。");
                        args.AddRange(new[] { "-device", $"ide-cd,drive={id},bus=ide.{sataPort},bootindex={BootIndex(BootClass.Cd)}" });
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
                var mac = string.IsNullOrEmpty(net.MacAddress)
                    ? DeriveMac(net.DeviceId)
                    : DeviceIdPolicy.IsValidMac(net.MacAddress)
                        ? net.MacAddress
                        : throw new InvalidOperationException("网卡 MAC 地址格式无效（必须是六组十六进制字节）。");
                // "断开"= 不生成任何网络参数（QEMU 没有 none 后端；-netdev none 是非法参数，
                // 会让 QEMU 初始化即退出）。设备保留在配置里，用户随时可以再接上。
                if (net.Mode == NetworkMode.Disconnected)
                    break;
                var netdev = net.Mode switch
                {
                    NetworkMode.Nat => $"user,id={id}",
                    // 桥接 / Host-only：使用安装器创建并重命名的固定 TAP 适配器。
                    // BridgeAdapter 是宿主物理网卡选择，不能直接作为 ifname；
                    // 旧实现按 DeviceId 生成 GrassVM-Tap-*，但安装器只创建了
                    // tap0901，因此 QEMU 启动时会因找不到适配器而失败。
                    NetworkMode.Bridged => $"tap,id={id},ifname={TapNetwork.AdapterName},script=no,downscript=no",
                    NetworkMode.HostOnly when !string.IsNullOrWhiteSpace(net.VirtualNetworkId)
                        => $"tap,id={id},ifname={TapNetwork.AdapterName},script=no,downscript=no",
                    NetworkMode.HostOnly => throw new InvalidOperationException("Host-only 网卡未选择虚拟网络。请先选择一个宿主虚拟网络。"),
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

    /// <summary>同类设备内递增序号（每设备 bootindex 必须唯一：QEMU 在 realize 时拒绝重复值）。</summary>
    private readonly Dictionary<BootClass, int> _bootSeq = new();
    /// <summary>未进 BootOrder 的类各占一段互不重叠的高位区间（否则两类的首台都拿同一个基数）。</summary>
    private readonly Dictionary<BootClass, int> _unrankedSlot = new();

    private int BootIndex(BootClass @class)
    {
        var i = Config.BootOrder.IndexOf(@class);
        // 类不在 BootOrder（用户手改配置）：顺延到 BootOrder 之后的独立区段，仍参与
        // 同一套动态宽度计算，避免与排位类或其他未排名类撞上 bootindex。
        int classRank;
        if (i >= 0)
        {
            classRank = i;
        }
        else
        {
            if (!_unrankedSlot.TryGetValue(@class, out var slot))
                slot = _unrankedSlot[@class] = _unrankedSlot.Count + 1;
            classRank = Config.BootOrder.Count + slot;
        }
        var n = _bootSeq.TryGetValue(@class, out var v) ? v : 0;
        _bootSeq[@class] = n + 1;
        // 同类设备的 bootindex 必须构成【连续区段】：QEMU 按数值全局排序。
        // 区段宽度按当前配置设备总数动态计算，不能写死为 100：服务层允许 128
        // 个设备，同一类超过 100 个时固定百位区段会与下一类重叠，QEMU realize
        // 将因重复 bootindex 拒绝启动。保留 100 作为小配置的稳定基数；设备总数
        // 至少为 1，避免空配置时出现零区段。
        var segmentWidth = Math.Max(100, Config.Devices.Count);
        return checked((classRank + 1) * segmentWidth + n + 1);
    }

    private void AddRawDevices(List<string> args)
    {
        // Raw/兼容设备：忠实保留导入的原始参数，只读展示、只能删除后重新添加。
        // 标记 Unsupported 的（用户确认"仍然导入"的不可信/无法支持设备）绝不进
        // 命令行。安全校验：不允许原始参数劫持控制通道或替换关键全局参数。
        foreach (var raw in Config.DevicesOfType<RawDevice>().Where(r => !r.Unsupported))
        {
            if (raw.Arguments.Count == 0 || raw.Arguments.Count % 2 != 0)
                throw new InvalidOperationException("兼容设备参数结构不完整（必须是 -device 与设备值成对出现）。");
            for (var i = 0; i < raw.Arguments.Count; i += 2)
            {
                var option = raw.Arguments[i];
                var norm = "-" + option.TrimStart('-');
                if (!string.Equals(norm, "-device", StringComparison.Ordinal))
                    throw new InvalidOperationException($"兼容设备的参数不被允许：{option}");
                var value = raw.Arguments[i + 1];
                if (value.StartsWith('-'))
                    throw new InvalidOperationException("兼容设备参数结构不完整（设备值不能是另一个选项）。");
                if (DeviceValueReferencesHostFile(value)
                    || value.Contains("drive=", StringComparison.OrdinalIgnoreCase)
                    )
                    throw new InvalidOperationException("兼容设备不允许引用宿主文件或物理设备（file=/path=/romfile=/host= 等）。");
            }
            args.AddRange(raw.Arguments);
        }
    }

    /// <summary>-device 值里出现的"宿主文件/后端引用"属性（QemuOpts 键不区分大小写）。</summary>
    private static readonly string[] HostFileProps =
    {
        "file=", "path=", "romfile=", "chardev=", "netdev=", "fsdev=", "fd=", "bios=",
        // host= = 宿主 PCI/USB 物理设备地址直通（vfio-pci,host=00:02.0）：
        // 与 usb-host 型号同类的宿主硬件透传，不含任何文件引用键
        "host=",
    };

    internal static bool DeviceValueReferencesHostFile(string deviceValue) =>
        HostFileProps.Any(p => deviceValue.Contains(p, StringComparison.OrdinalIgnoreCase))
        // usb-host = 把宿主 USB 物理设备（U 盘/安全密钥/手机）直接透传进客户机，
        // 不含任何 file=/path= 这类文件引用键，靠属性黑名单拦不住。产品模型里
        // 宿主 USB 设备默认属于 Host，用户要在显示器设备栏主动"连接到此虚拟机"
        //——兼容设备参数静默透传绕过这道用户授权。型号段 = 首个逗号前（忽略空白）
        || deviceValue.Split(',')[0].Trim().Equals("usb-host", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> RawArgumentBlocklist = new()
    {
        // 控制通道 / 全局形态（劫持 Core 与 QEMU 的连接或生命周期）。
        // -qmp-pretty 是 -qmp 的别名形态（同样开控制服务器）；-trace 以
        // events=/file= 读写宿主文件（与 -D/-readconfig 同类）。
        // -no-shutdown/-no-reboot/-action/-watchdog-action 改写"客户机关机 =
        // QEMU 进程退出"这一 Core 状态机的根基：QEMU 永不退出 → Exited 不触发
        // → vm.lock 永不释放、Library 永远显示运行中、Core 永不空闲退出
        "-qmp", "-qmp-pretty", "-mon", "-chardev", "-serial", "-parallel", "-monitor", "-daemonize", "-snapshot",
        "-no-shutdown", "-no-reboot", "-action", "-watchdog-action",
        "-spice", "-display", "-vnc", "-add-fd", "-pidfile", "-incoming", "-accel", "-machine", "-M",
        // -nographic 把串口和 HMP 监视器都复用到 stdio（正是上面注释里的经典劫持
        // 形态）；-curses 同理占终端；-sdl 开 QEMU 自有的显示窗口（"用户永远
        // 不该看到 QEMU"的破口）；-gdb/-s/-S 开宿主调试口/冻结启动
        "-nographic", "-curses", "-sdl", "-gdb", "-s", "-S",
        // 宿主资源读写（不受控地打开宿主文件/后端 = 数据外泄与破坏面）：
        // -drive/-blockdev 可把宿主任意文件挂成客户机磁盘；-netdev/-nic/-object
        // 可建宿主后端；-bios/-L/-plugin/-fw_cfg/-audiodev 可加载宿主代码/固件
        "-drive", "-blockdev", "-netdev", "-nic", "-object", "-bios", "-L", "-plugin",
        "-fw_cfg", "-audiodev", "-trace",
        // 恢复/迁移形态（绕过挂起指纹校验直接重放状态）
        "-loadvm",
        // 设备全局微调（可改写固件可见形态）
        "-global",
        // 关键全局标量（QEMU 末位生效）：Core 自己在前面发出的 -m/-smp/-cpu/
        // -name/-boot/-rtc/-vga 会被这些重复项静默改写——内存/CPU/启动顺序/
        // 时钟/显卡形态全部失真，且用户毫无感知
        "-m", "-smp", "-cpu", "-name", "-boot", "-rtc", "-vga",
        // 宿主文件 → 客户机可见表的载入器（与 -option-rom/-fw_cfg 同类）：
        // -acpitable file=… / -smbios file=… 都能把宿主任意文件内容塞进
        // 客户机固件可见的 ACPI/SMBIOS 表
        "-acpitable", "-smbios",
        // 宿主文件别名/短选项（与 -drive 同效或更直接的宿主访问）：
        // -hda..-hdd/-cdrom/-fda/-fdb/-sd/-mtdblock 直接把宿主文件挂成磁盘；
        // -pflash 以【可写】方式打开宿主文件；-kernel/-initrd 把宿主文件载入客户机；
        // -usbdevice disk:<文件> 把宿主文件挂成客户机可读写的 USB 存储盘（同类洞）；
        // -virtfs/-fsdev 向客户机共享宿主任意目录；-net 可建 TAP/桥接宿主后端；
        "-usbdevice",
        // -D/-debugcon 往宿主任意路径写文件
        "-hda", "-hdb", "-hdc", "-hdd", "-cdrom", "-fda", "-fdb", "-sd", "-mtdblock",
        "-pflash", "-kernel", "-initrd", "-virtfs", "-fsdev", "-net", "-D", "-debugcon",
        // 配置注入/改写（结构性绕过点）：
        // -readconfig 从宿主文件加载任意 [drive]/[netdev]/[device] 配置 = 整个
        // blocklist 的旁路；-writeconfig 往宿主任意路径写；-option-rom 把宿主
        // 二进制载入客户机固件；-set 可改写已有选项（如 drive.<id>.file=宿主路径）；
        // -mem-path 用宿主文件当客户机内存后端
        "-readconfig", "-writeconfig", "-option-rom", "-set", "-mem-path",
    };

    private void AddDisplayAndSpice(List<string> args, string? spicePassword)
    {
        // 每台 VM 必须有显示设备；一台 VM 同时只允许一个"显示器"窗口。
        if (!Config.HasDisplayDevice)
            throw new InvalidOperationException("此虚拟机没有显示设备。Grass Block VM 不支持无显示器虚拟机。");
        // QXL 2D：Windows 旧版/新版 Guest 兼容性最好的稳定路径（3D 加速完全隐藏，不做实验性开关）
        // 显式关闭 QEMU 默认的 std VGA，确保“一台 VM 一个显示设备”而不是
        // qxl-vga + 默认 VGA 的双显卡组合（部分 Guest 会选错输出通道）。
        args.AddRange(new[] { "-vga", "none", "-device", "qxl-vga" });
        // SPICE 只监听本机；端口自动分配（port=0），由 Core 经 QMP query-spice 获取后交给显示器窗口/桥。
        // 每次构建都必须启用 SPICE ticket，避免同机其他用户扫描回环端口后绕过
        // Electron 的一次性 WebSocket token 直接接管画面/键鼠。生产启动路径传入
        // 会话随机 ticket；低层预览/测试路径也生成随机 ticket，绝不回退到未认证模式。
        var spice = $"addr=127.0.0.1,port=0,password={Esc(spicePassword!)},disable-ticketing=off";
        args.AddRange(new[] { "-spice", spice });
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
