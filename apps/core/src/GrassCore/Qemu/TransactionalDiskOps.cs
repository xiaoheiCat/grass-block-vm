using System.Diagnostics;
using System.Text;
using GrassCore.GrassVm;

namespace GrassCore.Qemu;

/// <summary>
/// qemu-img 事务规则：GrassCore 只组织参数并调用 bundled qemu-img，不实现 QCOW2 格式。
/// 目标文件先写同一文件系统的 *.grass-tmp；完成并校验成功后再原子切换。
/// 取消、qemu-img 崩溃、Core 崩溃只清理临时产物，原文件不原地破坏。
/// 应用启动时自动删除残留 .grass-tmp，不尝试断点续传/恢复长任务。
/// </summary>
public sealed class TransactionalDiskOps
{
    public const string TempSuffix = ".grass-tmp";

    private readonly string _qemuImgPath;
    private readonly Action<string>? _log;

    public TransactionalDiskOps(string qemuImgPath, Action<string>? log = null)
    {
        _qemuImgPath = qemuImgPath;
        _log = log;
    }

    /// <summary>长任务句柄：可取消。取消后只清理临时产物。</summary>
    public sealed class Operation : IDisposable
    {
        internal Process? Process;
        internal string? TempTarget;
        public bool Completed { get; internal set; }
        public void Cancel()
        {
            try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 已退出 */ }
        }
        public void Dispose() => Process?.Dispose();
    }

    /// <summary>
    /// 生成一个新的稀疏 QCOW2（Grass Block VM 自建磁盘统一稀疏 QCOW2；"稀疏/预分配"不暴露给用户）。
    /// </summary>
    public async Task CreateSparseQcow2Async(string targetPath, long sizeBytes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targetPath)!);
        var tmp = targetPath + TempSuffix;
        DeleteIfExists(tmp);
        using var op = Run(["create", "-f", "qcow2", tmp, sizeBytes.ToString()], tmp);
        await WaitForAsync(op, ct);
        VerifyImage(tmp, "qcow2");
        File.Move(tmp, targetPath, overwrite: false);
        op.Completed = true;
    }

    /// <summary>扩容：只增大虚拟磁盘容器，不自动扩 Guest 分区/文件系统。同样走临时文件 + 原子替换。</summary>
    public async Task ResizeQcow2Async(string currentPath, long newSizeBytes, CancellationToken ct = default)
    {
        if (!File.Exists(currentPath)) throw new FileNotFoundException("磁盘文件不存在。", currentPath);
        // 复制到临时文件再 resize，避免 resize 中断损坏原文件
        var tmp = currentPath + TempSuffix;
        DeleteIfExists(tmp);
        File.Copy(currentPath, tmp, overwrite: true);
        using var op = Run(["resize", tmp, newSizeBytes.ToString()], tmp);
        await WaitForAsync(op, ct);
        VerifyImage(tmp, "qcow2");
        File.Move(tmp, currentPath, overwrite: true);
        op.Completed = true;
    }

    /// <summary>格式转换（导入 VMDK/RAW → QCOW2）。转换前调用方负责空间预估。</summary>
    public Task ConvertToQcow2Async(string sourcePath, string targetPath, CancellationToken ct = default) =>
        ConvertAsync(sourcePath, targetPath, "qcow2", ct);

    /// <summary>通用格式转换（导出 OVF 用 VMDK 流式格式等）。事务规则与 QCOW2 相同。</summary>
    public async Task ConvertAsync(string sourcePath, string targetPath, string format, CancellationToken ct = default)
    {
        var tmp = targetPath + TempSuffix;
        DeleteIfExists(tmp);
        using var op = Run(["convert", "-O", format, sourcePath, tmp], tmp);
        await WaitForAsync(op, ct);
        VerifyImage(tmp, format);
        File.Move(tmp, targetPath, overwrite: false);
        op.Completed = true;
    }

    /// <summary>
    /// 创建指向 backing 文件的外部 overlay（链接克隆/快照链用）。
    /// backing 使用绝对路径引用；失败的产物照旧走 .grass-tmp 清理。
    /// </summary>
    public void CreateOverlay(string backingFile, string overlayPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        var tmp = overlayPath + TempSuffix;
        DeleteIfExists(tmp);
        using var op = Run(["create", "-f", "qcow2", "-b", backingFile, "-F", "qcow2", tmp], tmp);
        WaitForAsync(op, CancellationToken.None).GetAwaiter().GetResult();
        VerifyImage(tmp, "qcow2");
        File.Move(tmp, overlayPath, overwrite: false);
        op.Completed = true;
    }

    internal Operation Run(string[] arguments, string tempTarget)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _qemuImgPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        _log?.Invoke($"qemu-img {string.Join(' ', arguments)}");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 qemu-img。");
        return new Operation { Process = p, TempTarget = tempTarget };
    }

    internal static async Task WaitForAsync(Operation op, CancellationToken ct)
    {
        // 取消 = Kill qemu-img（整棵进程树），等待退出后只清理临时产物，原文件不动
        using var reg = ct.Register(() => op.Cancel());
        // stdout/stderr 并行排水：等待退出期间子进程的输出不会塞满管道造成死锁
        var errTask = op.Process!.StandardError.ReadToEndAsync(CancellationToken.None);
        var outTask = op.Process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await op.Process.WaitForExitAsync(CancellationToken.None);
        var err = (await errTask).Trim();
        _ = await outTask;
        if (ct.IsCancellationRequested)
        {
            CleanupTemp(op);
            throw new OperationCanceledException("磁盘操作已取消；原文件未被修改。", ct);
        }
        if (op.Process.ExitCode != 0)
        {
            CleanupTemp(op);
            throw new QemuImgException($"qemu-img 失败（退出码 {op.Process.ExitCode}）：{err}");
        }
    }

    private static void VerifyImage(string path, string format)
    {
        // 校验：产物头部魔数必须与目标格式一致。完整打开校验交给 QEMU 启动时的独占打开。
        var header = new byte[20];
        using var fs = File.OpenRead(path);
        var read = fs.Read(header, 0, header.Length);
        var ok = format.ToLowerInvariant() switch
        {
            "qcow2" or "qcow" => read >= 4 && header[0] == 'Q' && header[1] == 'F' && header[2] == 'I' && header[3] == 0xfb,
            // VMDK：稀疏头魔数 "KDMV"，或文本描述文件开头 "# Disk DescriptorFile"
            "vmdk" => read >= 4 && ((header[0] == 'K' && header[1] == 'D' && header[2] == 'M' && header[3] == 'V')
                      || Encoding.UTF8.GetString(header, 0, read).StartsWith("# Disk Des", StringComparison.Ordinal)),
            // RAW 等无魔数格式：至少有真实数据（读满 4 字节；空文件视为失败产物）
            _ => read >= 4,
        };
        if (!ok)
            throw new QemuImgException($"校验失败：产物不是有效的 {format} 文件，已丢弃临时文件。");
    }

    private static void CleanupTemp(Operation op)
    {
        if (op.TempTarget is not null) DeleteIfExists(op.TempTarget);
    }

    private static void DeleteIfExists(string p)
    {
        if (File.Exists(p)) File.Delete(p);
    }
}

public sealed class QemuImgException(string message) : Exception(message);

/// <summary>
/// 启动预检（22.1 启动 VM 第 1–2 步的纯逻辑部分）：
/// 资源存在性 / vm.lock / WHPX。任何一层失败都阻止启动并给人类可读错误。
/// </summary>
public sealed class StartupPreflight
{
    public sealed record Problem(string UserMessage, bool Fatal);

    public static List<Problem> Check(GrassVmPackage package, VmConfigView config)
    {
        var problems = new List<Problem>();
        if (File.Exists(package.LockPath))
        {
            problems.Add(new Problem(
                "此虚拟机已被占用（vm.lock 存在）。只有在确认它没有在其他实例或其他电脑上运行时，才能解除锁定。",
                Fatal: true));
        }
        if (!config.HasDisplayDevice)
            problems.Add(new Problem("此虚拟机没有显示设备，无法启动。", Fatal: true));
        if (config.NeedsNvram && !File.Exists(Path.Combine(package.FirmwarePath, "VARS.fd")))
        {
            problems.Add(new Problem(
                "此虚拟机的启动固件数据（NVRAM）缺失。请在设置中重建，或删除后重新创建这台虚拟机。",
                Fatal: true));
        }

        foreach (var disk in config.Disks)
        {
            var resolved = GrassVm.PathPolicy.Resolve(package, disk.Path);
            if (!File.Exists(resolved))
            {
                problems.Add(new Problem(
                    $"无法使用 {disk.DisplayName}：找不到它的磁盘文件。启动前需要重新定位该文件（成功后新位置将被永久保存）。",
                    Fatal: true));
            }
        }
        foreach (var cd in config.Cds.Where(c => c.IsoPath is not null))
        {
            var resolved = GrassVm.PathPolicy.Resolve(package, cd.IsoPath!);
            if (!File.Exists(resolved))
                problems.Add(new Problem(
                    $"无法使用 {cd.DisplayName}：找不到安装镜像。请重新选择镜像或弹出光盘。",
                    Fatal: true));
        }
        return problems;
    }
}

/// <summary>预检用的配置视图（避免 Qemu 层直接依赖 Config 命名空间的循环）。</summary>
public sealed record VmConfigView(
    bool HasDisplayDevice,
    bool NeedsNvram,
    IReadOnlyList<VmConfigView.DiskView> Disks,
    IReadOnlyList<VmConfigView.CdView> Cds)
{
    public sealed record DiskView(string Path, string DisplayName);
    public sealed record CdView(string? IsoPath, string DisplayName);
}
