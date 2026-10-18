using System.Text.Json.Serialization;

namespace GrassCore.Snapshots;

/// <summary>
/// 快照 = "整台虚拟机时间点"：虚拟硬件配置 + 包内磁盘状态；运行中快照还含内存状态。
/// 每个快照保存完整 config.json 副本（不做差异）；使用 QCOW2 外部 overlay 链，不用内部快照。
/// 快照有不可变 UUID；VM 自身不使用 UUID。允许树状分支；UI 显示真实树，不允许拖动重排。
/// </summary>
public sealed class Snapshot
{
    public required string Uuid { get; init; }
    public string? ParentSnapshotUuid { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool HasMemoryState { get; init; }
    /// <summary>快照创建时的完整 VmConfiguration 副本（JSON）。</summary>
    public required string FullConfigSnapshot { get; init; }
    /// <summary>每个包内磁盘对应一个 overlay 引用（deviceId → snapshots/&lt;uuid&gt;/disks/ 内文件）。</summary>
    public Dictionary<string, string> DiskOverlayRefs { get; set; } = new();
    /// <summary>升级保护快照：隐藏保留，24 小时后的第一次正常关机才自动删除；用户普通快照不影响它。</summary>
    public bool IsUpgradeProtection { get; init; }
    public DateTimeOffset? UpgradeProtectionCreatedAt { get; init; }
    /// <summary>升级保护时的元数据备份目录（temp/upgrade-backup-*；快照删除时一并回收）。</summary>
    public string? UpgradeProtectionBackupDir { get; init; }
}

/// <summary>
/// 快照树操作（计划阶段）。所有 destructive op 先生成依赖图与计划，再执行。
/// </summary>
public sealed class SnapshotTree
{
    private readonly Dictionary<string, Snapshot> _byUuid;

    public SnapshotTree(IEnumerable<Snapshot> snapshots)
    {
        _byUuid = snapshots.ToDictionary(s => s.Uuid, StringComparer.OrdinalIgnoreCase);
        Root = snapshots.Where(s => s.ParentSnapshotUuid is null).ToList();
    }

    public static bool IsValidUuid(string uuid) => Guid.TryParse(uuid, out _);

    public IReadOnlyCollection<Snapshot> All => _byUuid.Values;
    public List<Snapshot> Root { get; }

    public Snapshot Get(string uuid) => _byUuid[uuid];

    public bool TryGet(string uuid, out Snapshot? s) => _byUuid.TryGetValue(uuid, out s);

    public IEnumerable<Snapshot> ChildrenOf(string uuid) =>
        _byUuid.Values.Where(s => string.Equals(s.ParentSnapshotUuid, uuid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.CreatedAt);

    /// <summary>从根到某快照的 overlay 链（磁盘链重写用）。</summary>
    public IReadOnlyList<Snapshot> ChainToRoot(string uuid)
    {
        var chain = new List<Snapshot>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Snapshot? cur = _byUuid[uuid];
        while (cur is not null)
        {
            if (!visited.Add(cur.Uuid))
                throw new InvalidOperationException("快照树包含循环父引用。");
            chain.Add(cur);
            cur = cur.ParentSnapshotUuid is null ? null : (TryGet(cur.ParentSnapshotUuid, out var p) ? p : null);
        }
        return chain;
    }

    /// <summary>某快照的全部后代（含自身）。</summary>
    public ISet<string> DescendantsOf(string uuid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uuid };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var s in _byUuid.Values.Where(s => s.ParentSnapshotUuid is not null))
            {
                if (result.Contains(s.ParentSnapshotUuid!) && result.Add(s.Uuid)) grew = true;
            }
        }
        return result;
    }
}

/// <summary>链接克隆依赖：子 VM 记录父 .grassvm 位置 + 父快照 UUID。</summary>
public sealed record LinkedCloneReference(string ChildVmName, string ChildVmPath, string ParentSnapshotUuid);

/// <summary>
/// 删除快照的计划结果：
/// - 没有外部链接克隆依赖的非叶子快照：自动重写/合并 overlay 链，保留后代。
/// - 是链接克隆基线：先列出受影响链接克隆并明确警告；用户仍可删除，相关链接克隆随后失效。
/// </summary>
public sealed record SnapshotDeletePlan(
    string TargetUuid,
    bool HasLinkedCloneDependencies,
    IReadOnlyList<LinkedCloneReference> AffectedLinkedClones,
    IReadOnlyList<(string ChildUuid, string NewParentUuid)> Rebindings)
{
    public bool RequiresMerge => Rebindings.Count > 0;
}

public sealed class SnapshotPlanner
{
    /// <summary>
    /// 生成删除计划。链式重写（block commit/rebase）本身是高风险存储操作，由 GrassCore 独占执行；
    /// 这里产出"把子快照重挂到被删节点的父级"的重绑定表。
    /// </summary>
    public static SnapshotDeletePlan PlanDelete(
        SnapshotTree tree,
        string targetUuid,
        IEnumerable<LinkedCloneReference> linkedClones)
    {
        var target = tree.Get(targetUuid);
        var affected = linkedClones
            .Where(lc => string.Equals(lc.ParentSnapshotUuid, targetUuid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 子快照重挂到被删节点的父级；删除根节点时子节点各自成为新根（NewParent = null 语义以 "__root__" 表达）
        var rebindings = target.ParentSnapshotUuid is null
            ? tree.ChildrenOf(targetUuid).Select(child => (child.Uuid, "__root__")).ToList()
            : tree.ChildrenOf(targetUuid).Select(child => (child.Uuid, target.ParentSnapshotUuid!)).ToList();

        return new SnapshotDeletePlan(targetUuid, affected.Count > 0, affected, rebindings);
    }

    /// <summary>
    /// 恢复旧快照前的破坏范围：当前未快照的工作状态将永久丢失。
    /// 恢复时硬件配置和外部资源路径引用一起回滚；包外磁盘不参与数据回滚（仅引用关系回滚）。
    /// </summary>
    public sealed record RestorePlan(string TargetUuid, IReadOnlyList<string> Warnings);

    public static RestorePlan PlanRestore(SnapshotTree tree, string targetUuid)
    {
        var warnings = new List<string>
        {
            "恢复后，当前尚未创建快照的更改将永久丢失；如果需要保留，请先创建快照。",
            "虚拟硬件配置将回滚到快照创建时的状态。",
            "包外虚拟硬盘的数据内容不会回滚，恢复后仍使用它们当前的数据。",
        };
        return new RestorePlan(targetUuid, warnings);
    }

    /// <summary>
    /// 升级保护快照清理判定：保留至少 24 小时，并在 24 小时后的第一次正常关机时自动删除。
    /// 强制关机、崩溃、QEMU 异常退出都不算正常关机。
    /// </summary>
    public static bool ShouldDeleteUpgradeProtection(Snapshot s, DateTimeOffset now, bool cleanShutdown)
        => s.IsUpgradeProtection
           && cleanShutdown
           && s.UpgradeProtectionCreatedAt is { } created
           && now - created >= TimeSpan.FromHours(24);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotOpKind
{
    Create,
    Restore,
    Delete,
    Merge,
}
