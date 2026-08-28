using System.Text.Json;
using System.Text.Json.Serialization;
using GrassCore.Qemu;

namespace GrassCore.GrassVm;

/// <summary>
/// 一个 .grassvm 文件夹就是一台虚拟机。VM 本身没有 UUID——包就是身份。
/// 固定目录结构；未知文件/未知目录一律保留，绝不主动删除。
/// </summary>
public sealed class GrassVmPackage
{
    public const string Extension = ".grassvm";

    // 固定子目录（约定，均相对包根）
    public const string DisksDir = "disks";
    public const string SnapshotsDir = "snapshots";
    public const string FirmwareDir = "firmware";
    public const string ArtworkDir = "artwork";
    public const string LogsDir = "logs";
    public const string TempDir = "temp";
    public const string RuntimeDir = "runtime";

    public const string ConfigFile = "config.json";
    public const string StateFile = "state.json";
    public const string LockFile = "vm.lock";
    public const string SessionFile = "runtime/session.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public GrassVmPackage(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(Path), Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Not a {Extension} package: {path}", nameof(path));
        }
    }

    /// <summary>包根目录的绝对路径。这个路径就是这台 VM 的身份。</summary>
    public string Path { get; }

    /// <summary>包的显示名 = 文件夹名去掉扩展名。改名 = 改包名。</summary>
    public string Name => System.IO.Path.GetFileName(Path)[..^Extension.Length];

    public string DisksPath => System.IO.Path.Combine(Path, DisksDir);
    public string SnapshotsPath => System.IO.Path.Combine(Path, SnapshotsDir);
    public string FirmwarePath => System.IO.Path.Combine(Path, FirmwareDir);
    public string ArtworkPath => System.IO.Path.Combine(Path, ArtworkDir);
    public string LogsPath => System.IO.Path.Combine(Path, LogsDir);
    public string TempPath => System.IO.Path.Combine(Path, TempDir);
    public string RuntimePath => System.IO.Path.Combine(Path, RuntimeDir);
    public string ConfigPath => System.IO.Path.Combine(Path, ConfigFile);
    public string StatePath => System.IO.Path.Combine(Path, StateFile);
    public string LockPath => System.IO.Path.Combine(Path, LockFile);
    public string SessionPath => System.IO.Path.Combine(Path, SessionFile);

    /// <summary>Library Root 扫描：目录名以 .grassvm 结尾即被发现。发现 ≠ 已验证。</summary>
    public static bool IsGrassVmDirectory(string dirPath) =>
        string.Equals(System.IO.Path.GetExtension(dirPath), Extension, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<GrassVmPackage> ScanLibraryRoot(string libraryRoot)
    {
        if (!Directory.Exists(libraryRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(libraryRoot))
        {
            if (IsGrassVmDirectory(dir)) yield return new GrassVmPackage(dir);
        }
    }

    /// <summary>
    /// 创建全新包结构（固定目录 + 空 config/state 占位由调用方写入）。
    /// 已存在同名包则抛异常——创建永远不覆盖既有 VM。
    /// </summary>
    public static GrassVmPackage CreateNew(string parentDir, string name)
    {
        var invalid = name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars());
        if (invalid >= 0 || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("VM 名称不能包含文件系统非法字符。");
        var root = System.IO.Path.Combine(parentDir, name + Extension);
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException($"已存在同名虚拟机：{System.IO.Path.GetFileName(root)}");
        var pkg = new GrassVmPackage(root);
        pkg.EnsureStructure();
        return pkg;
    }

    /// <summary>确保固定目录存在。缺失 disks/ 等目录属于"确定无损"问题，允许保守自动修复。</summary>
    public void EnsureStructure()
    {
        Directory.CreateDirectory(Path);
        Directory.CreateDirectory(DisksPath);
        Directory.CreateDirectory(SnapshotsPath);
        Directory.CreateDirectory(FirmwarePath);
        Directory.CreateDirectory(ArtworkPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(TempPath);
        Directory.CreateDirectory(RuntimePath);
    }

    /// <summary>
    /// 完整性校验（发现 ≠ 已验证；第一次打开/启动时执行）。
    /// 只报告问题；"确定无损"的修复（缺目录、state.json 损坏重建）单独列出，
    /// 任何可能改动 VM 数据的修复都必须征得用户确认（保守修复哲学）。
    /// </summary>
    public IntegrityReport CheckIntegrity()
    {
        var report = new IntegrityReport();
        if (!File.Exists(ConfigPath))
        {
            report.FatalProblems.Add("缺少 config.json，无法确定这台虚拟机的配置。");
        }
        foreach (var (dir, label) in new[]
        {
                 (DisksPath, DisksDir), (SnapshotsPath, SnapshotsDir), (FirmwarePath, FirmwareDir),
                 (ArtworkPath, ArtworkDir), (LogsPath, LogsDir), (TempPath, TempDir), (RuntimePath, RuntimeDir),
             })
        {
            if (!Directory.Exists(dir)) report.SafeRepairs.Add(new SafeRepair($"缺少 {label}/ 目录", () => Directory.CreateDirectory(dir)));
        }
        if (File.Exists(StatePath))
        {
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(StatePath));
            }
            catch (JsonException)
            {
                // state.json 只是可随 VM 移动的非关键状态；损坏可确定无损地重建
                report.SafeRepairs.Add(new SafeRepair("state.json 已损坏", () => AtomicFile.ReplaceFile(StatePath, "{}")));
            }
        }
        // 残留 .grass-tmp：应用启动时自动删除，不尝试断点续传（由 TransactionalDiskOps 清理）
        return report;
    }

    /// <summary>应用启动时清理所有残留 .grass-tmp 临时产物。原文件永不被原地破坏，因此清理无损。</summary>
    public int CleanupResidualTempFiles()
    {
        var n = 0;
        if (!Directory.Exists(Path)) return 0;
        foreach (var f in Directory.EnumerateFiles(Path, "*" + TransactionalDiskOps.TempSuffix, SearchOption.AllDirectories))
        {
            File.Delete(f);
            n++;
        }
        return n;
    }
}

public sealed class IntegrityReport
{
    /// <summary>无法通过任何修复解决的问题。</summary>
    public List<string> FatalProblems { get; } = new();

    /// <summary>确定无损、可自动执行的修复动作。</summary>
    public List<SafeRepair> SafeRepairs { get; } = new();

    public bool IsFatal => FatalProblems.Count > 0;

    public void ApplySafeRepairs()
    {
        foreach (var r in SafeRepairs) r.Apply();
    }
}

public sealed record SafeRepair(string Description, Action Apply);
