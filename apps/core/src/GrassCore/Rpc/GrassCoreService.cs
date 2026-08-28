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
        string bundledQemuMajor, IQemuProcessLauncher? launcher = null)
    {
        _db = db;
        _qemuSystemPath = qemuSystemPath;
        _qemuImgPath = qemuImgPath;
        _ovmfDir = ovmfDir;
        _bundledQemuMajor = bundledQemuMajor;
        _launcher = launcher ?? new QemuProcessLauncher(qemuSystemPath);
    }

    public sealed record RunningVm(string PackagePath, RuntimeSession Session, Process Process);

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

    public object ScanLibrary()
    {
        var root = _db.LibraryRoot ?? throw new InvalidOperationException("尚未设置虚拟机存档位置。");
        var vms = GrassVmPackage.ScanLibraryRoot(root)
            .Select(p => new { path = p.Path, name = p.Name, locked = File.Exists(p.LockPath) })
            .ToList();
        return new { libraryRoot = root, vms };
    }

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
        return new { path = pkg.Path, name = pkg.Name };
    }

    // ---------- 启动 / 电源 ----------

    public object StartVm(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).LoadAndUpgrade();

        // 预检：资源、vm.lock、显示设备（WHPX 检测在 Windows 主机上执行）
        var view = ToConfigView(config);
        var problems = StartupPreflight.Check(pkg, view);
        if (problems.Any(p => p.Fatal))
            throw new GrassCoreException(string.Join("\n", problems.Where(p => p.Fatal).Select(p => p.UserMessage)));

        var @lock = new VmLock(pkg);
        @lock.Acquire();

        // 升级保护：跨 QEMU major 首启 → 备份元数据 + 隐藏保护快照（由快照服务落盘）
        var state = VmState.Load(pkg);
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
        state.Save(pkg);

        _running[pkg.Path] = new RunningVm(pkg.Path, session, proc);
        return new { qemuPid = proc.Id, qmpPipe = cmd.QmpPipeName, sessionId };
    }

    public object PowerAction(string packagePath, string action) => action switch
    {
        // 正常关机 = QGA/ACPI 请求（不是 kill process）；强制关机 = 电源菜单 + 二次确认后调用方才允许
        "shutdown" => Qmp(packagePath, "system_powerdown"),
        "forceOff" => Qmp(packagePath, "quit"),
        // 挂起 = 保存内存/CPU/设备状态后完全退出 QEMU（不是 pause；1.0 无"暂停"）
        "suspend" => SuspendVm(packagePath),
        _ => throw new GrassCoreException($"未知电源动作：{action}"),
    };

    private object SuspendVm(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        // 挂起状态只保证在相同宿主 CPU 环境 + 同一 QEMU major 下恢复
        Qmp(packagePath, "migrate \"exec:\""); // 占位：实际实现为 stop + migrate to file + quit（Windows 实机联调阶段落地）
        ReleaseVm(pkg);
        return new { suspended = true };
    }

    private void ReleaseVm(GrassVmPackage pkg)
    {
        if (_running.Remove(pkg.Path))
        {
            // 正常关机后清空 runtime/ 并删除 vm.lock
            if (Directory.Exists(pkg.RuntimePath))
                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
            new VmLock(pkg).Release();
        }
    }

    private object Qmp(string packagePath, string command)
    {
        if (!_running.TryGetValue(packagePath, out var vm))
            throw new GrassCoreException("此虚拟机没有在运行。");
        // QMP 命令经 Named Pipe 发送（Windows 实机联调阶段接入 QmpClient）
        return new { sent = command, pipe = vm.Session.QmpPipe };
    }

    public object UnlockVm(string packagePath)
    {
        // 调用方必须已取得用户的风险确认（UI 弹窗）
        var pkg = new GrassVmPackage(packagePath);
        new VmLock(pkg).ForceUnlockByUser();
        return new { unlocked = true };
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
