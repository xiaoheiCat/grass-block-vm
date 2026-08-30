using System.Diagnostics;
using System.Text;
using GrassCore.Clone;
using GrassCore.Config;
using GrassCore.ExportImport;
using GrassCore.GrassVm;
using GrassCore.Profiles;
using GrassCore.Qemu;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

/// <summary>克隆 / 导出 / 导入 / OVF 交换的集成测试（假 qemu-img 驱动磁盘操作）。</summary>
public class CloneExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _fakeQemuImg;
    private readonly string _root; // Library Root

    public CloneExportTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(_root);
        _fakeQemuImg = CreateFakeQemuImg();
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string CreateFakeQemuImg() => FakeQemuImg.Create(Path.Combine(Path.GetTempPath(), "grassvm-fakes-" + Guid.NewGuid().ToString("N")));

    private TransactionalDiskOps Ops() => new(_fakeQemuImg);

    private async Task<GrassVmPackage> CreateVmWithDiskAsync(string name)
    {
        var pkg = GrassVmPackage.CreateNew(_root, name);
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", name);
        var diskPath = Path.Combine(pkg.DisksPath, "system.qcow2");
        await Ops().CreateSparseQcow2Async(diskPath, 80L * 1024 * 1024 * 1024);
        config.Devices.Add(new DiskDevice
        {
            Path = PathPolicy.NormalizeReference(pkg, diskPath),
            SizeBytes = 80L * 1024 * 1024 * 1024,
            CreatedOrder = 10,
        });
        new ConfigStore(pkg).Save(config);
        return pkg;
    }

    [Fact]
    public async Task FullClone_CreatesIndependentCopy_NoSnapshotHistory_NoLocalTraces()
    {
        var src = await CreateVmWithDiskAsync("Windows 11");
        // 制造本机痕迹与用户快照
        File.WriteAllText(src.StatePath, "{\"lastStartedAt\":\"2026-01-01T00:00:00Z\"}");
        var config = new ConfigStore(src).Load();
        SnapshotService.Create(src, config, "安装完成");
        config.CpuCores = 6;
        new ConfigStore(src).Save(config); // 克隆应取当前配置

        var cloner = new CloneService(Ops());
        var clone = await cloner.FullCloneAsync(src, "Windows 11 副本");

        var cloneConfig = new ConfigStore(clone).Load();
        Assert.Equal("Windows 11 副本", cloneConfig.Name);
        Assert.Equal(6, cloneConfig.CpuCores); // 完整克隆取当前有效状态
        Assert.Null(cloneConfig.CloneInfo);    // 完整克隆是独立 VM
        Assert.True(File.Exists(Path.Combine(clone.DisksPath, "system.qcow2")));
        Assert.Empty(Directory.EnumerateDirectories(clone.SnapshotsPath)); // 不含快照历史
        Assert.False(File.Exists(clone.StatePath));                        // 不带本机使用痕迹
        // 设备 UUID 重新生成（内部标识不共享）
        var srcIds = new ConfigStore(src).Load().Devices.Select(d => d.DeviceId).ToHashSet();
        var cloneIds = cloneConfig.Devices.Select(d => d.DeviceId).ToHashSet();
        Assert.Empty(srcIds.Intersect(cloneIds));
        Assert.Equal(srcIds.Count, cloneIds.Count);
    }

    [Fact]
    public async Task LinkedClone_MustBeBasedOnSnapshot_RecordsCloneInfo()
    {
        var src = await CreateVmWithDiskAsync("Ubuntu");
        var config = new ConfigStore(src).Load();
        // 带 diskOps 创建：冻结文件真实存在——链接克隆的 overlay backing 指向它，
        // 真实 qemu-img（和修正后的假件）都要求 backing 在场
        var snap = SnapshotService.Create(src, config, "干净系统", diskOps: Ops());

        var cloner = new CloneService(Ops());
        var clone = cloner.LinkedClone(src, snap.Uuid, "Ubuntu 实验田");

        var cloneConfig = new ConfigStore(clone).Load();
        Assert.NotNull(cloneConfig.CloneInfo);
        Assert.Equal(snap.Uuid, cloneConfig.CloneInfo!.ParentSnapshotUuid);
        // 同目录 → 相对引用（../Ubuntu.grassvm），移动整个 Library 后仍有效
        Assert.False(Path.IsPathRooted(cloneConfig.CloneInfo.ParentVmPath));
        Assert.Contains("../", cloneConfig.CloneInfo.ParentVmPath);
        var overlay = PathPolicy.Resolve(clone, cloneConfig.Devices.OfType<DiskDevice>().First().Path);
        Assert.True(File.Exists(overlay)); // overlay 已生成（backing 指向父快照 overlay）

        // 链接克隆构成"删除快照"的依赖：SnapshotService 能找到它
        var deps = SnapshotService.FindLinkedCloneReferences(src);
        var dep = Assert.Single(deps);
        Assert.Equal("Ubuntu 实验田", dep.ChildVmName);
        Assert.Equal(snap.Uuid, dep.ParentSnapshotUuid);
    }

    [Fact]
    public async Task GrassVmZip_RoundTrips_FullArchive_WithoutLocalTraces()
    {
        var src = await CreateVmWithDiskAsync("归档机");
        var config = new ConfigStore(src).Load();
        SnapshotService.Create(src, config, "带历史的快照");
        File.WriteAllText(src.StatePath, "{}"); // 本机痕迹
        Directory.CreateDirectory(src.RuntimePath);
        File.WriteAllText(src.SessionPath, "{\"session\":\"x\"}");
        File.WriteAllText(Path.Combine(src.LogsPath, "qemu.log"), "log");

        GrassVmZip.EnsureExportable(src, isRunning: false);
        var zipPath = Path.Combine(_dir, "archive.zip");
        GrassVmZip.Export(src, zipPath);

        // 导入到另一个 Library Root（完整档案：快照树 + 磁盘 + 配置都在）
        var otherRoot = Path.Combine(_dir, "imported-root");
        Directory.CreateDirectory(otherRoot);
        var imported = GrassVmZip.Import(zipPath, otherRoot);
        Assert.Equal("归档机", imported.Name);
        // 快照树完整保留（完整档案）
        var tree = SnapshotService.LoadTree(imported);
        Assert.Single(tree.All);
        // 本机痕迹与瞬态不进入档案
        Assert.False(File.Exists(imported.StatePath));
        Assert.False(Directory.EnumerateFiles(imported.RuntimePath).Any());
        Assert.Empty(Directory.EnumerateFiles(imported.LogsPath));
        // 磁盘随包
        Assert.True(File.Exists(Path.Combine(imported.DisksPath, "system.qcow2")));
    }

    [Fact]
    public async Task GrassVmZip_Export_RequiresShutdown()
    {
        var src = await CreateVmWithDiskAsync("运行中");
        // 运行中 → vm.lock 存在
        new VmLock(src).Acquire();
        Assert.Throws<GrassCoreException>(() => GrassVmZip.EnsureExportable(src, isRunning: false));
        Assert.Throws<GrassCoreException>(() => GrassVmZip.EnsureExportable(src, isRunning: true));
        new VmLock(src).Release();
        GrassVmZip.EnsureExportable(src, isRunning: false); // 关机后允许
    }

    [Fact]
    public void GrassVmZip_Import_RejectsZipSlip()
    {
        var zipPath = Path.Combine(_dir, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("Evil.grassvm/../../escaped.txt");
        }
        var ex = Assert.ThrowsAny<Exception>(() => GrassVmZip.Import(zipPath, _root));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void GrassVmZip_Import_FailsOnExistingName_NoMerge()
    {
        var zipPath = Path.Combine(_dir, "dup.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("Dup.grassvm/config.json");
        }
        Directory.CreateDirectory(Path.Combine(_root, "Dup.grassvm"));
        Assert.Throws<GrassCoreException>(() => GrassVmZip.Import(zipPath, _root));
    }

    [Fact]
    public async Task Ovf_RoundTrip_CDAndNetworkMode_Survive()
    {
        // 往返保真：CD/DVD 设备（含介质）与"断开"网卡模式都不能丢——
        // ResourceType 写错（16 而非 15）会让导入端把光驱当硬盘解析失败后丢弃；
        // 网卡不带模式扩展的话"断开"往返一次就变回 NAT
        var src = await CreateVmWithDiskAsync("光驱机");
        var config = new ConfigStore(src).Load();
        var iso = Path.Combine(_dir, "install.iso");
        File.WriteAllBytes(iso, "ISO-DATA"u8.ToArray());
        config.Devices.Add(new CdromDevice
        {
            IsoPath = iso, // 包外绝对引用（导出只拷介质、不搬原文件）
            CreatedOrder = 30,
        });
        config.Devices.Add(new CdromDevice { IsoPath = null, CreatedOrder = 31 }); // 空光驱
        config.Devices.RemoveAll(d => d is NetworkDevice);
        config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Disconnected, CreatedOrder = 40 });
        // 用户明确关掉的声卡/USB：往返不能被 Profile 默认值悄悄打开
        // （"忠实保留原虚拟硬件"包含关闭状态）
        foreach (var audio in config.Devices.OfType<GrassCore.Config.AudioDevice>())
            audio.Enabled = false;
        foreach (var usb in config.Devices.OfType<GrassCore.Config.UsbControllerDevice>())
            usb.Enabled = false;
        new ConfigStore(src).Save(config);

        var dest = Path.Combine(_dir, "ovf-cd-out");
        var ovfPath = await new OvfExporter(Ops()).ExportAsync(src, dest);

        var plan = new OvfImporter(Ops()).PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath), dest);
        Assert.False(plan.BlocksImport);
        var cds = plan.Config.Devices.OfType<CdromDevice>().ToList();
        Assert.Equal(2, cds.Count);
        // 介质光驱的引用指向档案内拷贝的 ISO（导入执行时会再拷进包内 isovol/）
        var withMedium = cds.Single(c => c.IsoPath is not null);
        Assert.EndsWith(".iso", withMedium.IsoPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(withMedium.IsoPath!), "光驱介质必须随档案走（否则往返丢介质）");
        Assert.NotNull(cds.Single(c => c.IsoPath is null));
        // 断开模式如实还原，不被默认 NAT 顶替
        var nic = plan.Config.Devices.OfType<NetworkDevice>().Single();
        Assert.Equal(NetworkMode.Disconnected, nic.Mode);
        // 系统 Profile / 固件随档案走：不还原的话 UEFI 安装的客户机往返后按
        // BIOS/pc 起不来（导入端默认 "other"）
        var w11 = OsProfileLibrary.ById("windows-11");
        Assert.Equal("windows-11", plan.Config.OsProfileId);
        Assert.Equal(w11.Firmware, plan.Config.Firmware.Kind);
        Assert.Equal(w11.SecureBoot, plan.Config.Firmware.SecureBoot);
        if (w11.Tpm)
            Assert.NotEmpty(plan.Config.Devices.OfType<GrassCore.Config.TpmDevice>());
        // 关闭状态如实保留：要么设备缺席（等效于关）、要么 Enabled=false——
        // 绝不能回来一台 Enabled=true 的声卡/USB
        Assert.DoesNotContain(plan.Config.Devices.OfType<GrassCore.Config.AudioDevice>(), d => d.Enabled);
        Assert.DoesNotContain(plan.Config.Devices.OfType<GrassCore.Config.UsbControllerDevice>(), d => d.Enabled);
    }

    [Fact]
    public async Task Ovf_RoundTrip_CurrentStateOnly()
    {
        var src = await CreateVmWithDiskAsync("交换机");
        var dest = Path.Combine(_dir, "ovf-out");
        var exporter = new OvfExporter(Ops());
        var ovfPath = await exporter.ExportAsync(src, dest);

        Assert.True(File.Exists(ovfPath));
        var vmdk = Directory.EnumerateFiles(dest, "*.vmdk").Single();
        using (var reader = new StreamReader(vmdk, Encoding.UTF8))
        {
            Assert.StartsWith("# Disk Des", reader.ReadLine() ?? "");
        }

        // 再导入：能映射回原生设备（硬盘、CPU、内存、网卡、显示器）
        var importer = new OvfImporter(Ops());
        var plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath), dest);
        Assert.False(plan.BlocksImport);
        Assert.Single(plan.Disks);
        Assert.Equal(4, plan.Config.CpuCores);
        Assert.Equal(8192, plan.Config.MemoryMiB);
        Assert.True(plan.Config.HasDisplayDevice);
        Assert.NotEmpty(plan.Config.Devices.OfType<NetworkDevice>());
        Assert.True(plan.Config.HasBootableSource);
    }

    [Fact]
    public async Task Ovf_Import_BlocksUnsupported_RestorePathWithUserOverride()
    {
        // 构造一个含"完全无法支持设备"的 OVF（ResourceType=999 + 无可转换参数）
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
              <References>
                <File ovf:id="file1" ovf:href="disk.vmdk" ovf:size="1024"/>
              </References>
              <DiskSection>
                <Disk ovf:diskId="disk1" ovf:fileRef="file1" ovf:capacity="80" ovf:capacityAllocationUnits="Gigabytes"/>
              </DiskSection>
              <VirtualSystem ovf:id="vs">
                <Name>Hard Case</Name>
                <Item>
                  <rasd:Caption>GPU Offload Acceleration</rasd:Caption>
                  <rasd:ResourceType>999</rasd:ResourceType> <!-- 未知/无法支持的资源 -->
                </Item>
                <Item>
                  <rasd:Caption>Hard Disk 1</rasd:Caption>
                  <rasd:ResourceType>17</rasd:ResourceType>
                  <rasd:HostResource>ovf:/disk/disk1</rasd:HostResource>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var ovfDir = Path.Combine(_dir, "ovf-bad");
        Directory.CreateDirectory(ovfDir);
        File.WriteAllText(Path.Combine(ovfDir, "case.ovf"), ovf);
        File.WriteAllText(Path.Combine(ovfDir, "disk.vmdk"), "# Disk DescriptorFile\nfake");

        var importer = new OvfImporter(Ops());
        var plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Parse(ovf), ovfDir);

        Assert.True(plan.BlocksImport);              // 默认阻止导入并提示风险
        Assert.Contains(plan.UnsupportedDevices, d => d.Contains("GPU Offload"));
        Assert.Equal(80L * 1024 * 1024 * 1024, plan.Disks.Single().VirtualSizeBytes); // capacity × units

        // 不允许 → 执行抛异常
        await Assert.ThrowsAsync<GrassCoreException>(() =>
            importer.ExecuteAsync(plan, _root, "Hard Case", allowUnsupported: false));
        Assert.False(Directory.Exists(Path.Combine(_root, "Hard Case.grassvm"))); // 无半成品

        // 用户选择"仍然导入" → 允许，但标记不可用
        var pkg = await importer.ExecuteAsync(plan, _root, "Hard Case", allowUnsupported: true);
        var raw = new ConfigStore(pkg).Load().Devices.OfType<RawDevice>().Single(r => r.Unsupported);
        Assert.Contains("GPU Offload", raw.Label);
    }

    [Fact]
    public async Task Ovf_Import_UEFICreatesNvram_PreflightPasses()
    {
        // UEFI 信封导入必须落 NVRAM（VARS.fd）：不落的话启动预检直接判
        // "固件数据缺失"拒绝启动，且设置页没有重建入口 = 永久无法启动的 VM
        var src = await CreateVmWithDiskAsync("导入机"); // windows-11 → UEFI
        var dest = Path.Combine(_dir, "ovf-uefi-out");
        var ovfPath = await new OvfExporter(Ops()).ExportAsync(src, dest);
        var fwDir = Path.Combine(_dir, "fw");
        Directory.CreateDirectory(fwDir);
        File.WriteAllText(Path.Combine(fwDir, "OVMF_VARS.fd"), "template-vars");

        var importer = new OvfImporter(Ops(), Path.Combine(fwDir, "OVMF_VARS.fd"));
        var plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath), dest);
        var pkg = await importer.ExecuteAsync(plan, _root, "导入机-副本", allowUnsupported: false);

        Assert.True(File.Exists(Path.Combine(pkg.FirmwarePath, "VARS.fd")),
            "UEFI 导入必须从模板初始化 NVRAM");
        // 启动预检不能有致命问题（NVRAM 在场、磁盘都转换到位）
        var cfg = new ConfigStore(pkg).Load();
        var view = new GrassCore.Qemu.VmConfigView(
            HasDisplayDevice: true,
            NeedsNvram: cfg.Firmware?.Kind == GrassCore.Config.FirmwareKind.Uefi,
            Disks: cfg.Devices.OfType<DiskDevice>()
                .Select(d => new GrassCore.Qemu.VmConfigView.DiskView(d.Path, "disk")).ToList(),
            Cds: new List<GrassCore.Qemu.VmConfigView.CdView>());
        var fatal = GrassCore.Qemu.StartupPreflight.Check(pkg, view).Where(p => p.Fatal).ToList();
        Assert.Empty(fatal);
    }

    [Fact]
    public void Ovf_Import_ForeignEnvelope_FirmwareMatchesOtherProfile()
    {
        // 第三方信封（无 gvm:OsProfile）→ profile=other（BIOS/pc）。配置默认值
        // 是 Uefi——不对齐的话预检要 NVRAM、构建器却按 BIOS 开机：假 UEFI
        // 永远起不来（复用"完全无法支持"测试的信封形态，走导入执行）
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>Foreign</Name>
                <Item>
                  <rasd:ResourceType>4</rasd:ResourceType>
                  <rasd:VirtualQuantity>2048</rasd:VirtualQuantity>
                  <rasd:AllocationUnits>MegaBytes</rasd:AllocationUnits>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var plan = new OvfImporter(Ops()).PlanFromOvf(System.Xml.Linq.XDocument.Parse(ovf), _dir);
        Assert.Equal(GrassCore.Config.FirmwareKind.Bios, plan.Config.Firmware?.Kind);
    }

    [Fact]
    public void Ovf_Import_MemoryExponentUnits_NotSilentlyShrunk()
    {
        // DMTF 指数单位 "byte * 2^30"：旧代码把它归一化成 "g"，而 switch 只有
        // "gb" → 落进"未知单位按 MB"，2GiB 静默变 2MiB 再被钳制抬到 512MiB
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>Exponent</Name>
                <Item>
                  <rasd:ResourceType>4</rasd:ResourceType>
                  <rasd:VirtualQuantity>2</rasd:VirtualQuantity>
                  <rasd:AllocationUnits>byte * 2^30</rasd:AllocationUnits>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var plan = new OvfImporter(Ops()).PlanFromOvf(System.Xml.Linq.XDocument.Parse(ovf), _dir);
        // 2 × 2^30 B = 2 GiB = 2048 MiB（钳制上限 = 宿主内存一半，测试机至少 ≥ 1GB）
        Assert.Equal(2048, plan.Config.MemoryMiB);
    }

    [Fact]
    public void Ovf_Import_UnknownProfileId_FirmwareSyncedToDefault_NotFakeUefi()
    {
        // 新版本导出的 gvm:OsProfile（此版本不认识的 id）：警告照给，但固件
        // 必须跟有效 profile（other → BIOS）对齐——配置默认值 Uefi 不清理的话，
        // 预检要 NVRAM、构建器却按 BIOS 开机 = 一台永远起不来的"假 UEFI"
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:gvm="http://grass-block.vm/ovf-ext/1">
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>From The Future</Name>
                <gvm:OsProfile id="future-os-2.0"/>
                <Item>
                  <rasd:ResourceType>4</rasd:ResourceType>
                  <rasd:VirtualQuantity>2048</rasd:VirtualQuantity>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var plan = new OvfImporter(Ops()).PlanFromOvf(System.Xml.Linq.XDocument.Parse(ovf), _dir);
        Assert.Contains(plan.Warnings, w => w.Contains("不认识"));
        Assert.Equal("other", plan.Config.OsProfileId);
        Assert.Equal(GrassCore.Config.FirmwareKind.Bios, plan.Config.Firmware?.Kind);
    }

    [Fact]
    public void Ovf_SpaceEstimate_UsesDiskSizes()
    {
        var ovfDir = Path.Combine(_dir, "ovf-est");
        Directory.CreateDirectory(ovfDir);
        var diskFile = Path.Combine(ovfDir, "big.vmdk");
        File.WriteAllBytes(diskFile, new byte[10 * 1024 * 1024]); // 10MB 物理
        var disks = new[] { new OvfImporter.DiskImport(diskFile, 80L * 1024 * 1024 * 1024, "disks/big.qcow2") };
        var estimate = OvfImporter.EstimateRequiredBytes(disks);
        // 文档契约 = Σ max(物理, 虚拟)：虚拟 80GB > 物理 10MB → 按 80GB 计。
        // 别把物理量再加一遍（源文件不在存档卷上，加一遍 = 预估翻倍，执行前
        // 的硬校验会拒绝明明放得下的导入）
        Assert.Equal(80L * 1024 * 1024 * 1024, estimate);
    }

    [Fact]
    public async Task Ovf_RoundTrip_RawDeviceQemuArgs_Survive()
    {
        // 原生 RawDevice（OVF 导入来的兼容设备）再导出 → 再导入：参数必须一个不少。
        // 扩展元素（gvm:QemuArgs）的命名空间/序列化一旦回归，往返就静默丢设备
        var src = await CreateVmWithDiskAsync("带兼容设备");
        var config = new ConfigStore(src).Load();
        config.Devices.Add(new RawDevice
        {
            Label = "SATA 控制器",
            Arguments = new List<string> { "-device", "ich9-ahci,id=sata" },
            CreatedOrder = 30,
        });
        new ConfigStore(src).Save(config);

        var dest = Path.Combine(_dir, "ovf-raw-out");
        var ovfPath = await new OvfExporter(Ops()).ExportAsync(src, dest);

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Load(ovfPath), dest);
        Assert.False(plan.BlocksImport);
        var raw = Assert.Single(plan.Config.Devices.OfType<RawDevice>());
        Assert.Equal("SATA 控制器", raw.Label);
        Assert.Equal(new[] { "-device", "ich9-ahci,id=sata" }, raw.Arguments);
        Assert.False(raw.Unsupported); // 带 Grass 出处标记：可信，参数保留
    }

    [Fact]
    public void Ovf_Import_ForeignQemuArgs_NotTrusted()
    {
        // 伪造的 gvm:QemuArgs（无 Grass 出处标记）：不可信——列入"完全无法支持"
        // 需用户确认，且导入后标记 Unsupported（绝不进 QEMU 命令行）。
        // 否则任意第三方 OVF 就能在 startVm 时以用户身份注入 QEMU 参数
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:gvm="http://grass-block.vm/ovf-ext/1">
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>Hostile</Name>
                <Item>
                  <rasd:Caption>Friendly Helper</rasd:Caption>
                  <rasd:ResourceType>1</rasd:ResourceType>
                  <gvm:QemuArgs>-drive
            file=C:/Users/x/secret.txt,format=raw
            -device
            ide-hd,drive=hostfile</gvm:QemuArgs>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var ovfDir = Path.Combine(_dir, "ovf-hostile");
        Directory.CreateDirectory(ovfDir);
        File.WriteAllText(Path.Combine(ovfDir, "case.ovf"), ovf);

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Parse(ovf), ovfDir);

        Assert.True(plan.BlocksImport); // 必须走用户确认
        var raw = Assert.Single(plan.Config.Devices.OfType<RawDevice>());
        Assert.True(raw.Unsupported, "不可信参数的设备不能进入 QEMU 命令行");
    }

    [Fact]
    public void Preflight_MissingCdIso_IsWarningNotDeadEnd()
    {
        // 安装镜像是用户随时会清理的外部文件（Downloads/临时目录）：缺失只能
        // 警告（空光驱启动，运行后可换介质）。设为 Fatal 会把停机状态下的 VM
        // 变成永远开不了机的死路——设置页没有换介质入口
        var view = new GrassCore.Qemu.VmConfigView(
            HasDisplayDevice: true,
            NeedsNvram: false,
            Disks: new List<GrassCore.Qemu.VmConfigView.DiskView>(),
            Cds: new List<GrassCore.Qemu.VmConfigView.CdView>
            {
                new(System.IO.Path.Combine(_dir, "definitely-missing.iso"), "CD/DVD"),
            });
        var pkg = GrassVmPackage.CreateNew(_dir, "PreflightCd");
        var problems = GrassCore.Qemu.StartupPreflight.Check(pkg, view);
        var cd = Assert.Single(problems);
        Assert.False(cd.Fatal, "缺失安装镜像必须可以启动（空光驱），不能是死路");
    }

    [Fact]
    public void Ovf_Import_VirtualSystemCollection_OnlyFirstVmImported()
    {
        // DMTF 规范的多 VM 形态：VirtualSystem 藏在 VirtualSystemCollection
        // 之下（不是 Envelope 的直接子元素）。只认直接子元素会把 vs 解析成
        // null，Item 循环退回整棵树——所有 VM 的盘/设备拼进一台机器，而
        // "仅导入第一个"的警告成了谎言。必须只认第一个 VirtualSystem
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
              <References/>
              <VirtualSystemCollection id="collection">
                <VirtualSystem id="vm1">
                  <Name>First VM</Name>
                  <Item>
                    <rasd:ResourceType>3</rasd:ResourceType>
                    <rasd:VirtualQuantity>1</rasd:VirtualQuantity>
                  </Item>
                </VirtualSystem>
                <VirtualSystem id="vm2">
                  <Name>Second VM</Name>
                  <Item>
                    <rasd:ResourceType>3</rasd:ResourceType>
                    <rasd:VirtualQuantity>8</rasd:VirtualQuantity>
                  </Item>
                </VirtualSystem>
              </VirtualSystemCollection>
            </Envelope>
            """;

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Parse(ovf), _dir);

        Assert.Equal("First VM", plan.Config.Name);   // 名字来自第一个系统（嵌套形态也能读到）
        Assert.Equal(1, plan.Config.CpuCores);        // 只认第一个系统：第二个的 Item 不再覆盖
        Assert.Contains(plan.Warnings, w => w.Contains("2 个虚拟系统"));
    }

    [Fact]
    public void Ovf_Import_UsbHostPassthrough_Gated()
    {
        // usb-host 不带任何 file=/path= 属性——属性黑名单拦不住，但它是把
        // 宿主 USB 物理设备（U 盘/安全密钥）静默透传进客户机的型号。产品模型
        // 里宿主 USB 默认属于 Host（显示器设备栏主动连接才透传），必须按
        // "不可信"拦入占位，绝不随导入静默生效
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:gvm="http://grass-block.vm/ovf-ext/1">
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>UsbHost</Name>
                <Item>
                  <rasd:Caption>Friendly USB</rasd:Caption>
                  <rasd:ResourceType>1</rasd:ResourceType>
                  <gvm:QemuArgs>-device
            usb-host,vendorid=0x46f4,productid=0x0001</gvm:QemuArgs>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var ovfDir = Path.Combine(_dir, "ovf-usbhost");
        Directory.CreateDirectory(ovfDir);
        File.WriteAllText(Path.Combine(ovfDir, "case.ovf"), ovf);

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Parse(ovf), ovfDir);

        var raw = Assert.Single(plan.Config.Devices.OfType<RawDevice>());
        Assert.True(raw.Unsupported, "usb-host 透传不能随导入静默生效");
        Assert.Contains(plan.UnsupportedDevices, d => d.Contains("Friendly USB"));
    }

    [Fact]
    public async Task Ovf_RoundTrip_UnsupportedFlag_Survives()
    {
        // 占位（不可用）的兼容设备往返后必须还是占位：否则用户当初被明确告知
        // "不会生效"的参数，一导出再导入就静默复活进 QEMU 命令行
        var src = await CreateVmWithDiskAsync("带占位设备");
        var config = new ConfigStore(src).Load();
        config.Devices.Add(new RawDevice
        {
            Label = "神秘控制器",
            Arguments = new List<string> { "-device", "mystery-dev,opts=1" },
            Unsupported = true,
            CreatedOrder = 31,
        });
        new ConfigStore(src).Save(config);

        var dest = Path.Combine(_dir, "ovf-unsupported-out");
        var ovfPath = await new OvfExporter(Ops()).ExportAsync(src, dest);

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Load(ovfPath), dest);
        var raw = Assert.Single(plan.Config.Devices.OfType<RawDevice>());
        Assert.True(raw.Unsupported, "导出侧带出的占位标记在导入侧必须复原");
    }

    [Fact]
    public void Ovf_Import_ForgedMarkerQemuArgs_StillGated()
    {
        // 伪造出处标记（<gvm:Export tool="Grass Block VM"/>）救不了白名单外的参数：
        // 信任判定只看内容，不看被审文件自己声称的身份
        var ovf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:gvm="http://grass-block.vm/ovf-ext/1">
              <gvm:Export tool="Grass Block VM" version="1.0"/>
              <References/>
              <VirtualSystem ovf:id="vs">
                <Name>Forged</Name>
                <Item>
                  <rasd:Caption>Helper</rasd:Caption>
                  <rasd:ResourceType>1</rasd:ResourceType>
                  <gvm:QemuArgs>-readconfig
            C:/Users/x/evil.ini</gvm:QemuArgs>
                </Item>
              </VirtualSystem>
            </Envelope>
            """;
        var ovfDir = Path.Combine(_dir, "ovf-forged");
        Directory.CreateDirectory(ovfDir);
        File.WriteAllText(Path.Combine(ovfDir, "case.ovf"), ovf);

        var plan = new OvfImporter(Ops()).PlanFromOvf(
            System.Xml.Linq.XDocument.Parse(ovf), ovfDir);

        Assert.True(plan.BlocksImport);
        Assert.All(plan.Config.Devices.OfType<RawDevice>(), r => Assert.True(r.Unsupported));
    }
}
