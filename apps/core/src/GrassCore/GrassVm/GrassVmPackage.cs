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
        if (ContainsReparsePoint(Path))
            throw new ArgumentException("虚拟机包不能通过符号链接或目录联接访问。", nameof(path));
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

    public static bool ContainsReparsePoint(string path)
    {
        var current = new DirectoryInfo(System.IO.Path.GetFullPath(path));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current.Parent;
        }
        return false;
    }

    public static IEnumerable<GrassVmPackage> ScanLibraryRoot(string libraryRoot)
    {
        if (!Directory.Exists(libraryRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(libraryRoot))
        {
            // 以 "." 开头的是事务暂存目录（.creating-…/.importing-…）：导入/创建
            // 进行中（可达数分钟）或中断残留。它们没有 config.json，列出来就是
            // 一张打不开也删不掉的"幽灵卡"——扫描直接跳过
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (IsGrassVmDirectory(dir))
            {
                GrassVmPackage? package = null;
                try { package = new GrassVmPackage(dir); }
                catch (ArgumentException) { }
                catch (IOException) { }
                if (package is not null) yield return package;
            }
        }
    }

    /// <summary>
    /// 创建全新包结构（固定目录 + 空 config/state 占位由调用方写入）。
    /// 已存在同名包则抛异常——创建永远不覆盖既有 VM。
    /// </summary>
    public static GrassVmPackage CreateNew(string parentDir, string name)
    {
        var invalid = name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars());
        if (invalid >= 0 || string.IsNullOrWhiteSpace(name) || name.StartsWith('.')
            || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new ArgumentException("VM 名称不能包含文件系统非法字符。");
        var parent = System.IO.Path.GetFullPath(parentDir).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, name + Extension));
        var parentCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(System.IO.Path.GetDirectoryName(root), parent, parentCmp))
            throw new ArgumentException("VM 名称必须是存档位置的直接子目录名称。");
        // 检查-再-创建之间有竞态（并发 CreateVm 都通过 Directory.Exists 检查）：
        // 先在旁边造好完整目录，再用一次原子 rename 抢注——只有一个赢家
        var staging = System.IO.Path.Combine(parentDir, $".creating-{name}-{System.IO.Path.GetRandomFileName()}{Extension}");
        try
        {
            var staged = new GrassVmPackage(staging);
            staged.EnsureStructure();
            Directory.Move(staging, root);
            return new GrassVmPackage(root);
        }
        catch (IOException)
        {
            // 目标已存在（rename 失败）或暂存目录创建失败
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* 尽力 */ }
            if (Directory.Exists(root) || File.Exists(root))
                throw new InvalidOperationException($"已存在同名虚拟机：{System.IO.Path.GetFileName(root)}");
            throw;
        }
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

    /// <summary>
    /// 应用启动时清理残留 .grass-tmp 临时产物。原文件永不被原地破坏，因此清理无损。
    /// 只删写入停止超过 10 分钟的：刚崩溃 Core 的半成品可能正被重生的实例继续使用，
    /// 启动即删会把别人的活干掉；新鲜文件留给下一轮。
    /// </summary>
    public int CleanupResidualTempFiles()
    {
        var n = 0;
        if (!Directory.Exists(Path)) return 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var f in Directory.EnumerateFiles(Path, "*" + TransactionalDiskOps.TempSuffix, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }))
        {
            // commit 副本事务的暂存不能当垃圾删：overlay 可能已指向它（删了链就断）。
            // 由 RepairStagedOverlays → FinishCommitTempFiles 收尾换名
            if (f.EndsWith(TransactionalDiskOps.CommitTempSuffix, StringComparison.Ordinal)) continue;
            try
            {
                if (ContainsReparsePoint(f)) continue;
                if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                File.Delete(f);
                n++;
            }
            catch (IOException)
            {
                // 被占用 = 有人正在写：留给下一轮
            }
            catch (UnauthorizedAccessException)
            {
                // 逐文件兜底：一个文件清不掉不阻断其余清理
            }
        }
        // OVA 导出的工作目录（temp/ova-<guid>/，含全盘 VMDK 转换件）：导出中崩
        // 溃/被杀时 finally 不再有机会跑，残留可达整台 VM 的磁盘体积且没有任何
        // 组件回收——"交给系统清理"是谎言。同样遵守 10 分钟新鲜度门槛
        var tempDir = System.IO.Path.Combine(Path, TempDir);
        if (Directory.Exists(tempDir))
        {
            foreach (var d in Directory.EnumerateDirectories(tempDir, "ova-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(d) >= cutoff) continue;
                    Directory.Delete(d, recursive: true);
                    n++;
                }
                catch (IOException)
                {
                    // 被占用 = 导出可能仍在进行：留给下一轮
                }
                catch (UnauthorizedAccessException) { /* 逐目录兜底 */ }
            }
        }
        // 孤儿 suspend.state（挂起标记已不在、RAM 大小的废弃镜像——挂起失败回滚
        // 路径删不掉时的残渣）：启动时没有任何会话在写它；标记在场的【合法挂起】
        // 绝不能动
        try
        {
            // Load 会把损坏 state.json 静默视为新状态；此处若直接据此删除，可能
            // 抹掉仍然合法的挂起镜像（标记只是暂时无法解析）。先严格解析：损坏
            // 时留给 IntegrityReport/下一轮修复，绝不碰 suspend.state
            if (File.Exists(StatePath))
            {
                using var stateDoc = JsonDocument.Parse(File.ReadAllText(StatePath));
            }
            if (GrassCore.Config.VmState.Load(this).SuspendedStatePath is null)
            {
                var stray = System.IO.Path.Combine(Path, "suspend.state");
                if (File.Exists(stray))
                {
                    File.Delete(stray);
                    n++;
                }
            }
        }
        catch { /* state 读写失败/损坏/占用：留待下一轮 */ }
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
