using GrassCore.Config;

namespace GrassCore.Profiles;

/// <summary>
/// OS Profile 随 Grass Block VM 整包发布，不做在线独立更新。
/// Profile 决定固件、磁盘/网卡设备型号（Windows 优先"安装即识别"，现代 Linux 优先 VirtIO，
/// 老旧系统自动切换 BIOS/IDE/e1000 兼容硬件）与推荐资源。用户永远看不到这些底层选择。
/// </summary>
public sealed record OsProfile(
    string Id,
    string DisplayName,
    string Family,            // windows | linux | other
    MachineKind Machine,
    FirmwareKind Firmware,
    bool SecureBoot,
    bool Tpm,
    DiskBus SystemDiskBus,
    NicModel Nic,
    int RecommendedCpuCores,
    int RecommendedMemoryMiB,
    long RecommendedDiskBytes,
    bool Verified,            // "推荐/已验证" vs 可创建
    string? Notes = null)
{
    /// <summary>Windows 11：UEFI + TPM 2.0 + Secure Boot；系统盘 SATA/AHCI（安装阶段无需加载驱动）。</summary>
    public static OsProfile Windows11 { get; } = new(
        "windows-11", "Windows 11", "windows", MachineKind.Q35, FirmwareKind.Uefi,
        SecureBoot: true, Tpm: true, SystemDiskBus: DiskBus.Sata, NicModel.E1000,
        RecommendedCpuCores: 4, RecommendedMemoryMiB: 8192, RecommendedDiskBytes: 80L * 1024 * 1024 * 1024,
        Verified: true);

    public static OsProfile Windows10 { get; } = new(
        "windows-10", "Windows 10", "windows", MachineKind.Q35, FirmwareKind.Uefi,
        SecureBoot: false, Tpm: false, SystemDiskBus: DiskBus.Sata, NicModel.E1000,
        4, 8192, 80L * 1024 * 1024 * 1024, Verified: true);

    public static OsProfile Windows81 { get; } = new(
        "windows-8-1", "Windows 8.1", "windows", MachineKind.Q35, FirmwareKind.Uefi,
        false, false, DiskBus.Sata, NicModel.E1000, 2, 4096, 50L * 1024 * 1024 * 1024, Verified: false);

    public static OsProfile Windows7 { get; } = new(
        "windows-7", "Windows 7", "windows", MachineKind.Pc, FirmwareKind.Bios,
        false, false, DiskBus.Ide, NicModel.E1000, 2, 4096, 50L * 1024 * 1024 * 1024, Verified: false);

    public static OsProfile WindowsXp { get; } = new(
        "windows-xp", "Windows XP", "windows", MachineKind.Pc, FirmwareKind.Bios,
        false, false, DiskBus.Ide, NicModel.E1000, 1, 1024, 20L * 1024 * 1024 * 1024, Verified: false);

    public static OsProfile Ubuntu { get; } = new(
        "ubuntu", "Ubuntu", "linux", MachineKind.Q35, FirmwareKind.Uefi,
        false, false, DiskBus.Virtio, NicModel.Virtio,
        4, 4096, 40L * 1024 * 1024 * 1024, Verified: true);

    public static OsProfile Debian { get; } = new(
        "debian", "Debian", "linux", MachineKind.Q35, FirmwareKind.Uefi,
        false, false, DiskBus.Virtio, NicModel.Virtio,
        2, 4096, 32L * 1024 * 1024 * 1024, Verified: false);

    public static OsProfile Fedora { get; } = new(
        "fedora", "Fedora", "linux", MachineKind.Q35, FirmwareKind.Uefi,
        false, false, DiskBus.Virtio, NicModel.Virtio,
        4, 4096, 40L * 1024 * 1024 * 1024, Verified: false);

    public static OsProfile Arch { get; } = new(
        "arch", "Arch Linux", "linux", MachineKind.Q35, FirmwareKind.Uefi,
        false, false, DiskBus.Virtio, NicModel.Virtio,
        2, 4096, 32L * 1024 * 1024 * 1024, Verified: false);

    /// <summary>冷门系统：保守硬件配置（BIOS + IDE + e1000），可创建但不承诺体验。</summary>
    public static OsProfile Other { get; } = new(
        "other", "其他系统", "other", MachineKind.Pc, FirmwareKind.Bios,
        false, false, DiskBus.Ide, NicModel.E1000,
        1, 2048, 20L * 1024 * 1024 * 1024, Verified: false);
}

public enum MachineKind { Q35, Pc }
public enum DiskBus { Sata, Ide, Virtio }
public enum NicModel { E1000, Virtio }

/// <summary>内置 Profile 库。QEMU 能合理运行的 Guest 原则上可创建；仅 Verified 系统显示"推荐/已验证"。</summary>
public static class OsProfileLibrary
{
    private static readonly List<OsProfile> All = new()
    {
        OsProfile.Windows11, OsProfile.Windows10, OsProfile.Windows81, OsProfile.Windows7, OsProfile.WindowsXp,
        OsProfile.Ubuntu, OsProfile.Debian, OsProfile.Fedora, OsProfile.Arch,
        OsProfile.Other,
    };

    public static IReadOnlyList<OsProfile> List() => All;

    public static OsProfile ById(string id) =>
        All.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"未知 OS Profile：{id}");

    /// <summary>用 Profile 的推荐值生成初始 VmConfiguration（向导 80% 配置由它完成）。</summary>
    public static Config.VmConfiguration CreateDefaultConfig(string profileId, string name)
    {
        var p = ById(profileId);
        var config = new Config.VmConfiguration
        {
            Name = name,
            OsProfileId = p.Id,
            CpuCores = p.RecommendedCpuCores,
            MemoryMiB = p.RecommendedMemoryMiB,
            Firmware = new Config.FirmwareSettings
            {
                Kind = p.Firmware, SecureBoot = p.SecureBoot, Tpm = p.Tpm,
            },
        };
        int order = 0;
        config.Devices.Add(new Config.DisplayDevice { CreatedOrder = ++order });
        config.Devices.Add(new Config.NetworkDevice { Mode = Config.NetworkMode.Nat, CreatedOrder = ++order });
        config.Devices.Add(new Config.AudioDevice { Enabled = true, CreatedOrder = ++order });
        config.Devices.Add(new Config.UsbControllerDevice { Enabled = true, CreatedOrder = ++order });
        if (p.Tpm) config.Devices.Add(new Config.TpmDevice { Enabled = true, CreatedOrder = ++order });
        return config;
    }
}
