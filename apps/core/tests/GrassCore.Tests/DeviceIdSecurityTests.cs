using System.IO.Compression;
using GrassCore.Config;
using GrassCore.ExportImport;
using GrassCore.GrassVm;
using GrassCore.Library;
using GrassCore.Profiles;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

public sealed class DeviceIdSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-device-id-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly HostDb _db;

    public DeviceIdSecurityTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(_root);
        _db = new HostDb(Path.Combine(_dir, "grass.db"));
        _db.SetPreference("libraryRoot", _root);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("disks/disk.qcow2")]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public void DeviceIdPolicy_RejectsPathLikeAndArbitraryValues(string value)
        => Assert.False(DeviceIdPolicy.IsValid(value));

    [Fact]
    public void DeviceIdPolicy_AcceptsGeneratedUuid()
        => Assert.True(DeviceIdPolicy.IsValid(Guid.NewGuid().ToString()));

    [Fact]
    public void SnapshotCreate_RejectsInvalidDeviceIdBeforeFilesystemMutation()
    {
        var package = GrassVmPackage.CreateNew(_root, "Snapshot Security");
        var config = OsProfileLibrary.CreateDefaultConfig("other", package.Name);
        config.Devices[0].DeviceId = "..\\..\\evil";

        Assert.Throws<GrassCoreException>(() => SnapshotService.Create(package, config, "must reject"));
        Assert.Empty(Directory.EnumerateDirectories(package.SnapshotsPath));
    }

    [Fact]
    public void SnapshotLoad_RejectsInvalidDeviceIdMetadata()
    {
        var package = GrassVmPackage.CreateNew(_root, "Snapshot Metadata Security");
        var snapshotId = Guid.NewGuid().ToString();
        var snapshotDir = Path.Combine(package.SnapshotsPath, snapshotId);
        Directory.CreateDirectory(snapshotDir);
        File.WriteAllText(Path.Combine(snapshotDir, "metadata.json"), $$"""
            {
              "uuid": "{{snapshotId}}",
              "createdAt": "2026-01-01T00:00:00Z",
              "fullConfigSnapshot": "{}",
              "diskOverlayRefs": { "..\\\\..\\\\evil": "disks/disk-evil.qcow2" }
            }
            """);

        Assert.Empty(SnapshotService.LoadTree(package).All);
    }

    [Fact]
    public void UpdateConfig_RejectsInvalidDeviceId()
    {
        var package = CreatePackageWithInvalidDeviceId("Update Security");
        var service = CreateService();
        var configJson = ConfigJson.Serialize(new ConfigStore(package).Load());

        var ex = Assert.Throws<GrassCoreException>(() => service.UpdateConfig(package.Path, configJson));
        Assert.Contains("UUID", ex.Message);
    }

    [Fact]
    public void StartVm_RejectsInvalidDeviceIdBeforeLaunchingQemu()
    {
        var package = CreatePackageWithInvalidDeviceId("Start Security");
        var service = CreateService();

        var ex = Assert.Throws<GrassCoreException>(() => service.StartVm(package.Path));
        Assert.Contains("UUID", ex.Message);
        Assert.False(File.Exists(package.LockPath));
    }

    [Fact]
    public void ZipImport_RejectsInvalidDeviceId()
    {
        var zipPath = Path.Combine(_dir, "invalid-device.zip");
        var config = OsProfileLibrary.CreateDefaultConfig("other", "Invalid Device");
        config.Devices[0].DeviceId = "..\\..\\evil";
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Invalid Device.grassvm/config.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(ConfigJson.Serialize(config));
        }

        var ex = Assert.Throws<GrassCoreException>(() => GrassVmZip.Import(zipPath, _root));
        Assert.Contains("UUID", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "Invalid Device.grassvm")));
    }

    private GrassVmPackage CreatePackageWithInvalidDeviceId(string name)
    {
        var package = GrassVmPackage.CreateNew(_root, name);
        var config = OsProfileLibrary.CreateDefaultConfig("other", name);
        config.Devices[0].DeviceId = "..\\..\\evil";
        new ConfigStore(package).Save(config);
        return package;
    }

    private GrassCoreService CreateService() =>
        new(_db, Path.Combine(_dir, "missing-qemu.exe"), Path.Combine(_dir, "missing-qemu-img.exe"),
            Path.Combine(_dir, "firmware"), "11");
}
