using System.Diagnostics;
using System.Text.Json;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Library;
using GrassCore.Profiles;
using GrassCore.Qemu;
using GrassCore.Rpc;
using GrassCore.Snapshots;

namespace GrassCore.Rpc;

/// <summary>
/// GrassCore API 表面。GrassCore 是 .grassvm 从创建到销毁的唯一写入者；
/// Electron UI 永远不直接写 VM 包体。所有方法返回可 JSON 化的 DTO。
/// </summary>
public sealed class GrassCoreService
{
    private readonly HostDb _db;
    private readonly string _qemuSystemPath;
    private readonly string _qemuImgPath;
    private readonly string _ovmfDir;
    private readonly string _bundledQemuMajor;
    private readonly IQemuProcessLauncher _launcher;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunningVm> _running =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否有运行中的 VM（Core 空闲退出判定用）。</summary>
    public bool HasRunningVms => !_running.IsEmpty;

    /// <summary>本机标识（宿主级持久，首次生成）：会话归属判定，防别机会话被本机"收尾"。</summary>
    private string MachineId
    {
        get
        {
            var id = _db.GetPreference("machineId");
            if (id is null)
            {
                id = Guid.NewGuid().ToString("N");
                _db.SetPreference("machineId", id);
            }
            return id;
        }
    }

    /// <summary>每包一把信号量：生命周期操作（启动/恢复/挂起/电源）按包串行——并发 RPC 分发下双击 start 不会再赛跑。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pkgGates =
        new(StringComparer.OrdinalIgnoreCase);

    private T WithPackageGate<T>(string packagePath, Func<T> action)
    {
        var gate = _pkgGates.GetOrAdd(packagePath,
            _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try { return action(); }
        finally { gate.Release(); }
    }

    private void WithPackageGate(string packagePath, Action action)
    {
        var gate = _pkgGates.GetOrAdd(packagePath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try { action(); }
        finally { gate.Release(); }
    }

    public GrassCoreService(HostDb db, string qemuSystemPath, string qemuImgPath, string ovmfDir,
        string bundledQemuMajor, IQemuProcessLauncher? launcher = null,
        Func<string, IQmpTransport?>? qmpTransportFactory = null)
    {
        _db = db;
        _qemuSystemPath = qemuSystemPath;
        _qemuImgPath = qemuImgPath;
        _ovmfDir = ovmfDir;
        _bundledQemuMajor = bundledQemuMajor;
        _launcher = launcher ?? new QemuProcessLauncher(qemuSystemPath);
        _qmpTransportFactory = qmpTransportFactory ?? DefaultQmpTransport;
    }

    /// <summary>QMP 传输：Windows Named Pipe；开发机（非 Windows）上电源动作不可用（需实机联调）。</summary>
    private static IQmpTransport? DefaultQmpTransport(string pipeName) =>
        OperatingSystem.IsWindows() ? new WindowsNamedPipeTransport(pipeName) : null;

    private readonly Func<string, IQmpTransport?> _qmpTransportFactory;

    public sealed record RunningVm(string PackagePath, RuntimeSession Session, Process Process)
    {
        public QmpClient? Qmp { get; set; }
        /// <summary>已发送 ACPI 电源按钮请求（正常关机的唯一可信信号；quit/崩溃不算）。</summary>
        public volatile bool AcpiShutdownRequested;
    }

    /// <summary>当前 Library Root（宿主只有一个；更改只影响之后创建/导入）。</summary>
    public string? LibraryRoot => _db.LibraryRoot;

    /// <summary>设置 Library Root。旧目录 VM 不迁移、不再出现在主界面。</summary>
    public object SetLibraryRoot(string newRoot)
    {
        if (!Directory.Exists(newRoot)) Directory.CreateDirectory(newRoot);
        _db.ChangeLibraryRoot(newRoot);
        return new { libraryRoot = _db.LibraryRoot };
    }

    // ---------- Library ----------

    public ScanLibraryResult ScanLibrary()
    {
        var root = _db.LibraryRoot ?? throw new InvalidOperationException("尚未设置虚拟机存档位置。");
        var autostart = _db.GetAutostartList().ToDictionary(a => a.VmPath, a => a.Enabled, StringComparer.OrdinalIgnoreCase);
        var vms = GrassVmPackage.ScanLibraryRoot(root)
            .Select(p =>
            {
                string osProfile = "other";
                int cpu = 0, mem = 0;
                var state = "stopped";
                try
                {
                    var config = new ConfigStore(p).Load();
                    osProfile = config.OsProfileId;
                    cpu = config.CpuCores;
                    mem = config.MemoryMiB;
                }
                catch { /* 无法读取的包照常列出（发现≠已验证），打开时再给完整诊断 */ }
                try
                {
                    if (VmState.Load(p).SuspendedStatePath is not null) state = "suspended";
                }
                catch { /* state.json 损坏按 stopped 列出，打开时完整性检查给诊断 */ }
                if (_running.ContainsKey(p.Path)) state = "running";
                else if (File.Exists(p.LockPath) && File.Exists(p.SessionPath)) state = "running"; // 异常残留：接管中
                return new VmSummaryDto(
                    Path: p.Path, Name: p.Name, OsProfileId: osProfile, State: state,
                    CpuCores: cpu, MemoryMiB: mem,
                    Locked: File.Exists(p.LockPath),
                    HasAutostart: autostart.GetValueOrDefault(p.Path, false));
            })
            .ToList();
        return new ScanLibraryResult(root, vms);
    }

    public sealed record ScanLibraryResult(string LibraryRoot, List<VmSummaryDto> Vms);

    public sealed record VmSummaryDto(
        string Path, string Name, string OsProfileId, string State,
        int CpuCores, int MemoryMiB, bool Locked, bool HasAutostart);

    public object GetProfiles() => OsProfileLibrary.List().Select(p => new
    {
        id = p.Id, name = p.DisplayName, family = p.Family, verified = p.Verified,
        cpu = p.RecommendedCpuCores, memoryMiB = p.RecommendedMemoryMiB, diskGiB = p.RecommendedDiskBytes / 1024 / 1024 / 1024,
    });

    // ---------- 创建（向导 → Core 全权落盘）----------

    public object CreateVm(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!;
        var profileId = args.GetProperty("profileId").GetString()!;
        var diskGiB = args.GetProperty("diskGiB").GetInt64();
        // 名称同时是包目录名与 QEMU -name 值：文件系统非法字符或逗号（QEMU 选项解析分隔符）都拒绝
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(','))
            throw new GrassCoreException(
                $"虚拟机名称不合法：{name}（不能为空，不能包含文件系统不允许的字符或逗号）");
        var isoPath = args.TryGetProperty("isoPath", out var iso) && iso.ValueKind != JsonValueKind.Null ? iso.GetString() : null;

        // 向导可调参数：cpuCores / memoryMiB（缺省 = Profile 推荐；钳制到安全范围）
        var root = _db.LibraryRoot ?? throw new InvalidOperationException("尚未设置虚拟机存档位置。");
        var profile = OsProfileLibrary.ById(profileId);
        var pkg = GrassVmPackage.CreateNew(root, name);
        var config = OsProfileLibrary.CreateDefaultConfig(profileId, name);
        if (args.TryGetProperty("cpuCores", out var cpuEl) && cpuEl.ValueKind == JsonValueKind.Number)
            config.CpuCores = Math.Clamp(cpuEl.GetInt32(), 1, Math.Max(1, Environment.ProcessorCount));
        if (args.TryGetProperty("memoryMiB", out var memEl) && memEl.ValueKind == JsonValueKind.Number)
            config.MemoryMiB = Math.Clamp(memEl.GetInt32(), 512, Math.Max(512, (int)(GetTotalHostMemoryMiB() / 2)));

        // 向导强制的"至少一个可启动来源"（普通安装场景），底层模型不被限制
        var order = DeviceNamer.NextCreatedOrder(config);
        var diskPath = Path.Combine(pkg.DisksPath, "system.qcow2");
        config.Devices.Add(new DiskDevice
        {
            Path = PathPolicy.NormalizeReference(pkg, diskPath),
            SizeBytes = diskGiB * 1024 * 1024 * 1024,
            CreatedOrder = order,
        });
        if (isoPath is not null)
        {
            config.Devices.Add(new CdromDevice
            {
                IsoPath = PathPolicy.NormalizeReference(pkg, isoPath),
                CreatedOrder = DeviceNamer.NextCreatedOrder(config),
            });
        }
        if (profile.Firmware == FirmwareKind.Uefi)
        {
            // NVRAM 变量卷从模板复制，每 VM 独立
            Directory.CreateDirectory(pkg.FirmwarePath);
            var template = Path.Combine(_ovmfDir, "OVMF_VARS.fd");
            if (File.Exists(template)) File.Copy(template, Path.Combine(pkg.FirmwarePath, "VARS.fd"));
        }

        // 稀疏 QCOW2 创建（事务式）
        var diskOps = new TransactionalDiskOps(_qemuImgPath);
        diskOps.CreateSparseQcow2Async(diskPath, diskGiB * 1024 * 1024 * 1024).GetAwaiter().GetResult();

        new ConfigStore(pkg).Save(config);
        _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        // "创建后立即启动"（向导默认开）：由 Core 一并完成，UI 只负责刷新
        if (args.TryGetProperty("startAfterCreate", out var sac) && sac.ValueKind == JsonValueKind.True)
        {
            StartVm(pkg.Path);
            return new CreateVmResult(pkg.Path, pkg.Name) { Started = true };
        }
        return new CreateVmResult(pkg.Path, pkg.Name);
    }

    public sealed record CreateVmResult(string Path, string Name)
    {
        public bool Started { get; init; }
    }

    // ---------- 启动 / 电源 ----------

    public object StartVm(string packagePath) => WithPackageGate(packagePath, () => StartVmCore(packagePath));

    private object StartVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).LoadAndUpgrade();
        var state = VmState.Load(pkg);

        // 挂起状态：直接启动被拒绝（QEMU 必须 -incoming 才能回到保存的瞬间）
        if (state.SuspendedStatePath is not null)
            throw new GrassCoreException(
                "此虚拟机已挂起。请使用“恢复”回到挂起时的状态；如不需要保存的状态，请先恢复后再正常关机。");

        // 已在运行（本 Core 实例）：幂等拒绝，不触碰任何状态
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("此虚拟机已经在运行。");

        // 修复"快照换入中断"残留（幂等）：工作盘缺失但暂存 overlay 完好 → 完成换入
        SnapshotService.RepairStagedOverlays(pkg);

        // 预检：资源、vm.lock、显示设备、NVRAM（WHPX 检测在 Windows 主机上执行）
        var view = ToConfigView(config);
        var problems = StartupPreflight.Check(pkg, view);
        if (problems.Any(p => p.Fatal))
            throw new GrassCoreException(string.Join("\n", problems.Where(p => p.Fatal).Select(p => p.UserMessage)));

        // WHPX：只使用硬件虚拟化，初始化失败即阻止启动，绝不静默回退 TCG
        if (OperatingSystem.IsWindows())
        {
            var whpx = WhpxCapability.CheckWindows();
            if (!whpx.Available)
                throw new GrassCoreException(
                    $"此电脑无法使用硬件虚拟化（WHPX）：{whpx.UserGuidance}");
        }

        var @lock = new VmLock(pkg);
        @lock.Acquire();
        bool lockAcquired = true; // 预检通过后到这里才拥有锁；此前抛出都不属于本调用
        Process? proc = null;
        try
        {
            // 升级保护：跨 QEMU major 首启 → 备份元数据 + 隐藏保护快照（由快照服务落盘）
            if (UpgradeProtection.NeedsProtection(state.LastQemuMajor, _bundledQemuMajor))
            {
                var plan = UpgradeProtection.CreatePlan(pkg, state.LastQemuMajor!, _bundledQemuMajor);
                UpgradeProtection.BackupMetadata(pkg, plan.MetadataBackupDir);
                SnapshotService.Create(pkg, config, name: "升级保护快照", isUpgradeProtection: true,
                    diskOps: new Qemu.TransactionalDiskOps(_qemuImgPath));
            }

            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var cmd = new QemuCommandBuilder(config, _ovmfDir).Build(pkg.Path, sessionId);
            var session = new RuntimeSession
            {
                SessionId = sessionId,
                QmpPipe = cmd.QmpPipeName,
                StartedAt = DateTimeOffset.UtcNow,
                QemuMajorAtStart = _bundledQemuMajor,
                MachineId = MachineId,
            };
            Directory.CreateDirectory(pkg.RuntimePath);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            proc = _launcher.Start(cmd);
            session.QemuPid = proc.Id;
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            // 注意：升级保护快照已在磁盘上推进了 CurrentSnapshotUuid——必须重读，
            // 不能用过期实例覆盖（否则位置标记回退，后续自动删除会误判"位置不在这"，
            // 跳过工作 overlay 的 rebase → 链断）。只 patch 本函数拥有的字段。
            var fresh = VmState.Load(pkg);
            fresh.LastQemuMajor = _bundledQemuMajor;
            fresh.LastStartedAt = DateTimeOffset.UtcNow;
            fresh.SuspendedStatePath = null;
            fresh.Save(pkg);

            var vm = new RunningVm(pkg.Path, session, proc);
            vm.Qmp = TryConnectQmp(session.QmpPipe);
            _running[pkg.Path] = vm;
            WatchQemuProcess(vm);
            return new { qemuPid = proc.Id, qmpPipe = cmd.QmpPipeName, sessionId };
        }
        catch
        {
            // 启动失败回滚：只清理【本次调用】获得的资源（锁/进程/runtime）。
            // 若失败发生在取锁之前（预检拒绝），不触碰磁盘上的任何状态——那是别人的锁。
            if (lockAcquired) RollbackStart(pkg, proc);
            throw;
        }
    }

    /// <summary>启动失败回滚（QEMU 未成功进入可控状态）。调用方必须持有 vm.lock。</summary>
    private static void RollbackStart(GrassVmPackage pkg, Process? proc)
    {
        try
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch { /* 尽力而为 */ }
        try
        {
            if (Directory.Exists(pkg.RuntimePath))
                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
            new VmLock(pkg).Release();
        }
        catch { /* 下次启动预检会给残留锁诊断 */ }
    }

    /// <summary>挂起恢复：-incoming 从保存状态回到挂起瞬间。只保证相同宿主 CPU + 同一 QEMU major。</summary>
    public object ResumeVm(string packagePath) => WithPackageGate(packagePath, () => ResumeVmCore(packagePath));

    private object ResumeVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).LoadAndUpgrade();
        var state = VmState.Load(pkg);
        if (state.SuspendedStatePath is null)
            throw new GrassCoreException("此虚拟机没有保存的挂起状态。");
        var suspendFile = PathPolicy.Resolve(pkg, state.SuspendedStatePath);
        if (!File.Exists(suspendFile))
            throw new GrassCoreException("找不到挂起时保存的状态文件。该状态已丢失，只能重新启动虚拟机。");

        // 指纹校验：不同宿主 CPU / 不同 QEMU major 的挂起状态不保证可恢复
        var fingerprint = CurrentHostFingerprint();
        if (state.SuspendFingerprint is not null && state.SuspendFingerprint != fingerprint)
            throw new GrassCoreException(
                "此挂起状态是在不同的硬件或 QEMU 版本上保存的，无法保证正确恢复。建议重新启动虚拟机。");

        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("此虚拟机已经在运行。");

        // 与 StartVm 同一套预检（含换入中断修复）：挂起恢复同样要磁盘/固件在位，
        // 否则 QEMU 秒退、用户只看到"恢复没反应"
        SnapshotService.RepairStagedOverlays(pkg);
        var view = ToConfigView(config);
        var problems = StartupPreflight.Check(pkg, view);
        if (problems.Any(p => p.Fatal))
            throw new GrassCoreException(string.Join("\n", problems.Where(p => p.Fatal).Select(p => p.UserMessage)));
        if (OperatingSystem.IsWindows())
        {
            var whpx = WhpxCapability.CheckWindows();
            if (!whpx.Available)
                throw new GrassCoreException($"此电脑无法使用硬件虚拟化（WHPX）：{whpx.UserGuidance}");
        }

        var @lock = new VmLock(pkg);
        @lock.Acquire();
        bool lockAcquired = true;
        Process? proc = null;
        bool registered = false;
        try
        {
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var cmd = new QemuCommandBuilder(config, _ovmfDir).Build(pkg.Path, sessionId, suspendFile);
            var session = new RuntimeSession
            {
                SessionId = sessionId,
                QmpPipe = cmd.QmpPipeName,
                StartedAt = DateTimeOffset.UtcNow,
                QemuMajorAtStart = _bundledQemuMajor,
                MachineId = MachineId,
            };
            Directory.CreateDirectory(pkg.RuntimePath);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());
            proc = _launcher.Start(cmd);
            session.QemuPid = proc.Id;
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            var vm = new RunningVm(pkg.Path, session, proc);
            vm.Qmp = TryConnectQmp(session.QmpPipe);
            _running[pkg.Path] = vm;
            registered = true;
            WatchQemuProcess(vm);

            // 恢复确认：-incoming 迁移完成（query-status=running）后才清除挂起标记。
            // 一次失败的恢复尝试不能毁掉整个保存的会话（保守：失败时标记保留，可重试）。
            _ = Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                    while (DateTime.UtcNow < deadline && !vm.Process.HasExited)
                    {
                        var q = vm.Qmp ?? TryConnectQmp(session.QmpPipe);
                        if (q is not null)
                        {
                            vm.Qmp = q;
                            if (await q.VmStatusAsync() == "running")
                            {
                                var st = VmState.Load(pkg);
                                st.SuspendedStatePath = null;
                                st.SuspendFingerprint = null;
                                st.Save(pkg);
                                try { File.Delete(suspendFile); } catch { /* 空间回收失败不致命 */ }
                                return;
                            }
                        }
                        await Task.Delay(500);
                    }
                }
                catch
                {
                    // 确认失败：保留挂起标记（宁可保守；下次正常关机后标记自然失效）
                }
            });
            return new { qemuPid = proc.Id, resumed = true };
        }
        catch
        {
            // 失败回滚与 StartVm 同规则：只清理本次调用获得的资源；
            // 仅在确实注册进 _running 后才移除（别人的运行实例绝不动）
            if (registered) _running.TryRemove(pkg.Path, out _);
            if (lockAcquired) RollbackStart(pkg, proc);
            throw;
        }
    }

    /// <summary>
    /// QEMU 进程退出监视：ACPI 关机/quit/客户机内关机最终都表现为进程退出。
    /// 在这里做干净关机记账：清空 runtime/、释放 vm.lock、
    /// 升级保护快照满 24 小时后自动删除（§22.3）。
    /// </summary>
    private void WatchQemuProcess(RunningVm vm)
    {
        vm.Process.EnableRaisingEvents = true;
        vm.Process.Exited += (_, _) =>
        {
            try
            {
                if (!_running.Remove(vm.PackagePath, out var removed)) return; // 已被挂起/强制路径处理
                removed.Qmp?.Dispose();
                var pkg = new GrassVmPackage(vm.PackagePath);
                // 正常关机 = 我们发出过 ACPI 电源按钮请求后的退出。
                // 强制关机（quit）/崩溃/宿主断电都不算——升级保护快照只在真正正常关机后才开始 24h 计时。
                if (vm.AcpiShutdownRequested)
                    TryDeleteExpiredUpgradeProtection(pkg);
                // 正常退出路径：清空 runtime/ 并删除 vm.lock
                if (Directory.Exists(pkg.RuntimePath))
                    foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                new VmLock(pkg).Release();
            }
            catch
            {
                // 监视失败不影响 QEMU 已退出的事实；下次启动预检会给出残留锁诊断
            }
        };
    }

    /// <summary>
    /// 升级保护快照：跨版本首次正常关机后保留至少 24 小时，之后自动删除。
    /// 有链接克隆依赖时跳过（交互删除路径会先向用户列明影响）。
    /// </summary>
    private void TryDeleteExpiredUpgradeProtection(GrassVmPackage pkg)
    {
        try
        {
            var tree = SnapshotService.LoadTree(pkg);
            var expired = tree.All.Where(s =>
                SnapshotPlanner.ShouldDeleteUpgradeProtection(s, DateTimeOffset.Now, cleanShutdown: true));
            foreach (var s in expired)
            {
                // 有链接克隆以此为基线 → 交回交互路径（删除前必须向用户列明影响）
                var plan = SnapshotPlanner.PlanDelete(tree, s.Uuid, SnapshotService.FindLinkedCloneReferences(pkg));
                if (plan.AffectedLinkedClones.Count > 0) continue;
                SnapshotService.Delete(pkg, s.Uuid, new Qemu.TransactionalDiskOps(_qemuImgPath));
            }
        }
        catch
        {
            // 快照树异常时保守保留（宁可多占空间不可丢用户数据）
        }
    }

    /// <summary>
    /// Core 重启后的无损重接管（§6.3）：扫描可接管的 session（QEMU 仍在运行），
    /// 重新连 QMP、按 PID 重新包进程句柄、重新挂退出监视。VM 本身不受影响。
    /// </summary>
    public object AdoptRunningVms()
    {
        var adopted = new List<string>();
        var dead = new List<string>();
        var scanner = new CoreCrashRecovery();
        foreach (var result in scanner.ScanAdoptable(_db.LibraryRoot ?? string.Empty))
        {
            try
            {
                var pkg = result.Package;
                var session = result.Session;
                // 归属判定：别机的会话一概不动（库可能在 NAS/同步盘上；别机 PID 在本机
                // 必然"不存在"，按死了处理会解锁别人正在运行的 VM）
                if (session.MachineId is not null && session.MachineId != MachineId)
                    continue;
                // PID 未写入（Core 在 Start 与回写 session 之间崩溃）≠ 死了。
                // 此时 QEMU 可能正在运行：按"死了"清 runtime/放锁会破坏 vm.lock 永不自动清除的
                // 不变式（残留锁交给用户手动确认解锁）
                if (session.QemuPid <= 0)
                    continue;
                if (!result.QemuAlive)
                {
                    // QEMU 已不在（比如 Core 崩溃期间客户机内正常关机）：做干净收尾
                    if (Directory.Exists(pkg.RuntimePath))
                        foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                    new VmLock(pkg).Release();
                    dead.Add(pkg.Path);
                    continue;
                }
                var proc = Process.GetProcessById(session.QemuPid);
                // PID 复用防护：进程名必须还是 QEMU（否则是别人复用了 PID——把它当 QEMU 接管
                // 会在错误进程退出时误清 runtime/误放锁）
                if (!proc.ProcessName.Contains("qemu", StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(pkg.RuntimePath))
                        foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                    new VmLock(pkg).Release();
                    dead.Add(pkg.Path);
                    continue;
                }
                var vm = new RunningVm(pkg.Path, session, proc);
                vm.Qmp = TryConnectQmp(session.QmpPipe);
                // 挂起标记 + 活着的 QEMU = 上次 Core 在"标记已落盘、quit 未送达"窗口崩溃。
                // 原进程仍握有完整状态：直接 cont 让它继续跑，清掉标记并回收 suspend.state
                // （否则库列表显示"已挂起"，之后"恢复"会把旧内存重放到已前进的磁盘上）。
                var staleState = VmState.Load(pkg);
                if (staleState.SuspendedStatePath is not null)
                {
                    try
                    {
                        vm.Qmp?.ExecuteAsync("cont").GetAwaiter().GetResult();
                        var suspendFile = PathPolicy.Resolve(pkg, staleState.SuspendedStatePath);
                        staleState.SuspendedStatePath = null;
                        staleState.SuspendFingerprint = null;
                        staleState.Save(pkg);
                        if (File.Exists(suspendFile)) File.Delete(suspendFile);
                    }
                    catch
                    {
                        // cont 不可达：保守保留标记（用户仍可从 suspend.state 恢复）
                    }
                }
                _running[pkg.Path] = vm;
                WatchQemuProcess(vm);
                adopted.Add(pkg.Path);
            }
            catch
            {
                // 单台接管失败不影响其他；预检/手动解锁兜底
            }
        }
        return new { adopted, cleanedUp = dead };
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string CurrentHostFingerprint() =>
        $"{Environment.ProcessorCount}cpus|{_bundledQemuMajor}|{Environment.OSVersion.Version}";

    private QmpClient? TryConnectQmp(string pipeName)
    {
        QmpClient? client = null;
        try
        {
            var transport = _qmpTransportFactory(pipeName);
            if (transport is null) return null;
            client = new QmpClient(transport);
            // 在线程池线程完成握手（避免捕获调用方上下文死锁）；QEMU 启动窗口内可能未就绪 → 限时等待
            Task.Run(() => client.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return client;
        }
        catch
        {
            // 未就绪/超时：释放本次失败的连接（调用方后续会重试），不累积泄漏句柄
            client?.Dispose();
            return null;
        }
    }

    private QmpClient RequireQmp(string packagePath)
    {
        if (!_running.TryGetValue(packagePath, out var vm))
            throw new GrassCoreException("此虚拟机没有在运行。");
        // QMP 可能在启动窗口内未就绪：后续操作在这里重试连接
        vm.Qmp ??= TryConnectQmp(vm.Session.QmpPipe);
        return vm.Qmp ?? throw new GrassCoreException("QMP 控制通道不可用（QEMU 可能仍在启动），请稍后重试。");
    }

    public object PowerAction(string packagePath, string action) =>
        WithPackageGate(packagePath, () => PowerActionCore(packagePath, action));

    private object PowerActionCore(string packagePath, string action)
    {
        switch (action)
        {
            // 正常关机 = ACPI 电源按钮请求（QGA/ACPI）；不是 kill process。
            // 只有这条路径标记为"正常关机"信号（升级保护快照的 24h 计时以此为前提）。
            case "shutdown":
            {
                var qmp = RequireQmp(packagePath);
                var r = qmp.AcpiShutdownAsync().GetAwaiter().GetResult();
                _running[packagePath].AcpiShutdownRequested = true;
                return QmpResult(r);
            }
            // 强制关机 = 电源菜单 + 二次确认后调用方才允许（不算正常关机）
            case "forceOff":
                return QmpResult(RequireQmp(packagePath).ForceQuitAsync().GetAwaiter().GetResult());
            // 挂起 = 保存完整运行状态后完全退出 QEMU（不是 pause；1.0 无"暂停"）。
            // 直接调内部实现：PowerAction 已持有该包的门，再进公共 SuspendVm 会同线程重入死锁。
            case "suspend":
                return SuspendVmCore(packagePath);
            default:
                throw new GrassCoreException($"未知电源动作：{action}");
        }
    }

    private static object QmpResult(System.Text.Json.JsonElement e) => new { sent = true, result = e.ToString() };

    private object SuspendVm(string packagePath) =>
        WithPackageGate(packagePath, () => SuspendVmCore(packagePath));

    private object SuspendVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var qmp = RequireQmp(packagePath);
        // 挂起序列：stop（稳定点）→ migrate file:（保存内存/CPU/设备状态）→ 轮询至完成 → quit。
        // migrate 命令在迁移【开始】时即返回；大内存 VM 需要真实等待。
        qmp.StopAsync().GetAwaiter().GetResult();
        var stateFile = Path.Combine(pkg.FirmwarePath, "..", "suspend.state");
        stateFile = Path.GetFullPath(stateFile);
        qmp.MigrateToFileAsync(stateFile).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            var status = qmp.MigrationStatusAsync().GetAwaiter().GetResult();
            if (status == "completed") break;
            if (status is "failed" or "cancelled")
            {
                // 保存失败：恢复运行而不是把 VM 冻在 stop 状态
                qmp.ExecuteAsync("cont").GetAwaiter().GetResult();
                throw new GrassCoreException($"保存挂起状态失败（{status}），虚拟机已恢复运行。");
            }
            if (DateTime.UtcNow > deadline)
            {
                qmp.ExecuteAsync("cont").GetAwaiter().GetResult();
                throw new GrassCoreException("保存挂起状态超时，虚拟机已恢复运行。");
            }
            Thread.Sleep(500);
        }

        // 先落盘挂起标记再退出 QEMU：若 Core 在 quit 前后崩溃，VM 一致地处于"已挂起"，
        // suspend.state 不会被静默丢弃（顺序颠倒会让保存的会话凭空消失）。
        var state = VmState.Load(pkg);
        state.SuspendedStatePath = PathPolicy.NormalizeReference(pkg, stateFile);
        state.SuspendFingerprint = CurrentHostFingerprint();
        state.Save(pkg);

        try
        {
            qmp.ForceQuitAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // quit 失败：标记回滚（QEMU 仍在运行/可控，用户可重试挂起或正常关机）
            state.SuspendedStatePath = null;
            state.SuspendFingerprint = null;
            state.Save(pkg);
            throw;
        }

        ReleaseVm(pkg, clearRuntime: false); // 挂起后退出 QEMU，vm.lock 释放；suspend.state 在包内
        return new { suspended = true, stateFile };
    }

    private void ReleaseVm(GrassVmPackage pkg, bool clearRuntime = true)
    {
        if (_running.TryRemove(pkg.Path, out var vm))
        {
            vm.Qmp?.Dispose();
            // 正常关机后清空 runtime/ 并删除 vm.lock；挂起路径保留 runtime 记录片刻（session 已写入 state）
            if (clearRuntime && Directory.Exists(pkg.RuntimePath))
                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
            new VmLock(pkg).Release();
        }
    }

    public object UnlockVm(string packagePath)
    {
        // 调用方必须已取得用户的风险确认（UI 弹窗）
        var pkg = new GrassVmPackage(packagePath);
        new VmLock(pkg).ForceUnlockByUser();
        return new { unlocked = true };
    }

    /// <summary>CD/DVD 热插拔（唯一允许运行中修改的设备）：换镜像 / 弹出。</summary>
    public object ChangeMedium(string packagePath, string deviceId, string? isoPath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).Load();
        var cd = config.Devices.OfType<CdromDevice>().SingleOrDefault(d => d.DeviceId == deviceId)
                 ?? throw new GrassCoreException("找不到这台虚拟机的 CD/DVD 设备。");
        var resolved = isoPath is null ? null : PathPolicy.Resolve(pkg, isoPath);
        var qmp = RequireQmp(packagePath);
        if (resolved is null)
            qmp.EjectMediumAsync("cd" + cd.CreatedOrder).GetAwaiter().GetResult();
        else
            qmp.InsertMediumAsync("cd" + cd.CreatedOrder, resolved).GetAwaiter().GetResult();
        // 界面展示必须等于当前事实：热插拔成功后立即写回 config
        cd.IsoPath = isoPath is null ? null : PathPolicy.NormalizeReference(pkg, resolved!);
        new ConfigStore(pkg).Save(config);
        return new { changed = true };
    }

    // ---------- 设置（界面展示 = 当前事实）----------

    /// <summary>显示器窗口连接信息：-spice port=0 自动分配后的真实端口（经 QMP query-spice）。</summary>
    public object GetDisplayInfo(string packagePath)
    {
        var qmp = RequireQmp(packagePath);
        var port = qmp.QuerySpicePortAsync().GetAwaiter().GetResult();
        return new { spicePort = port };
    }

    /// <summary>宿主资源信息（设置页滑杆上限用；内存上限 = 物理内存一半）。</summary>
    public object GetHostInfo()
    {
        return new
        {
            cpuCores = Environment.ProcessorCount,
            memoryMiB = GetTotalHostMemoryMiB(),
        };
    }

    /// <summary>读取完整配置（设置页数据源；直接返回 JSON 对象而非字符串）。</summary>
    public object GetConfig(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).LoadAndUpgrade();
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            ConfigJson.Serialize(config));
    }

    /// <summary>
    /// 保存配置：仅关机状态允许（运行中唯一可改的是 CD/DVD，走 changeMedium）。
    /// 值域钳制：CPU 1..宿主核数，内存 512MB..宿主一半，磁盘/设备数量上限。
    /// </summary>
    public object UpdateConfig(string packagePath, string configJson) => WithPackageGate(packagePath, () => UpdateConfigCore(packagePath, configJson));

    private object UpdateConfigCore(string packagePath, string configJson)
    {
        var pkg = new GrassVmPackage(packagePath);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，关机后才能修改设置。");
        if (File.Exists(pkg.LockPath))
            throw new GrassCoreException("虚拟机被锁定（可能异常退出残留）。请先在详情中解除锁定。");
        // 挂起状态同样不可改：保存的内存/CPU/设备拓扑属于旧配置，-incoming 无法在更改后的硬件上回放
        if (VmState.Load(pkg).SuspendedStatePath is not null)
            throw new GrassCoreException("虚拟机已挂起。请先恢复并正常关机后再修改设置。");

        VmConfiguration config;
        try
        {
            config = ConfigJson.Deserialize(configJson);
        }
        catch (Exception e)
        {
            throw new GrassCoreException("配置格式不正确：" + e.Message);
        }
        if (config.SchemaVersion != VmConfiguration.CurrentSchemaVersion)
            throw new GrassCoreException($"配置版本不受支持（{config.SchemaVersion}），请用新版应用打开。");

        // 钳制到安全范围（宁可钳制不可拒绝——消费级产品原则）
        config.CpuCores = Math.Clamp(config.CpuCores, 1, Math.Max(1, Environment.ProcessorCount));
        var maxMem = Math.Max(512, (int)(GetTotalHostMemoryMiB() / 2));
        config.MemoryMiB = Math.Clamp(config.MemoryMiB, 512, maxMem);
        if (string.IsNullOrWhiteSpace(config.Name)) throw new GrassCoreException("虚拟机名称不能为空。");
        if (config.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || config.Name.Contains(','))
            throw new GrassCoreException(
                "名称包含文件系统或 QEMU 不允许的字符（含逗号）。");
        if (config.Devices.Count > 16)
            throw new GrassCoreException("设备数量超出上限（16）。");

        new ConfigStore(pkg).Save(config);
        return new { saved = true, cpuCores = config.CpuCores, memoryMiB = config.MemoryMiB };
    }

    private static long GetTotalHostMemoryMiB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                var row = searcher.Get().Cast<System.Management.ManagementBaseObject>().First();
                return Convert.ToInt64(row["TotalVisibleMemorySize"]) / 1024;
            }
        }
        catch { /* WMI 不可用时退回保守默认 */ }
        return 8 * 1024; // 8 GB 保守值
    }

    /// <summary>扩容硬盘（只能扩大；qemu-img resize 事务化执行 + 魔数校验保持链完整）。</summary>
    public object ResizeDisk(string packagePath, string deviceId, long newGiB) => WithPackageGate(packagePath, () => ResizeDiskCore(packagePath, deviceId, newGiB));

    private object ResizeDiskCore(string packagePath, string deviceId, long newGiB)
    {
        var pkg = new GrassVmPackage(packagePath);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，关机后才能修改硬盘容量。");
        if (VmState.Load(pkg).SuspendedStatePath is not null)
            throw new GrassCoreException("虚拟机已挂起。请先恢复并正常关机后再修改硬盘容量。");
        var config = new ConfigStore(pkg).Load();
        var disk = config.Devices.OfType<DiskDevice>().SingleOrDefault(d => d.DeviceId == deviceId)
                   ?? throw new GrassCoreException("找不到这块硬盘。");
        var newSize = newGiB * 1024L * 1024 * 1024;
        if (newSize <= disk.SizeBytes)
            throw new GrassCoreException("硬盘容量只能扩大，不能缩小。");
        if (disk.IsExternal)
            throw new GrassCoreException("这块硬盘在虚拟机包外部，请先在文件管理器中处理。");

        var diskOps = new TransactionalDiskOps(_qemuImgPath);
        diskOps.ResizeQcow2Async(PathPolicy.Resolve(pkg, disk.Path), newSize).GetAwaiter().GetResult();
        disk.SizeBytes = newSize;
        new ConfigStore(pkg).Save(config);
        return new { resized = true, sizeGiB = newGiB };
    }

    // ---------- 克隆 ----------

    /// <summary>克隆/快照等重操作的关机状态守卫：运行/锁定/挂起都不允许（Core 是唯一守门人）。</summary>
    private void EnsureStoppedForMutation(GrassVmPackage pkg)
    {
        // 换入中断修复（幂等）：残留暂存 overlay 不清掉，CreateOverlay 会因 overwrite:false 失败
        SnapshotService.RepairStagedOverlays(pkg);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，关机后才能执行此操作。");
        if (File.Exists(pkg.LockPath))
            throw new GrassCoreException("虚拟机被锁定（可能异常退出残留）。请先解除锁定。");
        if (VmState.Load(pkg).SuspendedStatePath is not null)
            throw new GrassCoreException("虚拟机已挂起。请先恢复并正常关机后再执行此操作。");
    }

    public object FullClone(string packagePath, string newName) => WithPackageGate(packagePath, () => FullCloneCore(packagePath, newName));

    private object FullCloneCore(string packagePath, string newName)
    {
        var pkg = new GrassVmPackage(packagePath);
        EnsureStoppedForMutation(pkg);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.FullCloneAsync(pkg, newName).GetAwaiter().GetResult();
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    public object LinkedClone(string packagePath, string snapshotUuid, string newName) => WithPackageGate(packagePath, () => LinkedCloneCore(packagePath, snapshotUuid, newName));

    private object LinkedCloneCore(string packagePath, string snapshotUuid, string newName)
    {
        var pkg = new GrassVmPackage(packagePath);
        EnsureStoppedForMutation(pkg);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.LinkedClone(pkg, snapshotUuid, newName);
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    // ---------- 导入导出（导出前必须关机）----------

    public object ExportZip(string packagePath, string zipPath) => WithPackageGate(packagePath, () => ExportZipCore(packagePath, zipPath));

    private object ExportZipCore(string packagePath, string zipPath)
    {
        var pkg = new GrassVmPackage(packagePath);
        ExportImport.GrassVmZip.EnsureExportable(pkg, _running.ContainsKey(pkg.Path));
        ExportImport.GrassVmZip.Export(pkg, zipPath);
        return new { exported = zipPath };
    }

    public object ImportZip(string zipPath)
    {
        var root = _db.LibraryRoot ?? throw new GrassCoreException("尚未设置虚拟机存档位置。");
        var pkg = ExportImport.GrassVmZip.Import(zipPath, root);
        _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new CreateVmResult(pkg.Path, pkg.Name);
    }

    /// <summary>OVA/OVF 导入分析：配置预览 + 磁盘清单 + 警告 + 完全无法支持的设备 + 空间预估。</summary>
    public object PlanImportOvf(string ovfOrOvaPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "grassvm-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ovfPath;
            if (ovfOrOvaPath.EndsWith(".ova", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(tempRoot, "ova");
                ovfPath = ExportImport.OvfImporter.ExtractOva(ovfOrOvaPath, dir);
            }
            else
            {
                ovfPath = ovfOrOvaPath;
            }
            var importer = new ExportImport.OvfImporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
            var plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath), Path.GetDirectoryName(Path.GetFullPath(ovfPath))!);
            var required = ExportImport.OvfImporter.EstimateRequiredBytes(plan.Disks);
            return new
            {
                vmName = plan.Config.Name,
                cpu = plan.Config.CpuCores,
                memoryMiB = plan.Config.MemoryMiB,
                disks = plan.Disks.Select(d => new { source = Path.GetFileName(d.SourceFile), capacityGiB = d.VirtualSizeBytes / 1024 / 1024 / 1024 }),
                warnings = plan.Warnings,
                unsupported = plan.UnsupportedDevices,
                blocksImport = plan.BlocksImport,
                requiredGiB = Math.Max(1, required / 1024 / 1024 / 1024),
            };
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    public object ExecuteImportOvf(string ovfOrOvaPath, string vmName, bool allowUnsupported)
    {
        var root = _db.LibraryRoot ?? throw new GrassCoreException("尚未设置虚拟机存档位置。");
        var tempRoot = Path.Combine(Path.GetTempPath(), "grassvm-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            string ovfPath = ovfOrOvaPath.EndsWith(".ova", StringComparison.OrdinalIgnoreCase)
                ? ExportImport.OvfImporter.ExtractOva(ovfOrOvaPath, Path.Combine(tempRoot, "ova"))
                : ovfOrOvaPath;
            var importer = new ExportImport.OvfImporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
            var plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath), Path.GetDirectoryName(Path.GetFullPath(ovfPath))!);
            var pkg = importer.ExecuteAsync(plan, root, vmName, allowUnsupported).GetAwaiter().GetResult();
            _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
            return new { path = pkg.Path, name = pkg.Name, warnings = plan.Warnings };
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>OVF 目录导出（当前有效状态；OVA 打包由导出器完成）。</summary>
    public object ExportOvf(string packagePath, string destDir) => WithPackageGate(packagePath, () => ExportOvfCore(packagePath, destDir));

    private object ExportOvfCore(string packagePath, string destDir)
    {
        var pkg = new GrassVmPackage(packagePath);
        ExportImport.GrassVmZip.EnsureExportable(pkg, _running.ContainsKey(pkg.Path));
        var exporter = new ExportImport.OvfExporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var ovfPath = exporter.ExportAsync(pkg, destDir).GetAwaiter().GetResult();
        return new { ovf = ovfPath };
    }

    // ---------- 快照 ----------

    public object CreateSnapshot(string packagePath, string name, string? description) => WithPackageGate(packagePath, () => CreateSnapshotCore(packagePath, name, description));

    private object CreateSnapshotCore(string packagePath, string name, string? description)
    {
        var pkg = new GrassVmPackage(packagePath);
        EnsureStoppedForMutation(pkg);
        var config = new ConfigStore(pkg).Load();
        var snap = SnapshotService.Create(pkg, config, name, description,
            diskOps: new Qemu.TransactionalDiskOps(_qemuImgPath));
        return new { uuid = snap.Uuid };
    }

    public object ListSnapshots(string packagePath) => SnapshotService.List(new GrassVmPackage(packagePath));

    public object RestoreSnapshot(string packagePath, string uuid) => WithPackageGate(packagePath, () => RestoreSnapshotCore(packagePath, uuid));

    private object RestoreSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        EnsureStoppedForMutation(pkg);
        var plan = SnapshotPlanner.PlanRestore(SnapshotService.LoadTree(pkg), uuid);
        SnapshotService.Restore(pkg, uuid, new Qemu.TransactionalDiskOps(_qemuImgPath));
        return new { restored = uuid, warnings = plan.Warnings };
    }

    public object PlanDeleteSnapshot(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        var clones = SnapshotService.FindLinkedCloneReferences(pkg);
        var plan = SnapshotPlanner.PlanDelete(SnapshotService.LoadTree(pkg), uuid, clones);
        return new
        {
            requiresMerge = plan.RequiresMerge,
            affectedLinkedClones = plan.AffectedLinkedClones.Select(c => new { c.ChildVmName, c.ChildVmPath }),
            rebindings = plan.Rebindings,
        };
    }

    public object DeleteSnapshot(string packagePath, string uuid) => WithPackageGate(packagePath, () => DeleteSnapshotCore(packagePath, uuid));

    private object DeleteSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        EnsureStoppedForMutation(pkg);
        SnapshotService.Delete(pkg, uuid, new Qemu.TransactionalDiskOps(_qemuImgPath));
        return new { deleted = uuid };
    }

    // ---------- 自动启动（串行：启动 1 台 → 等默认 10 秒 → 下一台；失败只跳过该 VM）----------

    public async Task<object> RunAutostartAsync(CancellationToken ct = default)
    {
        var results = new List<object>();
        var interval = TimeSpan.FromSeconds(_db.AutostartIntervalSeconds);
        foreach (var entry in _db.GetAutostartList().Where(e => e.Enabled))
        {
            try
            {
                results.Add(new { path = entry.VmPath, result = StartVm(entry.VmPath) });
            }
            catch (Exception ex)
            {
                // 某台失败只跳过，其他 VM 继续；登录后由 Windows 通知提示失败项
                results.Add(new { path = entry.VmPath, error = ex.Message });
            }
            if (interval > TimeSpan.Zero) await Task.Delay(interval, ct);
        }
        return new { started = results };
    }

    // ---------- 静态工具 ----------

    private static VmConfigView ToConfigView(VmConfiguration config)
    {
        var disks = config.DevicesOfType<DiskDevice>()
            .Select(d => new VmConfigView.DiskView(d.Path, "硬盘")).ToList();
        var cds = config.DevicesOfType<CdromDevice>()
            .Select(c => new VmConfigView.CdView(c.IsoPath, "CD/DVD")).ToList();
        return new VmConfigView(
            config.HasDisplayDevice,
            NeedsNvram: config.Firmware.Kind == FirmwareKind.Uefi,
            disks, cds);
    }
}

public sealed class GrassCoreException(string message) : Exception(message);

/// <summary>QEMU 进程启动抽象（测试注入用）。UI 或 GrassCore 崩溃不得导致 QEMU 退出。</summary>
public interface IQemuProcessLauncher
{
    Process Start(QemuCommandLine cmd);
}

public sealed class QemuProcessLauncher(string qemuSystemPath) : IQemuProcessLauncher
{
    public Process Start(QemuCommandLine cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = qemuSystemPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new GrassCoreException("无法启动 QEMU。");
        // QEMU stderr → logs/qemu.log：诊断早期退出（WHPX 初始化失败/固件缺失等）的唯一线索；
        // 同时持续排水，避免输出塞满管道把 QEMU 卡死
        try
        {
            var logDir = System.IO.Path.Combine(cmd.PackageRoot ?? ".", GrassVmPackage.LogsDir);
            Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, "qemu.log");
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(logPath, e.Data + "\n"); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) File.AppendAllText(logPath, e.Data + "\n"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch
        {
            // 日志失败不影响 QEMU 运行
        }
        return proc;
    }
}
