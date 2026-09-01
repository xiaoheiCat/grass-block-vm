using System.Text;

namespace GrassCore.GrassVm;

/// <summary>
/// 原子文件写入：临时文件 → 校验 → 原子替换。
/// GrassCore 是 .grassvm 的唯一写入者；所有持久化 JSON 写入都必须经过这里，
/// 避免半写入损坏。不做 config-history（误改的恢复由快照承担）。
/// </summary>
public static class AtomicFile
{
    /// <summary>写入文本文件：先写同目录临时文件，再原子替换。onValidate 用于"完成并校验成功后再切换"。</summary>
    public static void ReplaceFile(string targetPath, string content, Action<string>? validate = null)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(dir);
        var tmp = System.IO.Path.Combine(dir, System.IO.Path.GetRandomFileName() + ".grass-tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            validate?.Invoke(tmp);
            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>JSON 专用写入：临时文件写完后重新解析校验，确保不是半截/坏 JSON。</summary>
    public static void WriteJsonValidated(string targetPath, string json)
    {
        ReplaceFile(targetPath, json, tmp =>
        {
            using var _ = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tmp));
        });
    }
}

/// <summary>
/// vm.lock 语义：包根目录下的空文件。存在即视为 VM 被占用；
/// 异常残留锁绝不自动清除，必须由用户手动"解除锁定"并接受风险警告。
/// </summary>
public sealed class VmLock : IDisposable
{
    // 路径比较必须与宿主文件系统一致：Linux/macOS 区分大小写，不能把两个
    // 大小写不同的包误认为同一把进程内锁。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileStream> Handles =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly GrassVmPackage _package;
    private FileStream? _handle;

    public VmLock(GrassVmPackage package) => _package = package;

    public bool IsLocked => File.Exists(_package.LockPath);

    /// <summary>
    /// 获取锁。已存在 vm.lock 直接阻止并引导手动解除锁——本机崩溃后也不自动接管，
    /// 因为包可能在 NAS/同步盘上被另一台电脑持有。
    /// </summary>
    public void Acquire()
    {
        if (_handle is not null) return;
        if (IsLocked)
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        Directory.CreateDirectory(_package.Path);
        // 排他创建（原子）：检查-再-写入在并发双击下会让两个调用都通过检查——
        // 两个 QEMU 同写一张盘正是 vm.lock 要防的事故。CreateNew 在文件已存在时抛 IOException。
        FileStream? fs = null;
        try
        {
            fs = new FileStream(_package.LockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            fs.WriteByte(0);
            fs.Flush(true);
            if (!Handles.TryAdd(_package.LockPath, fs))
            {
                fs.Dispose();
                fs = null;
                throw new VmLockedException("此虚拟机已被当前进程占用。");
            }
            _handle = fs;
            fs = null;
        }
        catch (IOException) when (File.Exists(_package.LockPath))
        {
            try { fs?.Dispose(); } catch { }
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        }
        catch
        {
            try { fs?.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>正常释放 VM 时删除 vm.lock。仅当锁确实存在且由本次会话持有时调用。</summary>
    public void Release()
    {
        var h = Interlocked.Exchange(ref _handle, null);
        if (h is null)
        {
            // 生命周期收尾经常发生在 Core 重启后（AdoptRunningVms / Exited 监视器）：
            // 原进程的 FileStream 已由操作系统释放，但 vm.lock 文件仍在。先尝试把
            // 现存文件以 FileShare.None 重新登记到本进程；只有取得排他句柄才允许
            // 删除，避免把另一进程/另一台机器仍持有的锁误当残留清掉。
            // 如果当前进程的另一个 VmLock 实例仍持有句柄，不能从全局表移除并
            // 代为释放；否则一个无句柄的临时实例会误杀正在运行 VM 的锁。
            if (Handles.ContainsKey(_package.LockPath)) return;
            h = TryAttachExistingHandle();
        }
        else
        {
            Handles.TryRemove(_package.LockPath, out _);
        }
        if (h is null) return; // 不得误删别的进程持有的锁
        try { h?.Dispose(); } catch { }
        if (File.Exists(_package.LockPath)) File.Delete(_package.LockPath);
    }

    private FileStream? TryAttachExistingHandle()
    {
        if (!File.Exists(_package.LockPath)) return null;
        try
        {
            var fs = new FileStream(_package.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (Handles.TryAdd(_package.LockPath, fs)) return fs;
            fs.Dispose();
            // 另一个本进程实例已经持有它：不能把对方的句柄交给本次
            // Release，否则会误关对方的排他锁并删除仍在使用的 vm.lock。
            return null;
        }
        catch (IOException)
        {
            // 仍被其他进程/主机持有，保留锁文件。
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 接管 Core 重启后留下的锁文件。与 <see cref="Acquire"/> 不同，
    /// 该操作只接受已经存在的 vm.lock，不会在文件不存在时创建新锁；
    /// 只有成功取得排他句柄，调用方才可以把运行中的 QEMU 纳入本进程生命周期。
    /// </summary>
    public bool TryAcquireExisting()
    {
        if (_handle is not null) return true;
        var handle = TryAttachExistingHandle();
        if (handle is null) return false;
        _handle = handle;
        return true;
    }

    public void Dispose() => Release();

    /// <summary>
    /// 用户手动"解除锁定"。调用方（UI/RPC 层）必须先展示风险确认：
    /// 错误解除并同时启动同一虚拟机可能导致虚拟磁盘损坏或数据丢失。
    /// </summary>
    public void ForceUnlockByUser()
    {
        var h = Interlocked.Exchange(ref _handle, null);
        if (h is not null)
        {
            // 只有本对象取得的句柄才可以在这里释放；不能从全局表拿走另一个
            // VmLock 实例的句柄，否则 UI 的“解除锁定”会误关掉本进程正在运行的 VM。
            Handles.TryRemove(_package.LockPath, out _);
            try { h.Dispose(); } catch { }
            if (File.Exists(_package.LockPath)) File.Delete(_package.LockPath);
            return;
        }

        if (Handles.ContainsKey(_package.LockPath))
            throw new VmLockedException("此虚拟机仍由当前进程占用，不能强制解除锁定。");

        // 没有本进程句柄时，只有成功以独占方式接管现有文件，才能证明它
        // 已经是残留锁。若仍被另一进程/另一台电脑持有，保留锁文件，避免
        // Unix 上 unlink 仍打开的文件造成两个 QEMU 同时写盘。
        h = TryAttachExistingHandle();
        if (h is null)
        {
            if (File.Exists(_package.LockPath))
                throw new VmLockedException("无法确认锁已失效：它仍可能由另一实例或另一台电脑持有。");
            return;
        }
        try { h.Dispose(); } catch { }
        if (File.Exists(_package.LockPath)) File.Delete(_package.LockPath);
    }
}

public sealed class VmLockedException(string message) : Exception(message);
