using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    public const string ActiveMarkerSuffix = ".active";

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
        internal string? ActiveMarkerPath;
        public bool Completed { get; internal set; }
        /// <summary>子进程 stdout（WaitForAsync 排水后填充；info 类命令用）。</summary>
        public string Stdout { get; internal set; } = "";
        public void Cancel()
        {
            try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* 已退出 */ }
        }
        public void Dispose()
        {
            Process?.Dispose();
            if (ActiveMarkerPath is { } marker)
            {
                try { File.Delete(marker); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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

    /// <summary>
    /// 带源格式锁定的转换（导入用）：调用方从信封声明里解析出 qemu-img 格式名
    /// 后必须走这里显式 -f。默认重载不带 -f = qemu-img 自动探测源格式——
    /// 恶意镜像"声明 VMDK、实为带宿主 backing 的 qcow2"正是靠探测蒙混过关。
    /// </summary>
    public Task ConvertToQcow2Async(string sourcePath, string targetPath, string? sourceFormat,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourceFormat)) return ConvertToQcow2Async(sourcePath, targetPath, ct);
        var tmp = targetPath + TempSuffix;
        DeleteIfExists(tmp);
        using var op = Run(["convert", "-f", sourceFormat, "-O", "qcow2", sourcePath, tmp], tmp);
        WaitForAsync(op, ct).GetAwaiter().GetResult();
        VerifyImage(tmp, "qcow2");
        File.Move(tmp, targetPath, overwrite: false);
        op.Completed = true;
        return Task.CompletedTask;
    }

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
        => CreateOverlay(backingFile, overlayPath, relativeBacking: false);

    /// <param name="relativeBacking">
    /// true = backing 以 overlay 自身位置的相对路径写入（QEMU 按此解析）。
    /// 快照链必须用相对引用：包整体复制/导出导入到别的机器后链依然完整。
    /// 链接克隆保持绝对引用（backing 在另一个包里，本就不可迁移）。
    /// </param>
    /// <param name="deferBacking">
    /// true = 暂存模式：backing 尚不存在（快照冻结的崩溃安全顺序——overlay 先生成、
    /// 工作盘稍后才改名成 backing）。用 -u 跳过打开 backing（真实 qemu-img 会拒绝打开
    /// 不存在的 backing）+ 显式虚拟尺寸（-u 必须给 size）。
    /// </param>
    public void CreateOverlay(string backingFile, string overlayPath, bool relativeBacking,
        long? deferBackingVirtualSize = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        var backing = relativeBacking
            ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(overlayPath))!, Path.GetFullPath(backingFile))
            : backingFile;
        var tmp = overlayPath + TempSuffix;
        DeleteIfExists(tmp);
        List<string> args;
        if (deferBackingVirtualSize is { } size)
        {
            args = ["create", "-f", "qcow2", "-u", "-b", backing, "-F", "qcow2",
                "-o", $"size={size}", tmp];
        }
        else
        {
            args = ["create", "-f", "qcow2", "-b", backing, "-F", "qcow2", tmp];
        }
        using var op = Run([.. args], tmp);
        WaitForAsync(op, CancellationToken.None).GetAwaiter().GetResult();
        VerifyImage(tmp, "qcow2");
        File.Move(tmp, overlayPath, overwrite: false);
        op.Completed = true;
    }

    /// <summary>
    /// 把 overlay 的数据合并进它的 backing（qemu-img commit；用于删除链中快照：
    /// 被删层先并入其 backing，其后代 overlay 才能安全 rebase 到祖先）。
    ///
    /// commit 是唯一需要【原地】改写已有文件的磁盘操作（backing 是全链基座）。
    /// 执行前在同一包的 temp/ 中保留父盘副本和持久 journal；进程崩溃后由启动
    /// 修复入口按 journal 阶段恢复或清理。副本可能暂时占用接近父盘大小，
    /// 这是为避免 qemu-img commit 中断后永久丢失父盘数据付出的明确空间代价。
    /// </summary>
    public void CommitOverlay(string overlayFile)
    {
        // 提交前必须知道确切的 backing；把 qemu-img info 失败折叠成“无 backing”
        // 会跳过恢复 journal，随后原地 commit 可能把错误层改坏。
        var parent = QueryBackingFileStrict(overlayFile);
        var package = FindOwningPackage(overlayFile);
        var journal = package is null ? null : Path.Combine(package.TempPath, "commit-journal.json");
        var backup = parent is null ? null : parent + CommitTempSuffix;
        if (journal is not null && backup is not null)
        {
            if (GrassVmPackage.IsReparsePointOrLink(journal) || GrassVmPackage.IsReparsePointOrLink(backup))
                throw new QemuImgException("磁盘提交事务文件包含不受支持的符号链接或目录联接。");
            Directory.CreateDirectory(package!.TempPath);
            AtomicFile.WriteJsonValidated(journal, JsonSerializer.Serialize(new CommitJournal(parent!, backup, "prepared")));
            File.Copy(parent!, backup, overwrite: false);
            AtomicFile.WriteJsonValidated(journal, JsonSerializer.Serialize(new CommitJournal(parent!, backup, "copied")));
        }
        // tempTarget 只在失败清理时使用：commit 的目标就是真文件，绝不能被删，
        // 传一个不会被创建的哨兵路径（失败清理 File.Delete 对不存在的文件是 no-op）
        using var op = Run(["commit", "-f", "qcow2", overlayFile], overlayFile + ".commit-sentinel");
        // qemu-img 返回成功后，父盘已经包含 overlay 数据。之后的 check、journal
        // 更新或临时文件清理即使失败，也绝不能再用旧备份覆盖父盘。
        var commitCompleted = false;
        try
        {
            WaitForAsync(op, CancellationToken.None).GetAwaiter().GetResult();
            op.Completed = true;
            commitCompleted = true;
            if (journal is not null && backup is not null)
            {
                // 先持久化“已提交”再做任何校验/清理。这样即使后续步骤抛错，
                // 下次启动也只会回收副本，不会把父盘恢复成提交前的内容。
                AtomicFile.WriteJsonValidated(journal, JsonSerializer.Serialize(new CommitJournal(parent!, backup, "committed")));
            }
            if (parent is not null)
            {
                // 合并完立刻体检基座：坏了就抛，让调用方保留未改的元数据并可重试，
                // 不让损坏沿链静默传播；commit 已成功时绝不回滚父盘。
                using var chk = Run(["check", "-f", "qcow2", parent], null);
                WaitForAsync(chk, CancellationToken.None).GetAwaiter().GetResult();
                chk.Completed = true;
            }
            if (journal is not null && backup is not null)
            {
                DeleteIfExists(backup);
                DeleteIfExists(journal);
            }
        }
        catch
        {
            if (journal is not null && backup is not null)
            {
                try
                {
                    // 只有 qemu-img 尚未成功返回时才可以回滚。提交成功后的
                    // journal/backup 清理失败要留给下一次启动收尾，不能破坏新数据。
                    if (!commitCompleted && File.Exists(backup) && !GrassVmPackage.IsReparsePointOrLink(backup))
                        File.Copy(backup, parent!, overwrite: true);
                    DeleteIfExists(backup);
                    DeleteIfExists(journal);
                }
                catch { /* 持久 journal 留给下次 Core 启动收尾 */ }
            }
            throw;
        }
    }

    /// <summary>commit 副本事务的暂存后缀（保留常量：将来文件系统支持稀疏复制时可启用）。</summary>
    public const string CommitTempSuffix = ".grass-commit-tmp";

    /// <summary>
    /// 按持久化 commit journal 收尾中断的副本提交。只处理 journal 指定且位于
    /// 当前包内的父盘/备份；没有可验证 journal 时不信任孤立的 *.grass-commit-tmp。
    /// </summary>
    public static void FinishCommitTempFiles(GrassVmPackage package)
    {
        var journal = Path.Combine(package.TempPath, "commit-journal.json");
        if (!File.Exists(journal) || GrassVmPackage.IsReparsePointOrLink(journal)) return;
        try
        {
            var tx = JsonSerializer.Deserialize<CommitJournal>(File.ReadAllText(journal));
            if (tx is null || string.IsNullOrWhiteSpace(tx.Parent) || string.IsNullOrWhiteSpace(tx.Backup)) return;
            var root = Path.GetFullPath(package.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(tx.Parent).StartsWith(root, cmp) || !Path.GetFullPath(tx.Backup).StartsWith(root, cmp)) return;
            if (tx.Phase != "committed" && File.Exists(tx.Backup)
                && !GrassVmPackage.IsReparsePointOrLink(tx.Backup))
                File.Copy(tx.Backup, tx.Parent, overwrite: true);
            DeleteIfExists(tx.Backup);
            DeleteIfExists(journal);
        }
        catch { /* 保留 journal，待下一次启动或人工诊断 */ }
    }

    private sealed record CommitJournal(string Parent, string Backup, string Phase);

    private static GrassVmPackage? FindOwningPackage(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
             current is not null; current = current.Parent)
        {
            if (GrassVmPackage.IsGrassVmDirectory(current.FullName))
            {
                try { return new GrassVmPackage(current.FullName); }
                catch (ArgumentException) { return null; }
            }
        }
        return null;
    }

    /// <summary>
    /// 把 overlay 的 backing 指针改到新的 backing（qemu-img rebase；不迁移数据，
    /// 只改指针——数据已在 commit 阶段归并）。backing 为 null 表示断开引用。
    /// </summary>
    /// <param name="unsafeMode">
    /// 追加 -u（unsafe rebase）：不打开【旧】backing、只改指针。用于快照冻结——
    /// 冻结文件刚从 disks/ 移到 snapshots/&lt;uuid&gt;/disks/，其相对 backing 引用
    /// 按旧位置解析已失效，普通 rebase 会在打开旧 backing 时失败。
    /// </param>
    public void RebaseOverlay(string overlayFile, string? newBackingFile, bool relativeBacking = false,
        bool unsafeMode = false)
    {
        var args = new List<string> { "rebase", "-f", "qcow2" };
        if (newBackingFile is null || unsafeMode)
            args.Add("-u");
        if (newBackingFile is not null)
        {
            var backing = relativeBacking
                ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(overlayFile))!, Path.GetFullPath(newBackingFile))
                : newBackingFile;
            args.AddRange(["-F", "qcow2", "-b", backing]);
        }
        args.Add(overlayFile);
        using var op = Run([.. args], overlayFile + ".rebase-sentinel");
        WaitForAsync(op, CancellationToken.None).GetAwaiter().GetResult();
        op.Completed = true;
    }

    internal Operation Run(string[] arguments, string? tempTarget)
    {
        var marker = IsTransactionalTemp(tempTarget) ? tempTarget + ActiveMarkerSuffix : null;
        if (marker is not null)
        {
            try
            {
                if (GrassVmPackage.IsReparsePointOrLink(marker))
                    throw new QemuImgException("磁盘事务活动标记包含不受支持的符号链接或目录联接。");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                // 先写 pid=0，再启动子进程：Core 在这个极窄窗口崩溃时，启动清理
                // 仍会把新鲜标记保留下来，而不是误删刚准备使用的临时文件。
                File.WriteAllText(marker, JsonSerializer.Serialize(new
                {
                    pid = 0,
                    startedAtUtc = DateTimeOffset.UtcNow,
                }));
            }
            catch (IOException) { marker = null; }
            catch (UnauthorizedAccessException) { marker = null; }
        }
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
        var startedAtUtc = DateTimeOffset.UtcNow;
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 qemu-img。");
        }
        catch
        {
            if (marker is not null) { try { File.Delete(marker); } catch { } }
            throw;
        }
        if (marker is not null)
        {
            try
            {
                File.WriteAllText(marker, JsonSerializer.Serialize(new
                {
                    pid = p.Id,
                    startedAtUtc,
                }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new Operation { Process = p, TempTarget = tempTarget, ActiveMarkerPath = marker };
    }

    private static bool IsTransactionalTemp(string? path) =>
        path is not null && (path.EndsWith(TempSuffix, StringComparison.Ordinal)
            || path.EndsWith(CommitTempSuffix, StringComparison.Ordinal));

    internal static async Task WaitForAsync(Operation op, CancellationToken ct)
    {
        // 取消 = Kill qemu-img（整棵进程树），等待退出后只清理临时产物，原文件不动
        using var reg = ct.Register(() => op.Cancel());
        // stdout/stderr 并行排水：等待退出期间子进程的输出不会塞满管道造成死锁
        var errTask = op.Process!.StandardError.ReadToEndAsync(CancellationToken.None);
        var outTask = op.Process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await op.Process.WaitForExitAsync(CancellationToken.None);
        var err = (await errTask).Trim();
        op.Stdout = await outTask;
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

    /// <summary>
    /// 查询镜像当前实际指向的 backing（qemu-img info --output=json 的
    /// full-backing-filename；相对引用按镜像自身位置解析）。无 backing / 查询失败 → null。
    /// 快照冻结用【物理事实】而不是树元数据决定 rebase 目标——元数据与物理链
    /// 分歧时（崩溃窗口、链根删除后），相信文件本身才不会把数据层旁路掉。
    /// </summary>
    public string? QueryBackingFile(string imageFile)
    {
        try { return QueryBackingFileStrict(imageFile); }
        catch { return null; } // 查询不可用：调用方退回元数据推断
    }

    /// <summary>
    /// 严格版 backing 查询：失败（文件缺失 / qemu-img 出错 / 输出不可解析）抛
    /// QemuImgException，只有【真的没有 backing】才返回 null。宽松版把"查询
    /// 失败"与"没有 backing"都折叠成 null——安全决策处（导入前拒绝带 backing
    /// 的源镜像、删除快照前判定物理依赖者）必须区分这两种情形：把失败当
    /// "没有"会在最需要保守的地方选择最激进的动作。
    /// </summary>
    public string? QueryBackingFileStrict(string imageFile)
    {
        using var op = Run(["info", "--output=json", imageFile], null);
        WaitForAsync(op, CancellationToken.None).GetAwaiter().GetResult();
        op.Completed = true;
        using var doc = JsonDocument.Parse(op.Stdout);
        var root = doc.RootElement;
        string? backing = null;
        if (root.TryGetProperty("full-backing-filename", out var full) && full.ValueKind == JsonValueKind.String)
            backing = full.GetString();
        else if (root.TryGetProperty("backing-filename", out var rel) && rel.ValueKind == JsonValueKind.String)
        {
            var r = rel.GetString()!;
            backing = Path.IsPathRooted(r)
                ? r
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imageFile))!, r));
        }
        if (backing is null) return null;
        if (!File.Exists(backing))
            throw new QemuImgException($"磁盘 backing 文件缺失：{backing}");
        return backing;
    }

    private static void DeleteIfExists(string p)
    {
        // File.Exists 对悬空符号链接返回 false；若直接把该路径交给
        // File.Copy/qemu-img，目标可能被跟随并写到包外。临时路径属于 Core
        // 自己管理的命名空间，发现任何链接都必须失败而不是尝试复用。
        if (GrassVmPackage.IsReparsePointOrLink(p))
            throw new QemuImgException("磁盘事务临时文件包含不受支持的符号链接或目录联接。");
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

    public static List<Problem> Check(GrassVmPackage package, VmConfigView config, bool lockHeld = false)
    {
        var problems = new List<Problem>();
        if (!lockHeld && package.HasLockFiles)
        {
            problems.Add(new Problem(
                "此虚拟机已被占用（vm.lock 存在）。只有在确认它没有在其他实例或其他电脑上运行时，才能解除锁定。",
                Fatal: true));
        }
        if (!config.HasDisplayDevice)
            problems.Add(new Problem("此虚拟机没有显示设备，无法启动。", Fatal: true));
        if (config.NeedsNvram)
        {
            var vars = Path.Combine(package.FirmwarePath, "VARS.fd");
            if (!File.Exists(vars))
            {
                problems.Add(new Problem(
                    GrassVmPackage.IsReparsePointOrLink(vars)
                        ? "此虚拟机的启动固件数据（NVRAM）是符号链接或目录联接，已拒绝使用。请在设置中重建。"
                        : "此虚拟机的启动固件数据（NVRAM）缺失。请在设置中重建，或删除后重新创建这台虚拟机。",
                    Fatal: true));
            }
            else
            {
                try
                {
                    var attrs = File.GetAttributes(vars);
                    if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) != 0)
                        problems.Add(new Problem("此虚拟机的启动固件数据（NVRAM）必须是普通文件，不能是符号链接或目录。", Fatal: true));
                }
                catch (IOException)
                {
                    problems.Add(new Problem("无法检查此虚拟机的启动固件数据（NVRAM），已阻止启动。", Fatal: true));
                }
                catch (UnauthorizedAccessException)
                {
                    problems.Add(new Problem("无法检查此虚拟机的启动固件数据（NVRAM），已阻止启动。", Fatal: true));
                }
            }
        }

        foreach (var disk in config.Disks)
        {
            string resolved;
            try
            {
                resolved = GrassVm.PathPolicy.Resolve(package, disk.Path);
            }
            catch (ArgumentException)
            {
                problems.Add(new Problem(
                    $"无法使用 {disk.DisplayName}：磁盘路径包含不受支持的符号链接或目录联接。请重新定位该文件后重试。",
                    Fatal: true));
                continue;
            }
            if (!File.Exists(resolved))
            {
                problems.Add(new Problem(
                    // 措辞必须可行动：当前没有"重定位磁盘文件"的 UI 入口，指引
                    // 用户去恢复文件或重建，别承诺一个不存在的流程
                    $"无法使用 {disk.DisplayName}：找不到它的磁盘文件（{disk.Path}）。请恢复该文件后重试，或删除后重建这台虚拟机。",
                    Fatal: true));
            }
            else if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(Path.GetExtension(resolved), ".qcow2", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new Problem(
                    $"无法使用 {disk.DisplayName}：磁盘文件必须是普通的 QCOW2 文件。请重新定位该文件后重试。",
                    Fatal: true));
            }
        }
        foreach (var cd in config.Cds.Where(c => c.IsoPath is not null))
        {
            string resolved;
            try
            {
                resolved = GrassVm.PathPolicy.Resolve(package, cd.IsoPath!);
            }
            catch (ArgumentException)
            {
                problems.Add(new Problem(
                    $"{cd.DisplayName} 引用的安装镜像路径包含不受支持的符号链接或目录联接，请重新选择 ISO 文件。",
                    Fatal: true));
                continue;
            }
            if (!File.Exists(resolved))
                problems.Add(new Problem(
                    // 非致命：安装镜像被用户清理（Downloads/临时目录）是常态，空
                    // 光驱完全可以启动。设为 Fatal = 停机状态下永远无法开机，而
                    // 设置页没有换介质入口——把"删掉的 ISO"变成"删掉的虚拟机"。
                    // 运行后可从显示器窗口更换介质
                    $"{cd.DisplayName} 引用的安装镜像已不存在，将以空光驱启动。启动后可在显示器窗口更换介质。",
                    Fatal: false));
            else if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(Path.GetExtension(resolved), ".iso", StringComparison.OrdinalIgnoreCase))
                problems.Add(new Problem(
                    $"{cd.DisplayName} 引用的文件不是普通 ISO 镜像，请重新选择 ISO 文件。",
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
    public sealed record DiskView(string Path, string DisplayName, bool External = false);
    public sealed record CdView(string? IsoPath, string DisplayName);
}
