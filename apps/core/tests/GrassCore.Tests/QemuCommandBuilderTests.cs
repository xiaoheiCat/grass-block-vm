using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Profiles;
using GrassCore.Qemu;
using Xunit;

namespace GrassCore.Tests;

public class QemuCommandBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GrassVmPackage _pkg;

    public QemuCommandBuilderTests()
    {
        Directory.CreateDirectory(_dir);
        _pkg = GrassVmPackage.CreateNew(_dir, "Builder VM");
        Directory.CreateDirectory(_pkg.DisksPath);
        File.WriteAllText(Path.Combine(_pkg.DisksPath, "system.qcow2"), "fake");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string[] Build(VmConfiguration config, string packagePath, string sessionId = "abc123")
        => new QemuCommandBuilder(config, Path.Combine(packagePath, "fw"))
            .Build(packagePath, sessionId, spicePassword: "test-spice-password")
            .Args.ToArray();

    [Fact]
    public void Windows11Profile_UsesSataDisk_E1000Nic_UefiTpm_SecureBoot()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "Builder VM");
        // 向导创建的系统盘
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 80L * 1024 * 1024 * 1024, CreatedOrder = 10 });
        var args = Build(config, _pkg.Path);
        var j = string.Join(" ", args);

        Assert.Contains("-machine q35", j);
        Assert.Contains("-accel whpx", j);                       // 只使用硬件虚拟化
        Assert.DoesNotContain("tcg", j);                         // 绝不回退 TCG
        Assert.Contains("-cpu max", j);                          // 尽可能使用宿主 CPU 能力
        Assert.Contains("ide-hd,drive=disk10,bus=ide.0", j);     // Windows 系统盘默认 SATA/AHCI
        Assert.DoesNotContain("virtio-blk", j);
        Assert.Contains("-device e1000,netdev=net", j);          // Windows 原生可识别网卡
        Assert.Contains("OVMF_CODE.secboot.fd", j);              // Secure Boot 固件
        // TPM 参数依赖 GrassCore 先拉起模拟器宿主（Windows 实机阶段交付）；
        // 宿主未就绪时跳过（配置里的意图保留），否则 QEMU 初始化即失败
        Assert.DoesNotContain("tpm-tis", j);
        Assert.Contains("-vga none", j);                        // 禁止 QEMU 默认再补一张 std VGA
        Assert.Contains("-display none", j);                     // 用户永远看不见 QEMU 自己的窗口
        Assert.Contains("-rtc base=localtime", j);               // Windows 期望本地时间 RTC
    }

    [Fact]
    public void TpmArgs_EmittedOnlyWhenHostReady()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "TPM VM");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 80L * 1024 * 1024 * 1024, CreatedOrder = 10 });
        var withHost = string.Join(" ", Build(config, _pkg.Path));

        // 宿主就绪：TPM 2.0 参数生成
        var builder = new QemuCommandBuilder(config, Path.Combine(_dir, "fw")) { TpmHostReady = true };
        var ready = string.Join(" ", builder.Build(_pkg.Path).Args);

        Assert.DoesNotContain("tpm-tis", withHost);   // 默认：宿主未就绪
        Assert.Contains("tpm-tis", ready);            // 宿主就绪：TPM 2.0 生效
    }

    [Fact]
    public void LinuxProfile_UsesVirtioDiskAndNic()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 40L * 1024 * 1024 * 1024, CreatedOrder = 10 });
        var args = Build(config, _pkg.Path);
        var j = string.Join(" ", args);

        Assert.Contains("virtio-blk-pci,drive=disk10", j);
        Assert.Contains("virtio-net-pci,netdev=net", j);
        Assert.Contains("OVMF_CODE.fd", j);
        Assert.DoesNotContain("secboot", j);
        Assert.DoesNotContain("tpm-tis", j);
    }

    [Fact]
    public void LegacyProfile_UsesBios_Ide_E1000()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("windows-xp", "Builder VM");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 20L * 1024 * 1024 * 1024, CreatedOrder = 10 });
        var args = Build(config, _pkg.Path);
        var j = string.Join(" ", args);

        Assert.Contains("-machine pc", j);
        Assert.Contains("ide-hd,drive=disk10,bus=ide.", j);
        Assert.DoesNotContain("pflash", j);                      // 传统 BIOS 不需要 OVMF
    }

    [Fact]
    public void BootOrder_Default_DiskCdNetwork_With_Bootindex()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "Builder VM");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 1, CreatedOrder = 10 });
        config.Devices.Add(new CdromDevice { IsoPath = "C:\\iso\\win11.iso", CreatedOrder = 11 });
        var args = Build(config, _pkg.Path);
        var j = string.Join(" ", args);

        // 默认启动顺序 硬盘 → CD/DVD → 网络启动（区段 bootindex：硬盘 101…、
        // 光驱 201…、网络 301…；空槽位无影响）
        Assert.Contains("ide-hd,drive=disk10,bus=ide.0,bootindex=101", j);
        Assert.Contains("ide-cd,drive=cd11,bus=ide.1,bootindex=201", j);
        Assert.Contains(",bootindex=301", j); // 网络
    }

    [Fact]
    public void Build_ReusedBuilder_IsDeterministic()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Reusable Builder");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 8, CreatedOrder = 10 });
        config.Devices.Add(new DiskDevice { Path = "disks/data.qcow2", SizeBytes = 8, CreatedOrder = 11 });
        var builder = new QemuCommandBuilder(config, Path.Combine(_pkg.Path, "fw"));

        var first = builder.Build(_pkg.Path, "stable-session", spicePassword: "test-spice-password");
        var second = builder.Build(_pkg.Path, "stable-session", spicePassword: "test-spice-password");

        Assert.Equal(first.Args, second.Args);
        Assert.Equal(first.QmpPipeName, second.QmpPipeName);
    }

    [Fact]
    public void Cd_MissingIso_BootsWithEmptyDrive_NotDeadEnd()
    {
        // 缺失的安装镜像（用户清理 Downloads 是常态）必须降级为空光驱：照发
        // file=<不存在路径> 会让 QEMU 打不开文件【启动即退】——预检承诺的
        // "空光驱启动、运行后换介质"就成了永远到不了的谎言（显示器换介质
        // 恰恰需要一台已经跑起来的 VM）
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "CdMissing");
        config.Devices.Add(new CdromDevice { IsoPath = "C:/definitely/missing-安装镜像.iso", CreatedOrder = 20 });
        var j = string.Join(" ", Build(config, _pkg.Path));

        Assert.DoesNotContain("missing", j);                    // 不引用不存在的文件
        Assert.Contains("media=cdrom", j);                      // 空光驱仍然在位（可热插换盘）
        Assert.Contains("ide-cd,drive=cd20", j);                // 设备本体照常接上
    }

    [Fact]
    public void Qmp_UsesWindowsNamedPipe_AndSpiceLocalOnly()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        var cmd = new QemuCommandBuilder(config, Path.Combine(_pkg.Path, "fw")).Build(_pkg.Path, "session99");
        var j = string.Join(" ", cmd.Args);

        Assert.Equal(@"\\.\pipe\grassvm-qmp-session99", cmd.QmpPipeName);
        Assert.Contains("-chardev pipe,id=qmpchar,path=grassvm-qmp-session99", j); // path=（name= 会让 QEMU 解析即退出）
        Assert.Contains("-mon chardev=qmpchar,mode=control", j);
        Assert.Contains("addr=127.0.0.1,port=0,password=", j); // SPICE 只监听本机且启用 ticket
        Assert.Contains("disable-ticketing=off", j);
    }

    [Fact]
    public void ProductionSpice_UsesPerSessionTicket()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Ticketed VM");
        var cmd = new QemuCommandBuilder(config, Path.Combine(_pkg.Path, "fw"))
            .Build(_pkg.Path, "session-ticket", spicePassword: "0123456789abcdef");
        var j = string.Join(" ", cmd.Args);

        Assert.Contains("addr=127.0.0.1,port=0,password=0123456789abcdef,disable-ticketing=off", j);
        Assert.DoesNotContain("disable-ticketing=on", j);
    }

    [Fact]
    public void EveryVm_MustHaveDisplayDevice_HeadlessRejected()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        config.Devices.RemoveAll(d => d is DisplayDevice);
        Assert.Throws<InvalidOperationException>(() => Build(config, _pkg.Path));
    }

    [Fact]
    public void MultipleNetworks_EachIndependentMode()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.HostOnly, VirtualNetworkId = "net1", CreatedOrder = 20 });
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Disconnected, CreatedOrder = 21 });
        var j = string.Join(" ", Build(config, _pkg.Path));

        Assert.Contains("-netdev user,id=net", j);      // 默认 NAT
        Assert.Contains("-netdev tap,id=net20", j);     // Host-only 走 TAP
        Assert.Contains($"ifname={TapNetwork.AdapterName}", j);
        Assert.DoesNotContain("GrassVM-Tap-", j);       // 不引用安装器未创建的动态名称
        // "断开"= 完全不生成网络参数（QEMU 没有 none 后端，-netdev none 会启动失败）
        Assert.DoesNotContain("net21", j);
        Assert.Equal(2, CountOccurrences(j, "-device virtio-net-pci"));
    }

    [Fact]
    public void BridgedNetwork_UsesInstalledTapAdapter_NotPhysicalBridgeSelection()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Bridged VM");
        config.Devices.RemoveAll(d => d is NetworkDevice);
        config.Devices.Add(new NetworkDevice
        {
            Mode = NetworkMode.Bridged,
            BridgeAdapter = "Wi-Fi",
            CreatedOrder = 20,
        });

        var j = string.Join(" ", Build(config, _pkg.Path));

        // BridgeAdapter 是宿主物理网卡选择，不是 TAP 设备名称；QEMU 必须使用
        // 安装器创建并重命名的固定适配器，否则 ifname 不存在会启动失败。
        Assert.Contains($"-netdev tap,id=net20,ifname={TapNetwork.AdapterName}", j);
        Assert.DoesNotContain("ifname=Wi-Fi", j);
        Assert.DoesNotContain("GrassVM-Tap-", j);
    }

    [Fact]
    public void Cdrom_IsReadonly_AndHotpluggable()
    {
        // 构建器对"文件不存在的 ISO"降级为空光驱（缺镜像 ≠ 开不了机）——
        // 这里要断言 file= 形态，先把镜像文件真实造出来
        var isoDir = Path.Combine(_dir, "iso");
        Directory.CreateDirectory(isoDir);
        var iso = Path.Combine(isoDir, "win11.iso");
        File.WriteAllText(iso, "fake-iso");
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "Builder VM");
        config.Devices.Add(new CdromDevice { IsoPath = iso, CreatedOrder = 30 });
        var j = string.Join(" ", Build(config, _pkg.Path));

        Assert.Contains("media=cdrom,file=", j);
        Assert.Contains("win11.iso", j);
        Assert.Contains("readonly=on", j);
        Assert.Contains("ide-cd,drive=cd30", j);
        // CD/DVD 是唯一热插拔设备
        Assert.True(VmConfiguration.IsHotPluggable(config.Devices.OfType<CdromDevice>().First()));
        Assert.False(VmConfiguration.IsHotPluggable(config.Devices.OfType<DiskDevice>().FirstOrDefault() ?? new DiskDevice()));
    }

    [Fact]
    public void RawDevice_ArgumentsAppended_ButControlChannelsBlocked()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        config.Devices.Add(new RawDevice
        {
            Label = "VMware SATA Controller",
            Arguments = new List<string> { "-device", "ich9-ahci,id=sata" },
            CreatedOrder = 40,
        });
        var j = string.Join(" ", Build(config, _pkg.Path));
        Assert.Contains("ich9-ahci,id=sata", j);

        // 劫持控制通道的原始参数必须被拒绝
        var evil = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        evil.Devices.Add(new RawDevice { Arguments = new List<string> { "-qmp", "tcp:0.0.0.0:4444" }, CreatedOrder = 40 });
        Assert.Throws<InvalidOperationException>(() => Build(evil, _pkg.Path));

        // 宿主文件别名（与 -drive 同效或更直接）：全都被拒——blocklist 漏一个
        // 就能把宿主任意文件挂进客户机
        foreach (var alias in new[]
                 {
                     "-hda", "-cdrom", "-pflash", "-kernel", "-initrd", "-virtfs", "-fsdev",
                     "-net", "-object", "-drive", "-blockdev", "-netdev", "-nic", "-L", "-plugin",
                     "-fw_cfg", "-audiodev", "-loadvm", "-global", "-D", "-debugcon",
                     "-readconfig", "-writeconfig", "-option-rom", "-set", "-mem-path",
                     // 关键全局标量（QEMU 末位生效）：改写 Core 的 -m/-smp/-cpu 等
                     "-m", "-smp", "-cpu", "-name", "-boot", "-rtc", "-vga",
                     // 宿主文件 → 客户机固件表（与 -option-rom 同类）
                     "-acpitable", "-smbios",
                     // 宿主文件挂成可读写 USB 盘（与 -hda 同类）
                     "-usbdevice",
                     // 别名/同类形态：-qmp-pretty 同样开控制服务器；-sdl 开
                     // QEMU 自有显示窗口；-trace 以 events=/file= 读写宿主文件；
                     // -no-shutdown 等改写"关机=退出"的状态机根基（锁永不释放）
                     "-qmp-pretty", "-sdl", "-trace", "-no-shutdown", "-no-reboot", "-action", "-watchdog-action",
                     // 双连字形态：QEMU 的解析器同收 --opt（实测 --qmp 会真的开控制
                     // 服务器）——精确匹配会让整个黑名单被 -- 前缀绕过
                     "--qmp", "--drive", "--nographic", "--readconfig",
                 })
        {
            var cfg = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
            cfg.Devices.Add(new RawDevice
            {
                Arguments = new List<string> { alias, "C:/Users/victim/secret.bin" },
                CreatedOrder = 40,
            });
            var ex = Record.Exception(() => Build(cfg, _pkg.Path));
            Assert.True(ex is InvalidOperationException, $"别名未被拦截：{alias}");
        }

        // -device 值里的宿主文件引用（loader,file=）同样被拒；
        // romfile= 也是宿主文件引用（option ROM 读任意文件），大小写不敏感
        // （QemuOpts 键不区分大小写——loader,FILE= 不能绕过）
        var loader = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        loader.Devices.Add(new RawDevice
        {
            Arguments = new List<string> { "-device", "loader,file=C:/Users/victim/firmware.bin" },
            CreatedOrder = 40,
        });
        Assert.Throws<InvalidOperationException>(() => Build(loader, _pkg.Path));
        foreach (var payload in new[]
                 {
                     "virtio-net-pci,romfile=C:/Users/victim/rom.bin",
                     "loader,FILE=C:/Users/victim/firmware.bin",
                     "virtio-net-pci,netdev=n0",   // 后端引用同样拒（netdev 由 Core 全权管理）
                     "vfio-pci,host=00:02.0",       // 宿主 PCI 物理设备直通（与 usb-host 同类）
                     "e1000,bootindex=2",           // 对照：普通属性不受影响
                 })
        {
            var cfg2 = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
            cfg2.Devices.Add(new RawDevice
            {
                Arguments = new List<string> { "-device", payload },
                CreatedOrder = 41,
            });
            var ex2 = Record.Exception(() => Build(cfg2, _pkg.Path));
            if (payload == "e1000,bootindex=2")
                Assert.Null(ex2);
            else
                Assert.True(ex2 is InvalidOperationException, $"宿主文件/后端引用未被拦截：{payload}");
        }
    }

    [Fact]
    public void Mac_IsStable_PerDevice()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        var net = config.Devices.OfType<NetworkDevice>().First();
        var j1 = string.Join(" ", Build(config, _pkg.Path, "s1"));
        var j2 = string.Join(" ", Build(config, _pkg.Path, "s2"));
        Assert.Equal(CountOccurrences(j1, "mac=52:54:00"), CountOccurrences(j2, "mac=52:54:00"));
        Assert.Contains("mac=52:54:00:", j1);
    }

    [Fact]
    public void Mac_RejectsQemuOptionInjection()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Builder VM");
        var net = config.Devices.OfType<NetworkDevice>().First();
        net.MacAddress = "52:54:00:00:00:00,romfile=C:/Users/victim/secret.bin";

        Assert.Throws<InvalidOperationException>(() => Build(config, _pkg.Path));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    [Fact]
    public void GoldenInvariants_QemuParseable()
    {
        // 多盘 + 多网卡（OVF 导入的常见形态）：QEMU 会在 realize 时拒绝重复 bootindex
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Golden");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 8, CreatedOrder = 10 });
        config.Devices.Add(new DiskDevice { Path = "disks/data.qcow2", SizeBytes = 8, CreatedOrder = 11 });
        config.Devices.Add(new CdromDevice { IsoPath = null, CreatedOrder = 12 });
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Nat, CreatedOrder = 13 });
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Nat, CreatedOrder = 14 });
        var args = Build(config, _pkg.Path);

        // 1) bootindex 每设备唯一
        var indices = args.Where(a => a.Contains("bootindex=", StringComparison.Ordinal))
            .Select(a => a.Split("bootindex=")[1].Split(',')[0]).ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());

        // 1b) 类区段连续：所有硬盘 < 所有光驱 < 所有网卡（QEMU 按数值全局排序。
        // 旧算法第二块盘 101 排到光驱 2 / 网卡 3 之后——双盘 VM 先 PXE 再系统盘）
        List<int> AllIdx(params string[] devices) => args
            .Where(a => a.Contains("bootindex=", StringComparison.Ordinal)
                        && devices.Any(d => a.Contains(d, StringComparison.Ordinal)))
            .Select(a => int.Parse(a.Split("bootindex=")[1].Split(',')[0])).ToList();
        var diskIdx = AllIdx("ide-hd", "scsi-hd", "virtio-blk");
        var cdIdx = AllIdx("ide-cd", "scsi-cd");
        var netIdx = AllIdx("e1000", "virtio-net");
        Assert.True(diskIdx.Count >= 2 && netIdx.Count >= 2, "本测试需要多盘多网卡形态");
        Assert.True(diskIdx.Max() < cdIdx.Min(), "硬盘区段必须整体排在光驱前");
        Assert.True(cdIdx.Max() < netIdx.Min(), "光驱必须整体排在网卡前");

        // 2) 网卡 MAC 派生自 DeviceId：两块卡必须不同
        var macs = args.Where(a => a.Contains("mac=", StringComparison.Ordinal))
            .Select(a => a.Split("mac=")[1].Split(',')[0]).ToList();
        Assert.Equal(macs.Count, macs.Distinct().Count());
    }

    [Fact]
    public void BootIndex_RemainsUnique_WhenClassExceedsLegacyHundredSlot()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "Many disks");
        for (var i = 0; i < 101; i++)
            config.Devices.Add(new DiskDevice
            {
                Path = $"disks/data-{i}.qcow2",
                SizeBytes = 8,
                CreatedOrder = 100 + i,
            });
        config.Devices.Add(new CdromDevice { IsoPath = null, CreatedOrder = 300 });

        var args = Build(config, _pkg.Path);
        var indices = args.Where(a => a.Contains("bootindex=", StringComparison.Ordinal))
            .Select(a => int.Parse(a.Split("bootindex=")[1].Split(',')[0]))
            .ToList();
        Assert.Equal(indices.Count, indices.Distinct().Count());

        var diskIndices = args.Where(a => a.Contains("bootindex=", StringComparison.Ordinal)
                                           && a.Contains("virtio-blk", StringComparison.Ordinal))
            .Select(a => int.Parse(a.Split("bootindex=")[1].Split(',')[0]))
            .ToList();
        var cdIndices = args.Where(a => a.Contains("bootindex=", StringComparison.Ordinal)
                                        && a.Contains("ide-cd", StringComparison.Ordinal))
            .Select(a => int.Parse(a.Split("bootindex=")[1].Split(',')[0]))
            .ToList();
        Assert.True(diskIndices.Count > 100 && cdIndices.Count == 1);
        Assert.True(diskIndices.Max() < cdIndices.Min());
    }

    [Fact]
    public void AllNicsDisconnected_EmitsExplicitNicNone()
    {
        // 全部网卡"断开"：必须显式 -nic none——QEMU 默认会静默补一张用户态 NAT 网卡，
        // 用户以为断网了实际照样能上网（安全性不能靠默认值）
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "断网机");
        config.Devices.Add(new DiskDevice { Path = "disks/system.qcow2", SizeBytes = 8, CreatedOrder = 10 });
        // Profile 默认带一块 NAT 网卡——换成一块"断开"的（不是再加一块）
        config.Devices.RemoveAll(d => d is NetworkDevice);
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Disconnected, CreatedOrder = 20 });
        var args = Build(config, _pkg.Path);

        Assert.Contains("-nic", args);
        var i = Array.IndexOf(args, "-nic");
        Assert.Equal("none", args[i + 1]);
    }

    [Fact]
    public void RawNicDeviceWithoutBackend_StillEmitsNicNone()
    {
        // 裸 -device e1000（兼容设备、无后端）≠ 用户要网络：QEMU 的默认网卡只被
        // 后端选项（-netdev/-nic/-net）抑制。若被裸 -device 抑制了 -nic none，
        // QEMU 会静默补一张隐式 SLIRP NAT 网卡——"断网"承诺被绕过
        var config = OsProfileLibrary.CreateDefaultConfig("ubuntu", "RawNic");
        config.Devices.RemoveAll(d => d is NetworkDevice);
        config.Devices.Add(new RawDevice { Arguments = new List<string> { "-device", "e1000" }, CreatedOrder = 20 });
        var args = Build(config, _pkg.Path);

        var i = Array.IndexOf(args, "-nic");
        Assert.True(i >= 0 && args[i + 1] == "none",
            "裸 -device 网卡型号不得抑制 -nic none（会重新放开隐式 NAT 网卡）");
    }
}
