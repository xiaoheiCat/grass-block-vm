using GrassCore.Rpc;

namespace GrassCore.ExportImport;

/// <summary>
/// 导出目标的跨进程互斥。Core 通常是单实例，但升级/手工启动可能出现
/// 两个进程；仅靠进程内 lock 会让一个进程的失败回滚覆盖另一个进程的新档案。
/// 锁文件采用稳定路径 + 独占 FileStream，崩溃后由 DeleteOnClose 自动清理。
/// </summary>
internal sealed class ExportTargetLock : IDisposable
{
    private readonly FileStream _handle;

    private ExportTargetLock(FileStream handle) => _handle = handle;

    public static ExportTargetLock Acquire(string targetPath)
    {
        var full = VerifiedExtractionFile.ResolveStablePath(targetPath);
        var lockPath = VerifiedExtractionFile.ResolveStablePath(full + ".grass-export.lock");
        try
        {
            var handle = new FileStream(
                lockPath,
                // CreateNew 不会跟随已存在的链接；并发进程或竞态创建的
                // 普通锁文件/链接都会以 IOException 走“目标正被使用”。
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                options: FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            return new ExportTargetLock(handle);
        }
        catch (IOException)
        {
            throw new GrassCoreException("同一导出目标正被另一个 GrassCore 进程使用，请稍后重试。");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GrassCoreException("无法锁定导出目标，请检查目标目录权限后重试。");
        }
    }

    public void Dispose() => _handle.Dispose();
}
