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
    // vm.lock.guard 的句柄是实际的跨进程排他锁；FileOptions.DeleteOnClose
    // 确保 Core 崩溃后不会留下一个可被误认为仍在使用的 guard。vm.lock 本身
    // 由 _markerHandle 持有 FileShare.None，作为需要人工确认的持久化残留标记。
    private FileStream? _handle;
    private FileStream? _markerHandle;

    public VmLock(GrassVmPackage package) => _package = package;

    public bool IsLocked => File.Exists(_package.LockPath) || File.Exists(_package.LockGuardPath);

    /// <summary>
    /// 获取锁。已存在 vm.lock 直接阻止并引导手动解除锁——本机崩溃后也不自动接管，
    /// 因为包可能在 NAS/同步盘上被另一台电脑持有。
    /// </summary>
    public void Acquire()
    {
        if (_handle is not null) return;
        if (IsLocked || File.Exists(_package.LockGuardPath))
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        Directory.CreateDirectory(_package.Path);
        // 先原子创建并持有持久化标记，再创建独占 guard。两个进程的并发
        // CreateNew 只有一个赢家；guard 使用 FileShare.None，外部进程不能
        // 在生命周期内删除/替换它来绕过互斥。
        FileStream? marker = null;
        FileStream? guard = null;
        var markerCreated = false;
        try
        {
            marker = new FileStream(_package.LockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, options: FileOptions.WriteThrough);
            markerCreated = true;
            marker.WriteByte(0);
            marker.Flush(true);
            guard = new FileStream(_package.LockGuardPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, options: FileOptions.DeleteOnClose);
            if (!Handles.TryAdd(_package.LockPath, guard))
                throw new VmLockedException("此虚拟机已被当前进程占用。");
            _markerHandle = marker;
            _handle = guard;
            marker = null;
            guard = null;
        }
        catch (IOException) when (File.Exists(_package.LockPath) || File.Exists(_package.LockGuardPath))
        {
            try { guard?.Dispose(); } catch { }
            try { marker?.Dispose(); } catch { }
            // 若 guard 创建失败且没有其他持有者，撤销本次创建的标记；
            // 已存在 guard 时保留标记，避免把活锁伪装成无锁状态。
            if (markerCreated && !Handles.ContainsKey(_package.LockPath) && !File.Exists(_package.LockGuardPath))
                TryDeleteMarker();
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        }
        catch
        {
            try { guard?.Dispose(); } catch { }
            try { marker?.Dispose(); } catch { }
            if (markerCreated && !Handles.ContainsKey(_package.LockPath) && !File.Exists(_package.LockGuardPath))
                TryDeleteMarker();
            throw;
        }
    }

    /// <summary>正常释放 VM 时删除 vm.lock。仅当锁确实存在且由本次会话持有时调用。</summary>
    public void Release()
    {
        if (_handle is null)
        {
            // 生命周期收尾经常发生在 Core 重启后：只有成功以独占方式
            // 接管残留标记和 guard，才允许清理；另一个进程持有 marker/guard
            // 时这里会失败并保留锁。
            if (Handles.ContainsKey(_package.LockPath)) return;
            if (TryAttachExistingHandle() is null) return;
        }
        var guard = _handle;
        if (guard is null) return;
        try
        {
            var marker = Interlocked.Exchange(ref _markerHandle, null);
            try { marker?.Dispose(); } catch { }
            if (!TryDeleteMarker())
            {
                return;
            }
        }
        catch { return; /* 标记删除失败时继续持有 guard，交给后续重试 */ }
        Interlocked.Exchange(ref _handle, null);
        RemoveRegisteredHandle(guard);
        try { guard.Dispose(); } catch { /* DeleteOnClose 尽力清理 guard */ }
    }

    private FileStream? TryAttachExistingHandle()
    {
        if (!File.Exists(_package.LockPath)) return null;
        if (Handles.ContainsKey(_package.LockPath)) return null;
        FileStream? marker = null;
        FileStream? guard = null;
        try
        {
            // 新旧版本都必须能安全处理：旧版本只持有 vm.lock，若它仍在运行，
            // FileShare.None 会因共享冲突失败；若只是残留，则可在此取得 marker。
            marker = new FileStream(_package.LockPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, options: FileOptions.WriteThrough);
            guard = new FileStream(_package.LockGuardPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, options: FileOptions.DeleteOnClose);
            if (!Handles.TryAdd(_package.LockPath, guard)) return null;
            _markerHandle = marker;
            _handle = guard;
            marker = null;
            guard = null;
            return _handle;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { guard?.Dispose(); } catch { }
            try { marker?.Dispose(); } catch { }
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
        return TryAttachExistingHandle() is not null;
    }

    public void Dispose() => Release();

    /// <summary>
    /// 用户手动"解除锁定"。调用方（UI/RPC 层）必须先展示风险确认：
    /// 错误解除并同时启动同一虚拟机可能导致虚拟磁盘损坏或数据丢失。
    /// </summary>
    public void ForceUnlockByUser()
    {
        var guard = _handle;
        if (guard is not null)
        {
            // 只有本对象取得的句柄才可以在这里释放；不能从全局表拿走另一个
            // VmLock 实例的句柄，否则 UI 的“解除锁定”会误关掉本进程正在运行的 VM。
            var marker = Interlocked.Exchange(ref _markerHandle, null);
            try { marker?.Dispose(); } catch { }
            if (!TryDeleteMarker())
            {
                return;
            }
            Interlocked.Exchange(ref _handle, null);
            RemoveRegisteredHandle(guard);
            try { guard.Dispose(); } catch { }
            return;
        }

        if (Handles.ContainsKey(_package.LockPath))
            throw new VmLockedException("此虚拟机仍由当前进程占用，不能强制解除锁定。");

        // 没有本进程句柄时，只有成功以独占方式接管现有文件，才能证明它
        // 已经是残留锁。若仍被另一进程/另一台电脑持有，保留锁文件，避免
        // Unix 上 unlink 仍打开的文件造成两个 QEMU 同时写盘。
        guard = TryAttachExistingHandle();
        if (guard is null)
        {
            if (File.Exists(_package.LockPath))
                throw new VmLockedException("无法确认锁已失效：它仍可能由另一实例或另一台电脑持有。");
            return;
        }
        var adoptedMarker = Interlocked.Exchange(ref _markerHandle, null);
        try { adoptedMarker?.Dispose(); } catch { }
        if (!TryDeleteMarker())
        {
            return;
        }
        Interlocked.Exchange(ref _handle, null);
        RemoveRegisteredHandle(guard);
        try { guard.Dispose(); } catch { }
    }

    private bool TryDeleteMarker()
    {
        try
        {
            if (File.Exists(_package.LockPath))
                File.Delete(_package.LockPath);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void RemoveRegisteredHandle(FileStream handle)
    {
        // TryAttachExistingHandle 先登记句柄；成功删除后必须撤销登记，
        // 否则同一进程下一次 Acquire 会把已释放的 Stream 当成仍持有锁。
        ((System.Collections.Generic.ICollection<KeyValuePair<string, FileStream>>)Handles)
            .Remove(new KeyValuePair<string, FileStream>(_package.LockPath, handle));
    }
}

public sealed class VmLockedException(string message) : Exception(message);
