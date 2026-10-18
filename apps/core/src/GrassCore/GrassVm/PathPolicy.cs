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
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, cmp) ||
            full.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, cmp))
        {
            var rel = System.IO.Path.GetRelativePath(root, full);
            return rel.Replace('\\', '/');
        }
        return full;
    }

    /// <summary>把存储的引用解析为绝对路径（包内相对路径基于包根解析）。</summary>
    public static string Resolve(GrassVmPackage package, string storedRef) =>
        System.IO.Path.IsPathRooted(storedRef)
            ? ResolveExternalOrPackagePath(package, storedRef)
            : ResolvePackagePath(package, storedRef);

    public static bool IsInsidePackage(GrassVmPackage package, string storedRef)
    {
        if (System.IO.Path.IsPathRooted(storedRef)) return false;
        try
        {
            _ = ResolvePackagePath(package, storedRef);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 按解析后的实际位置判断资源是否包外。与 DiskDevice.IsExternal 的字符串判断
    /// 不同，这里兼容历史配置中保存的包内绝对路径，同时把越界/非法引用视为外部，
    /// 让调用方采取保守拒绝或不参与快照链的处理。
    /// </summary>
    public static bool IsExternal(GrassVmPackage package, string storedRef)
    {
        try
        {
            var resolved = System.IO.Path.GetFullPath(Resolve(package, storedRef));
            var root = System.IO.Path.GetFullPath(package.Path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return !resolved.StartsWith(root, cmp);
        }
        catch (ArgumentException) { return true; }
        catch (IOException) { return true; }
    }

    private static string ResolvePackagePath(GrassVmPackage package, string storedRef)
    {
        var root = EnsureTrailingSeparator(System.IO.Path.GetFullPath(package.Path));
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, storedRef.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, cmp))
            throw new ArgumentException("包内资源路径不能越出 .grassvm 包目录。", nameof(storedRef));
        if (HasReparsePointBetween(root, full))
            throw new ArgumentException("包内资源不能通过符号链接或目录联接访问。", nameof(storedRef));
        return full;
    }

    private static string ResolveExternalOrPackagePath(GrassVmPackage package, string storedRef)
    {
        // 配置档案可能来自 Linux/macOS。Windows 的 Path.GetFullPath("/mnt/…")
        // 会把 POSIX 根路径误解释为当前盘符下的相对路径（例如 D:\mnt\…），
        // 破坏“包外绝对路径原样保留”的契约；单斜杠 POSIX 路径在这里保持
        // 原文，双斜杠 UNC 路径仍按 Windows 规则解析。
        if (OperatingSystem.IsWindows()
            && storedRef.Length > 1
            && storedRef[0] == '/'
            && storedRef[1] != '/')
        {
            // 保留跨平台配置中的 POSIX 原文，但仍检查 .NET 在当前 Windows
            // 盘符下解析到的实际候选路径，避免恰好命中 junction 时绕过父链校验。
            var localCandidate = Path.GetFullPath(storedRef);
            if (GrassVmPackage.ContainsReparsePoint(localCandidate))
                throw new ArgumentException("包外资源不能通过符号链接或目录联接访问。", nameof(storedRef));
            return storedRef;
        }

        var full = Path.GetFullPath(storedRef);
        var root = EnsureTrailingSeparator(Path.GetFullPath(package.Path));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.StartsWith(root, cmp))
        {
            var relative = Path.GetRelativePath(package.Path, full);
            return ResolvePackagePath(package, relative);
        }
        // 包外绝对路径也可能通过父目录 junction/symlink 指向用户未选择的宿主位置。
        // 只检查最终文件属性无法防住父目录被替换的 TOCTOU/路径穿越场景。
        if (GrassVmPackage.ContainsReparsePoint(full))
            throw new ArgumentException("包外资源不能通过符号链接或目录联接访问。", nameof(storedRef));
        return full;
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        var current = new DirectoryInfo(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (current is not null && !string.Equals(current.FullName, root.TrimEnd(Path.DirectorySeparatorChar), cmp))
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            current = current.Parent;
        }
        return false;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(System.IO.Path.DirectorySeparatorChar) || path.EndsWith(System.IO.Path.AltDirectorySeparatorChar)
            ? path
            : path + System.IO.Path.DirectorySeparatorChar;
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
