using System.Text.Json;
using GrassCore.GrassVm;

namespace GrassCore.Config;

/// <summary>
/// 单步迁移：把 schemaVersion == From 的 JSON 文档升级为 To。
/// 每一步独立、可测试；失败抛异常即整体回滚（旧 config 不被破坏）。
/// </summary>
public interface IConfigMigration
{
    int From { get; }
    int To { get; }
    /// <summary>就地升级 JsonDocument 对应的 JSON 文本，返回新文本（必须已把 schemaVersion 写为 To）。</summary>
    string Migrate(string json);
}

/// <summary>
/// 逐版本迁移链 v1 → v2 → … → current。
/// 升级前自动备份旧 config；迁移链在备份完成后才执行；任一步失败即中止，不写回。
/// </summary>
public sealed class ConfigMigrationChain
{
    private readonly SortedList<int, IConfigMigration> _steps = new(); // key = From

    public static ConfigMigrationChain CreateDefault() => new(); // v1 是首个版本，暂无历史迁移

    public void Register(IConfigMigration step)
    {
        if (step.From < 0 || step.To != step.From + 1 || step.To > VmConfiguration.CurrentSchemaVersion)
            throw new ArgumentException($"迁移步骤必须严格递进且不超过当前版本：{step.From}->{step.To}。", nameof(step));
        if (_steps.Values.Any(s => s.To == step.To || s.From == step.From))
            throw new InvalidOperationException($"重复注册迁移步骤 {step.From}->{step.To}");
        _steps.Add(step.From, step);
    }

    public string MigrateToCurrent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("schemaVersion", out var v) || v.ValueKind != JsonValueKind.Number)
            throw new JsonException("config.json 缺少 schemaVersion。");
        var version = v.GetInt32();
        if (version < 0) throw new JsonException($"非法 schemaVersion：{version}"); // 0 保留给测试/genesis 迁移链
        if (version > VmConfiguration.CurrentSchemaVersion)
            throw new NewerSchemaException($"此虚拟机已由较新版本的 Grass Block VM 升级（schema {version} > 当前支持的 {VmConfiguration.CurrentSchemaVersion}），无法在当前版本打开。");

        var text = json;
        while (version < VmConfiguration.CurrentSchemaVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
                throw new InvalidOperationException($"缺少 schema v{version} 的迁移步骤。");
            text = step.Migrate(text);
            if (step.To != version + 1)
                throw new InvalidOperationException($"迁移步骤未递进：{version}->{step.To}。");
            version = step.To;
        }
        if (version != VmConfiguration.CurrentSchemaVersion)
            throw new InvalidOperationException($"配置迁移未到达当前版本：{version}。");
        return text;
    }
}

public sealed class NewerSchemaException(string message) : Exception(message);

/// <summary>
/// .grassvm 配置存取。配置写入由 GrassCore 独占：临时文件 + 校验 + 原子替换，不维护 config-history。
/// </summary>
public sealed class ConfigStore(GrassVmPackage package, ConfigMigrationChain? chain = null)
{
    private readonly ConfigMigrationChain _chain = chain ?? ConfigMigrationChain.CreateDefault();

    public VmConfiguration Load()
    {
        if (!File.Exists(package.ConfigPath))
            throw new FileNotFoundException("缺少 config.json。", package.ConfigPath);
        return LoadJson(File.ReadAllText(package.ConfigPath));
    }

    /// <summary>加载并（若旧版本）先备份、再按迁移链升级、最后写回最新格式。</summary>
    public VmConfiguration LoadAndUpgrade()
    {
        var original = File.ReadAllText(package.ConfigPath);
        using var probe = JsonDocument.Parse(original);
        var version = probe.RootElement.TryGetProperty("schemaVersion", out var v) ? v.GetInt32() : 0;
        if (version == VmConfiguration.CurrentSchemaVersion)
            return LoadJson(original);

        // 备份旧 config（升级保护的最小形态；跨 QEMU major 时另有升级保护快照）
        var backupDir = package.TempPath;
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, $"config.backup.v{version}.json");
        File.WriteAllText(backup, original);

        var migrated = _chain.MigrateToCurrent(original);
        var config = LoadJson(migrated);
        Save(config); // 迁移成功才写回；失败则原文件原封不动
        return config;
    }

    public void Save(VmConfiguration config)
    {
        config.SchemaVersion = VmConfiguration.CurrentSchemaVersion;
        AtomicFile.WriteJsonValidated(package.ConfigPath, ConfigJson.Serialize(config));
    }

    private static VmConfiguration LoadJson(string json) => ConfigJson.Deserialize(json);
}
