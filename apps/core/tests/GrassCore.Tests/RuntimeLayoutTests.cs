using Xunit;

namespace GrassCore.Tests;

public sealed class RuntimeLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "grassvm-runtime-layout-" + Guid.NewGuid().ToString("N"));
    private readonly string _installDir;

    public RuntimeLayoutTests()
    {
        _installDir = Path.Combine(_root, "resources", "GrassCore");
        Directory.CreateDirectory(_installDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ReleaseLayout_IgnoresEnvironmentOverrides()
    {
        var resources = Path.Combine(_root, "resources");
        var qemu = Path.Combine(resources, "QEMU");
        var firmware = Path.Combine(resources, "firmware");
        var helper = Path.Combine(resources, "GrassSpiceHelper");
        Directory.CreateDirectory(qemu);
        Directory.CreateDirectory(firmware);
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(qemu, "qemu-system-x86_64.exe"), "bundled");
        File.WriteAllText(Path.Combine(qemu, "qemu-img.exe"), "bundled");
        File.WriteAllText(Path.Combine(qemu, "qemu-major.txt"), "9");

        var malicious = Path.Combine(_root, "attacker-controlled");
        var paths = RuntimeLayout.Resolve(_installDir, allowDevOverrides: false, name => name switch
        {
            "GRASSCORE_QEMU_DIR" => malicious,
            "GRASSCORE_OVMF_DIR" => malicious,
            "GRASSCORE_QEMU_MAJOR" => "99",
            _ => null,
        });

        Assert.Equal(qemu, paths.QemuRoot);
        Assert.Equal(Path.Combine(qemu, "qemu-system-x86_64.exe"), paths.QemuSystem);
        Assert.Equal(Path.Combine(qemu, "qemu-img.exe"), paths.QemuImg);
        Assert.Equal(firmware, paths.FirmwareDir);
        Assert.Equal(helper, paths.HelperRoot);
        Assert.Equal("9", paths.QemuMajor);
        Assert.DoesNotContain("attacker-controlled", paths.QemuRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevelopmentLayout_AllowsExplicitEnvironmentOverrides()
    {
        var qemu = Path.Combine(_root, "fake-qemu");
        Directory.CreateDirectory(qemu);
        File.WriteAllText(Path.Combine(qemu, "qemu-system-x86_64.exe"), "fake");
        File.WriteAllText(Path.Combine(qemu, "qemu-img.exe"), "fake");

        var paths = RuntimeLayout.Resolve(_installDir, allowDevOverrides: true, name => name switch
        {
            "GRASSCORE_QEMU_DIR" => qemu,
            "GRASSCORE_OVMF_DIR" => Path.Combine(_root, "fake-firmware"),
            "GRASSCORE_QEMU_MAJOR" => "test-major",
            _ => null,
        });

        Assert.Equal(qemu, paths.QemuRoot);
        Assert.Equal(Path.Combine(qemu, "qemu-system-x86_64.exe"), paths.QemuSystem);
        Assert.Equal(Path.Combine(qemu, "qemu-img.exe"), paths.QemuImg);
        Assert.Equal(Path.Combine(_root, "fake-firmware"), paths.FirmwareDir);
        Assert.Equal("test-major", paths.QemuMajor);
    }
}
