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

    private static string CreateFakeQemuImg()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此测试当前仅在 POSIX CI 上运行假 qemu-img。");
        var sh = Path.Combine(Path.GetTempPath(), "fake-qemu-img-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(sh, """
            #!/bin/sh
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            case "$target" in
              *vmdk*) printf '# Disk DescriptorFile\nfake-vmdk' > "$target" ;;
              *)      printf 'QFI\373' > "$target" ;;
            esac
            printf '%s\n' "$*" >> "$target"
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }

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
        var snap = SnapshotService.Create(src, config, "干净系统");

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
    public void Ovf_Import_BlocksUnsupported_RestorePathWithUserOverride()
    {
        // 构造一个含"完全无法支持设备"的 OVF（ResourceType=1 + 无可转换参数）
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
                  <rasd:Caption>Some GPU Offload</rasd:Caption>
                  <rasd:ResourceType>1</rasd:ResourceType>
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
        Assert.Throws<GrassCoreException>(() =>
            importer.ExecuteAsync(plan, _root, "Hard Case", allowUnsupported: false).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(Path.Combine(_root, "Hard Case.grassvm"))); // 无半成品

        // 用户选择"仍然导入" → 允许，但标记不可用
        var pkg = importer.ExecuteAsync(plan, _root, "Hard Case", allowUnsupported: true).GetAwaiter().GetResult();
        var raw = new ConfigStore(pkg).Load().Devices.OfType<RawDevice>().Single(r => r.Unsupported);
        Assert.Contains("GPU Offload", raw.Label);
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
        // 物理量 + 虚拟量都要计入（只算物理量 = 空间不足时导入中途失败）
        Assert.True(estimate >= 10 * 1024 * 1024 + 80L * 1024 * 1024 * 1024);
    }
}
