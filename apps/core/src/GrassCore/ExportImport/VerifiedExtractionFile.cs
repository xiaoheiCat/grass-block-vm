using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using GrassCore.GrassVm;
using GrassCore.Rpc;

namespace GrassCore.ExportImport;

/// <summary>
/// 创建解压目标后，用文件句柄解析其最终路径，再允许写入内容。
/// 这样父目录在路径检查与打开之间被替换成 junction/symlink 时，
/// 只会留下一个尚未写入内容的空文件，并立即失败，不会把档案数据写到包外。
/// </summary>
internal static class VerifiedExtractionFile
{
    /// <summary>
    /// 将输出路径锚定到当前目录的最终路径。后续文件操作使用返回值，
    /// 因此原始父目录即使随后被替换成 junction/symlink，也不会把输出重定向到
    /// 另一棵目录树。不存在的末级目录会在已存在的祖先上拼回。
    /// </summary>
    public static string ResolveStablePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (GrassVmPackage.IsReparsePointOrLink(full))
            throw new GrassCoreException("导出目标或其父目录包含符号链接或目录联接，已拒绝写入。");
        if (!OperatingSystem.IsWindows())
            return full;

        var missing = new Stack<string>();
        var current = full;
        while (!Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                throw new GrassCoreException("无法确认导出目标的实际路径，已拒绝写入。");
            missing.Push(name);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                throw new GrassCoreException("无法确认导出目标的实际路径，已拒绝写入。");
            current = parent;
        }
        var stable = GetFinalPathForDirectory(current);
        // 目录句柄打开与路径检查之间若发生重解析点替换，句柄会解析到
        // 另一棵目录树；再次检查原路径，拒绝这次竞态，而不是把外部路径
        // 当成合法的稳定目标继续使用。
        if (GrassVmPackage.IsReparsePointOrLink(current))
            throw new GrassCoreException("导出目标或其父目录包含符号链接或目录联接，已拒绝写入。");
        while (missing.Count > 0)
            stable = Path.Combine(stable, missing.Pop());
        return stable;
    }

    public static FileStream OpenNewWithin(string root, string target)
    {
        // 必须在创建目标前固定根目录的最终路径；若随后 root 被替换成
        // junction，目标句柄会与这个旧锚点比较并失败，而不会把包外目录
        // 重新解析成“合法根”。
        var stableRoot = ResolveStablePath(root);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var actual = GetFinalPath(stream.SafeFileHandle, target);
            if (!IsWithin(stableRoot, actual))
                throw new GrassCoreException("解压目标在打开后解析到 staging 目录之外，已拒绝写入。");
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 打开已有输入文件并持有禁止删除/替换的读取句柄。句柄打开后再校验
    /// 最终路径，避免检查通过后文件被换成链接；句柄保持到调用方完成复制或
    /// qemu-img 转换，阻断 Windows 上的源文件 TOCTOU。
    /// </summary>
    public static FileStream OpenExistingVerified(string path)
    {
        var expected = ResolveStablePath(path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            var actual = GetFinalPath(stream.SafeFileHandle, expected);
            var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(actual, NormalizeFinalPath(expected), cmp))
                throw new GrassCoreException("导出源文件在打开后解析到包外或链接目标，已拒绝读取。");
            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle, string fallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            var fd = handle.DangerousGetHandle();
            if (OperatingSystem.IsLinux())
            {
                var unixBuffer = new StringBuilder(4096);
                var unixLength = UnixReadLink($"/proc/self/fd/{fd.ToInt64()}", unixBuffer, (nuint)unixBuffer.Capacity);
                if (unixLength <= 0 || unixLength >= unixBuffer.Capacity)
                    throw new GrassCoreException("无法确认文件句柄的实际路径，已拒绝写入或读取。");
                return Path.GetFullPath(unixBuffer.ToString(0, checked((int)unixLength)));
            }
            if (OperatingSystem.IsMacOS())
            {
                var macBuffer = new StringBuilder(1024);
                if (UnixFcntl(fd, MacFGetPath, macBuffer) != 0)
                    throw new GrassCoreException("无法确认文件句柄的实际路径，已拒绝写入或读取。");
                return Path.GetFullPath(macBuffer.ToString());
            }
            throw new GrassCoreException("当前平台无法确认文件句柄的实际路径，已拒绝写入或读取。");
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            throw new GrassCoreException("无法确认解压目标的实际路径，已拒绝写入。");
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new GrassCoreException("无法确认解压目标的实际路径，已拒绝写入。");
        }
        return NormalizeFinalPath(buffer.ToString());
    }

    private static bool IsWithin(string root, string path)
    {
        var rootFull = NormalizeFinalPath(Path.GetFullPath(root))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathFull = NormalizeFinalPath(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return pathFull.StartsWith(rootFull, cmp);
    }

    private static string GetFinalPathForDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            return Path.GetFullPath(path);

        var fullPath = Path.GetFullPath(path);
        using var handle = CreateFile(
            fullPath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new GrassCoreException("无法确认解压 staging 目录的实际路径，已拒绝写入。");
        return GetFinalPath(handle, fullPath);
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int MacFGetPath = 50;

    [DllImport("libc", EntryPoint = "readlink", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint UnixReadLink(string path, StringBuilder buffer, nuint bufferSize);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int UnixFcntl(IntPtr fd, int command, StringBuilder buffer);
}
