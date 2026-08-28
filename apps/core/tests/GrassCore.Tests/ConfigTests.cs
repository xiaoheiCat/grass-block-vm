using System.Text.Json;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Profiles;
using Xunit;

namespace GrassCore.Tests;

public class ConfigMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GrassVmPackage _pkg;

    public ConfigMigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _pkg = GrassVmPackage.CreateNew(_dir, "Test VM");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Save_IsAtomic_And_ProducesValidJson()
    {
        var store = new ConfigStore(_pkg);
        var config = OsProfileLibrary.CreateDefaultConfig("windows-11", "Test VM");
        store.Save(config);

        var json = File.ReadAllText(_pkg.ConfigPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json); // 不抛 = 完整 JSON
        Assert.Equal(VmConfiguration.CurrentSchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Load_AndUpgrade_MigratesStepwise_And_BacksUpOldConfig()
    {
        // 构造 v0 旧格式（仅用于验证迁移链机制的测试 Profile；生产链 v1 起步）
        var v0Json = """{"schemaVersion":0,"name":"Old VM","memoryMB":4096}""";
        File.WriteAllText(_pkg.ConfigPath, v0Json);

        var chain = new ConfigMigrationChain();
        chain.Register(new V0ToV1Migration());

        var store = new ConfigStore(_pkg, chain);
        var config = store.LoadAndUpgrade();

        Assert.Equal(VmConfiguration.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal("Old VM", config.Name);
        Assert.Equal(4096, config.MemoryMiB); // memoryMB → memoryMiB 字段重命名
        // 升级前备份存在
        Assert.True(Directory.EnumerateFiles(_pkg.TempPath, "config.backup.v0.json").Any());
        // 写回后文件已是新版本
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_pkg.ConfigPath));
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void MigrationFailure_LeavesOriginalFileUntouched()
    {
        var v0Json = """{"schemaVersion":0,"name":"Old VM","memoryMB":4096}""";
        File.WriteAllText(_pkg.ConfigPath, v0Json);

        var chain = new ConfigMigrationChain();
        chain.Register(new ThrowingMigration());

        var store = new ConfigStore(_pkg, chain);
        Assert.ThrowsAny<JsonException>(() => store.LoadAndUpgrade());

        // 原文件原封不动（失败回滚 = 没有写回）
        Assert.Equal(v0Json, File.ReadAllText(_pkg.ConfigPath));
    }

    [Fact]
    public void NewerSchema_IsRejected_WithClearMessage()
    {
        File.WriteAllText(_pkg.ConfigPath, """{"schemaVersion":99,"name":"Future VM"}""");
        var chain = ConfigMigrationChain.CreateDefault();
        var ex = Assert.Throws<NewerSchemaException>(() => chain.MigrateToCurrent(File.ReadAllText(_pkg.ConfigPath)));
        Assert.Contains("较新版本", ex.Message);
    }

    /// <summary>测试用 v0→v1：memoryMB 改名 memoryMiB。</summary>
    private sealed class V0ToV1Migration : IConfigMigration
    {
        public int From => 0;
        public int To => 1;
        public string Migrate(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == "schemaVersion") { w.WriteNumber("schemaVersion", 1); continue; }
                    if (p.Name == "memoryMB") { w.WriteNumber("memoryMiB", p.Value.GetInt32()); continue; }
                    p.WriteTo(w);
                }
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private sealed class ThrowingMigration : IConfigMigration
    {
        public int From => 0;
        public int To => 1;
        public string Migrate(string json) => throw new JsonException("boom");
    }
}

public class VmLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));

    public VmLockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Acquire_ThenSecondAcquire_IsBlocked()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Locked VM");
        var l1 = new VmLock(pkg);
        l1.Acquire();
        Assert.True(l1.IsLocked);

        var l2 = new VmLock(pkg);
        var ex = Assert.Throws<VmLockedException>(() => l2.Acquire());
        Assert.Contains("解除锁定", ex.Message); // 引导手动解除锁
    }

    [Fact]
    public void CrashResidue_IsNeverAutoCleared()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Crash VM");
        new VmLock(pkg).Acquire();
        // 模拟崩溃后：另一实例只读查看，绝不自动清锁
        Assert.True(File.Exists(pkg.LockPath));
        var l2 = new VmLock(pkg);
        Assert.Throws<VmLockedException>(() => l2.Acquire());
        Assert.True(File.Exists(pkg.LockPath));
    }

    [Fact]
    public void ForceUnlockByUser_ReleasesLock()
    {
        var pkg = GrassVmPackage.CreateNew(_dir, "Unlock VM");
        var l = new VmLock(pkg);
        l.Acquire();
        l.ForceUnlockByUser(); // UI 已确认风险
        Assert.False(l.IsLocked);
        l.Acquire(); // 可以重新接管
    }
}

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grassvm-tests-" + Guid.NewGuid().ToString("N"));
    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void ReplaceFile_LeavesNoTempResidue()
    {
        var target = Path.Combine(_dir, "state.json");
        AtomicFile.ReplaceFile(target, "{\"a\":1}");
        AtomicFile.ReplaceFile(target, "{\"a\":2}");
        Assert.Equal("{\"a\":2}", File.ReadAllText(target));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.grass-tmp"));
    }

    [Fact]
    public void WriteJsonValidated_RejectsInvalidJson_WithoutTouchingTarget()
    {
        var target = Path.Combine(_dir, "config.json");
        File.WriteAllText(target, "{\"old\":true}");
        Assert.ThrowsAny<JsonException>(() => AtomicFile.WriteJsonValidated(target, "{ this is not json"));
        Assert.Equal("{\"old\":true}", File.ReadAllText(target)); // 原内容未被破坏
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.grass-tmp"));
    }
}
