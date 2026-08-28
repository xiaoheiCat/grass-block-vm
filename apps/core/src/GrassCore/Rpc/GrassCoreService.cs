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
    private readonly Dictionary<string, RunningVm> _running = new(StringComparer.OrdinalIgnoreCase);

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
        var isoPath = args.TryGetProperty("isoPath", out var iso) && iso.ValueKind != JsonValueKind.Null ? iso.GetString() : null;

        var root = _db.LibraryRoot ?? throw new InvalidOperationException("尚未设置虚拟机存档位置。");
        var profile = OsProfileLibrary.ById(profileId);
        var pkg = GrassVmPackage.CreateNew(root, name);
        var config = OsProfileLibrary.CreateDefaultConfig(profileId, name);

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
        return new CreateVmResult(pkg.Path, pkg.Name);
    }

    public sealed record CreateVmResult(string Path, string Name);

    // ---------- 启动 / 电源 ----------

    public object StartVm(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).LoadAndUpgrade();
        var state = VmState.Load(pkg);

        // 挂起状态：直接启动被拒绝（QEMU 必须 -incoming 才能回到保存的瞬间）
        if (state.SuspendedStatePath is not null)
            throw new GrassCoreException(
                "此虚拟机已挂起。请使用“恢复”回到挂起时的状态；如不需要保存的状态，请先恢复后再正常关机。");

        // 预检：资源、vm.lock、显示设备（WHPX 检测在 Windows 主机上执行）
        var view = ToConfigView(config);
        var problems = StartupPreflight.Check(pkg, view);
        if (problems.Any(p => p.Fatal))
            throw new GrassCoreException(string.Join("\n", problems.Where(p => p.Fatal).Select(p => p.UserMessage)));

        var @lock = new VmLock(pkg);
        @lock.Acquire();

        // 升级保护：跨 QEMU major 首启 → 备份元数据 + 隐藏保护快照（由快照服务落盘）
        if (UpgradeProtection.NeedsProtection(state.LastQemuMajor, _bundledQemuMajor))
        {
            var plan = UpgradeProtection.CreatePlan(pkg, state.LastQemuMajor!, _bundledQemuMajor);
            UpgradeProtection.BackupMetadata(pkg, plan.MetadataBackupDir);
            SnapshotService.Create(pkg, config, name: "升级保护快照", isUpgradeProtection: true);
        }

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var cmd = new QemuCommandBuilder(config, _ovmfDir).Build(pkg.Path, sessionId);
        var session = new RuntimeSession
        {
            SessionId = sessionId,
            QmpPipe = cmd.QmpPipeName,
            StartedAt = DateTimeOffset.UtcNow,
            QemuMajorAtStart = _bundledQemuMajor,
        };
        Directory.CreateDirectory(pkg.RuntimePath);
        AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

        var proc = _launcher.Start(cmd);
        session.QemuPid = proc.Id;
        AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

        state.LastQemuMajor = _bundledQemuMajor;
        state.LastStartedAt = DateTimeOffset.UtcNow;
        state.SuspendedStatePath = null;
        state.Save(pkg);

        var vm = new RunningVm(pkg.Path, session, proc);
        vm.Qmp = TryConnectQmp(session.QmpPipe);
        _running[pkg.Path] = vm;
        return new { qemuPid = proc.Id, qmpPipe = cmd.QmpPipeName, sessionId };
    }

    /// <summary>挂起恢复：-incoming 从保存状态回到挂起瞬间。只保证相同宿主 CPU + 同一 QEMU major。</summary>
    public object ResumeVm(string packagePath)
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

        var @lock = new VmLock(pkg);
        @lock.Acquire();
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
            };
            Directory.CreateDirectory(pkg.RuntimePath);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());
            var proc = _launcher.Start(cmd);
            session.QemuPid = proc.Id;
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            // 恢复完成：清除挂起标记（状态文件保留到下次正常关机？不——恢复即消费）
            state.SuspendedStatePath = null;
            state.SuspendFingerprint = null;
            state.Save(pkg);

            var vm = new RunningVm(pkg.Path, session, proc);
            vm.Qmp = TryConnectQmp(session.QmpPipe);
            _running[pkg.Path] = vm;
            return new { qemuPid = proc.Id, resumed = true };
        }
        catch
        {
            new VmLock(pkg).Release();
            throw;
        }
    }

    private string CurrentHostFingerprint() =>
        $"{Environment.ProcessorCount}cpus|{_bundledQemuMajor}|{Environment.OSVersion.Version}";

    private QmpClient? TryConnectQmp(string pipeName)
    {
        try
        {
            var transport = _qmpTransportFactory(pipeName);
            if (transport is null) return null;
            var client = new QmpClient(transport);
            // 在线程池线程完成握手（避免捕获调用方上下文死锁）；QEMU 启动窗口内可能未就绪 → 限时等待
            Task.Run(() => client.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return client;
        }
        catch
        {
            return null; // QMP 未就绪（QEMU 尚在启动窗口内）；后续操作会再次尝试
        }
    }

    private QmpClient RequireQmp(string packagePath)
    {
        if (!_running.TryGetValue(packagePath, out var vm) || vm.Qmp is null)
            throw new GrassCoreException("此虚拟机没有在运行，或 QMP 控制通道不可用。");
        return vm.Qmp;
    }

    public object PowerAction(string packagePath, string action) => action switch
    {
        // 正常关机 = ACPI 电源按钮请求（QGA/ACPI）；不是 kill process
        "shutdown" => QmpResult(RequireQmp(packagePath).AcpiShutdownAsync().GetAwaiter().GetResult()),
        // 强制关机 = 电源菜单 + 二次确认后调用方才允许
        "forceOff" => QmpResult(RequireQmp(packagePath).ForceQuitAsync().GetAwaiter().GetResult()),
        // 挂起 = 保存完整运行状态后完全退出 QEMU（不是 pause；1.0 无"暂停"）
        "suspend" => SuspendVm(packagePath),
        _ => throw new GrassCoreException($"未知电源动作：{action}"),
    };

    private static object QmpResult(System.Text.Json.JsonElement e) => new { sent = true, result = e.ToString() };

    private object SuspendVm(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var qmp = RequireQmp(packagePath);
        // 挂起序列：stop（稳定点）→ migrate file:（保存内存/CPU/设备状态）→ 等完成 → quit
        qmp.StopAsync().GetAwaiter().GetResult();
        var stateFile = Path.Combine(pkg.FirmwarePath, "..", "suspend.state");
        stateFile = Path.GetFullPath(stateFile);
        qmp.MigrateToFileAsync(stateFile).GetAwaiter().GetResult();
        if (!qmp.MigrationFinishedAsync().GetAwaiter().GetResult())
            throw new GrassCoreException("保存挂起状态未完成，虚拟机仍在运行；请稍后重试。");
        qmp.ForceQuitAsync().GetAwaiter().GetResult();

        // 状态落盘：标记挂起 + 指纹（只保证相同宿主 CPU + 同一 QEMU major 下恢复）
        var state = VmState.Load(pkg);
        state.SuspendedStatePath = PathPolicy.NormalizeReference(pkg, stateFile);
        state.SuspendFingerprint = CurrentHostFingerprint();
        state.Save(pkg);

        ReleaseVm(pkg, clearRuntime: false); // 挂起后退出 QEMU，vm.lock 释放；suspend.state 在包内
        return new { suspended = true, stateFile };
    }

    private void ReleaseVm(GrassVmPackage pkg, bool clearRuntime = true)
    {
        if (_running.Remove(pkg.Path, out var vm))
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
        qmp.ChangeMediumAsync("cd" + cd.CreatedOrder, resolved).GetAwaiter().GetResult();
        // 界面展示必须等于当前事实：热插拔成功后立即写回 config
        cd.IsoPath = isoPath is null ? null : PathPolicy.NormalizeReference(pkg, resolved!);
        new ConfigStore(pkg).Save(config);
        return new { changed = true };
    }

    // ---------- 克隆 ----------

    public object FullClone(string packagePath, string newName)
    {
        var pkg = new GrassVmPackage(packagePath);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.FullCloneAsync(pkg, newName).GetAwaiter().GetResult();
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    public object LinkedClone(string packagePath, string snapshotUuid, string newName)
    {
        var pkg = new GrassVmPackage(packagePath);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.LinkedClone(pkg, snapshotUuid, newName);
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    // ---------- 导入导出（导出前必须关机）----------

    public object ExportZip(string packagePath, string zipPath)
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
    public object ExportOvf(string packagePath, string destDir)
    {
        var pkg = new GrassVmPackage(packagePath);
        ExportImport.GrassVmZip.EnsureExportable(pkg, _running.ContainsKey(pkg.Path));
        var exporter = new ExportImport.OvfExporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var ovfPath = exporter.ExportAsync(pkg, destDir).GetAwaiter().GetResult();
        return new { ovf = ovfPath };
    }

    // ---------- 快照 ----------

    public object CreateSnapshot(string packagePath, string name, string? description)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).Load();
        var snap = SnapshotService.Create(pkg, config, name, description);
        return new { uuid = snap.Uuid };
    }

    public object ListSnapshots(string packagePath) => SnapshotService.List(new GrassVmPackage(packagePath));

    public object RestoreSnapshot(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        var plan = SnapshotPlanner.PlanRestore(SnapshotService.LoadTree(pkg), uuid);
        SnapshotService.Restore(pkg, uuid);
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

    public object DeleteSnapshot(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        SnapshotService.Delete(pkg, uuid);
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
        return new VmConfigView(config.HasDisplayDevice, disks, cds);
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
        return Process.Start(psi) ?? throw new GrassCoreException("无法启动 QEMU。");
    }
}
