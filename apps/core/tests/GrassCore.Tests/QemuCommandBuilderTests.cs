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
        => new QemuCommandBuilder(config, Path.Combine(packagePath, "fw")).Build(packagePath, sessionId).Args.ToArray();

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
        Assert.Contains("ide-hd,drive=disk10,bus=ahci0.0", j);   // Windows 系统盘默认 SATA/AHCI
        Assert.DoesNotContain("virtio-blk", j);
        Assert.Contains("-device e1000,netdev=net", j);          // Windows 原生可识别网卡
        Assert.Contains("OVMF_CODE.secboot.fd", j);              // Secure Boot 固件
        // TPM 参数依赖 GrassCore 先拉起模拟器宿主（Windows 实机阶段交付）；
        // 宿主未就绪时跳过（配置里的意图保留），否则 QEMU 初始化即失败
        Assert.DoesNotContain("tpm-tis", j);
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

        // 默认启动顺序 硬盘 → CD/DVD → 网络启动（bootindex 1/2/3；空槽位无影响）
        Assert.Contains("ide-hd,drive=disk10,bus=ahci0.0,bootindex=1", j);
        Assert.Contains("ide-cd,drive=cd11,bus=ahci0.1,bootindex=2", j);
        Assert.Contains(",bootindex=3", j); // 网络
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
        Assert.Contains("addr=127.0.0.1,port=0,disable-ticketing=on", j); // SPICE 只监听本机
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
        // "断开"= 完全不生成网络参数（QEMU 没有 none 后端，-netdev none 会启动失败）
        Assert.DoesNotContain("net21", j);
        Assert.Equal(2, CountOccurrences(j, "-device virtio-net-pci"));
    }

    [Fact]
    public void Cdrom_IsReadonly_AndHotpluggable()
    {
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "Builder VM");
        config.Devices.Add(new CdromDevice { IsoPath = "C:\\iso\\win11.iso", CreatedOrder = 30 });
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

        // 2) 网卡 MAC 派生自 DeviceId：两块卡必须不同
        var macs = args.Where(a => a.Contains("mac=", StringComparison.Ordinal))
            .Select(a => a.Split("mac=")[1].Split(',')[0]).ToList();
        Assert.Equal(macs.Count, macs.Distinct().Count());
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
}
