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
public sealed class VmLock
{
    private readonly GrassVmPackage _package;

    public VmLock(GrassVmPackage package) => _package = package;

    public bool IsLocked => File.Exists(_package.LockPath);

    /// <summary>
    /// 获取锁。已存在 vm.lock 直接阻止并引导手动解除锁——本机崩溃后也不自动接管，
    /// 因为包可能在 NAS/同步盘上被另一台电脑持有。
    /// </summary>
    public void Acquire()
    {
        if (IsLocked)
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        Directory.CreateDirectory(_package.Path);
        // 排他创建（原子）：检查-再-写入在并发双击下会让两个调用都通过检查——
        // 两个 QEMU 同写一张盘正是 vm.lock 要防的事故。CreateNew 在文件已存在时抛 IOException。
        try
        {
            using var fs = new FileStream(_package.LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.WriteByte(0);
        }
        catch (IOException) when (File.Exists(_package.LockPath))
        {
            throw new VmLockedException($"此虚拟机已被占用（存在 vm.lock）。只有在确认它没有在其他 Grass Block VM 实例或其他电脑上运行时，才能解除锁定。");
        }
    }

    /// <summary>正常释放 VM 时删除 vm.lock。仅当锁确实存在且由本次会话持有时调用。</summary>
    public void Release()
    {
        if (File.Exists(_package.LockPath)) File.Delete(_package.LockPath);
    }

    /// <summary>
    /// 用户手动"解除锁定"。调用方（UI/RPC 层）必须先展示风险确认：
    /// 错误解除并同时启动同一虚拟机可能导致虚拟磁盘损坏或数据丢失。
    /// </summary>
    public void ForceUnlockByUser() => Release();
}

public sealed class VmLockedException(string message) : Exception(message);
