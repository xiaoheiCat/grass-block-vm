namespace GrassCore.GrassVm;

/// <summary>
/// .grassvm 路径规范：包内资源一律保存相对路径；用户即使选择了绝对路径，
/// 只要检测到资源实际位于同一包内，就自动改写为相对路径。
/// 包外资源保存绝对路径；路径失效时启动前要求重新定位，成功后永久写回（不提供"仅本次使用"）。
/// </summary>
public static class PathPolicy
{
    /// <summary>规范化资源引用：包内 → 相对（正斜杠分隔，跨平台稳定）；包外 → 绝对。</summary>
    public static string NormalizeReference(GrassVmPackage package, string chosenPath)
    {
        var full = System.IO.Path.GetFullPath(chosenPath);
        var root = package.Path;
        if (full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var rel = System.IO.Path.GetRelativePath(root, full);
            return rel.Replace('\\', '/');
        }
        return full;
    }

    /// <summary>把存储的引用解析为绝对路径（包内相对路径基于包根解析）。</summary>
    public static string Resolve(GrassVmPackage package, string storedRef) =>
        System.IO.Path.IsPathRooted(storedRef)
            ? storedRef
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(package.Path, storedRef.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public static bool IsInsidePackage(GrassVmPackage package, string storedRef) =>
        !System.IO.Path.IsPathRooted(storedRef);
}

/// <summary>外部资源重定位结果：能唯一匹配的资源自动迁移；多个候选或无法匹配时让用户决策。</summary>
public enum RelocationOutcome
{
    /// <summary>旧路径仍有效，无需处理。</summary>
    StillValid,
    /// <summary>在候选中能唯一、可靠地匹配 → 自动迁移并永久写回 config。</summary>
    AutoRelocated,
    /// <summary>存在多个合理候选 → 必须询问用户，不能擅自猜测。</summary>
    NeedsUserDecision,
    /// <summary>找不到任何候选 → 交由用户处理（重新定位 / 取消）。</summary>
    Missing,
}

/// <summary>外部资源重定位流程（B + 自动迁移优先）。</summary>
public static class ResourceRelocator
{
    public sealed record Result(RelocationOutcome Outcome, string NewPath, IReadOnlyList<string> Candidates);

    public static Result Relocate(string storedAbsPath, string fileName, IEnumerable<string> candidates)
    {
        if (File.Exists(storedAbsPath)) return new Result(RelocationOutcome.StillValid, storedAbsPath, Array.Empty<string>());

        // 只按"文件名相同"做唯一匹配——多个同名候选不能猜
        var matches = candidates.Where(c =>
            string.Equals(System.IO.Path.GetFileName(c), fileName, StringComparison.OrdinalIgnoreCase)).ToList();

        return matches.Count switch
        {
            1 => new Result(RelocationOutcome.AutoRelocated, matches[0], matches),
            0 => new Result(RelocationOutcome.Missing, storedAbsPath, Array.Empty<string>()),
            _ => new Result(RelocationOutcome.NeedsUserDecision, storedAbsPath, matches),
        };
    }
}
