using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.WebSockets;
using System.Text;
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
    private readonly string? _helperExecutablePath;
    private readonly IQemuProcessLauncher _launcher;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunningVm> _running =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _machineIdGate = new();

    /// <summary>是否有运行中的 VM（Core 空闲退出判定用）。</summary>
    public bool HasRunningVms => !_running.IsEmpty;

    /// <summary>
    /// 进行中的收尾/磁盘作业数（快照提交/rebase、升级保护清理等）。
    /// 空闲退出必须等它归零：VM 从 _running 摘除之后还有一个 qemu-img 窗口，
    /// 大盘 commit 轻松超过两分钟——Core 死在链维护一半 = 下个实例并发抢锁。
    /// </summary>
    private int _inFlightDiskJobs;

    public bool HasInFlightDiskJobs => Volatile.Read(ref _inFlightDiskJobs) > 0;

    private void WithDiskJob(Action body)
    {
        Interlocked.Increment(ref _inFlightDiskJobs);
        try { body(); }
        finally { Interlocked.Decrement(ref _inFlightDiskJobs); }
    }

    private T WithDiskJob<T>(Func<T> body)
    {
        Interlocked.Increment(ref _inFlightDiskJobs);
        try { return body(); }
        finally { Interlocked.Decrement(ref _inFlightDiskJobs); }
    }

    /// <summary>
    /// 本机标识（宿主级持久，首次生成）：会话归属判定，防别机会话被本机"收尾"。
    /// 进程内缓存：写进 session 的值与后续校验必须字字相同——每次都走 DB 读取，
    /// 任何一次读漏都会现场生成新 GUID，把自家会话误判成"别机的"（接管静默跳过）
    /// </summary>
    private string? _machineIdCached;

    private string MachineId
    {
        get
        {
            if (_machineIdCached is not null) return _machineIdCached;
            lock (_machineIdGate)
            {
                if (_machineIdCached is not null) return _machineIdCached;
                // INSERT OR IGNORE + 同一事务内回读，避免两个并发启动线程各自
                // 生成 machineId 后互相覆盖，导致其中一台 VM 的会话无法接管。
                _machineIdCached = _db.GetOrCreatePreference("machineId", () => Guid.NewGuid().ToString("N"));
                return _machineIdCached;
            }
        }
    }

    /// <summary>每包一把信号量：生命周期操作（启动/恢复/挂起/电源）按包串行——并发 RPC 分发下双击 start 不会再赛跑。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pkgGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进行中的生命周期阶段（"starting"/"suspending"）：ScanLibrary 如实上报，
    /// UI 才不会在长达数分钟的挂起/启动期间显示错误状态、点开注定失败的电源菜单。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lifecycleInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    // LibraryRoot 是宿主级全局状态。长操作持有读锁，允许 ScanLibrary/其他读
    // 请求继续服务；切换根目录取得写锁后会等待所有操作完成，期间不会把新根切
    // 进旧操作的中间窗口。支持递归是因为 CreateVm(startAfterCreate) 等复合路径
    // 会在同一线程再次进入包门。
    private readonly ReaderWriterLockSlim _libraryRootGate =
        new(LockRecursionPolicy.SupportsRecursion);

    private T WithLibraryOperation<T>(Func<T> action)
    {
        _libraryRootGate.EnterReadLock();
        try
        {
            return WithDiskJob(action);
        }
        finally { _libraryRootGate.ExitReadLock(); }
    }

    private void WithLibraryOperation(Action action)
    {
        _libraryRootGate.EnterReadLock();
        try
        {
            WithDiskJob(action);
        }
        finally { _libraryRootGate.ExitReadLock(); }
    }

    private T WithLifecyclePhase<T>(string packagePath, string phase, Func<T> action)
    {
        _lifecycleInFlight[packagePath] = phase;
        try { return action(); }
        finally { _lifecycleInFlight.TryRemove(packagePath, out _); }
    }

    private T WithPackageGate<T>(string packagePath, Func<T> action)
    {
        _libraryRootGate.EnterReadLock();
        try
        {
            // 在根目录门内重新解析路径，避免调用方在解析后、真正拿门前
            // 切换 LibraryRoot。所有包级长操作也在此门内执行，切根只能等待
            // 操作完全结束，不能把新根切进旧操作的中间窗口。
            var managedPath = ResolveManagedPackagePath(packagePath);
            var gate = _pkgGates.GetOrAdd(managedPath,
                _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try { return WithDiskJob(action); }
            catch (VmLockedException e) { throw new GrassCoreException(e.Message); }
            finally { gate.Release(); }
        }
        finally { _libraryRootGate.ExitReadLock(); }
    }

    private void WithPackageGate(string packagePath, Action action)
    {
        _libraryRootGate.EnterReadLock();
        try
        {
            var managedPath = ResolveManagedPackagePath(packagePath);
            var gate = _pkgGates.GetOrAdd(managedPath, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try { WithDiskJob(action); }
            catch (VmLockedException e) { throw new GrassCoreException(e.Message); }
            finally { gate.Release(); }
        }
        finally { _libraryRootGate.ExitReadLock(); }
    }

    private string ResolveManagedPackagePath(string packagePath)
    {
        var root = _db.LibraryRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new GrassCoreException("尚未设置虚拟机存档位置。");
        var fullPackage = Path.GetFullPath(packagePath);
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPackage.StartsWith(prefix, cmp)
            || !GrassVmPackage.IsGrassVmDirectory(fullPackage))
            throw new GrassCoreException("虚拟机必须位于当前存档位置内。");
        return fullPackage;
    }

    public GrassCoreService(HostDb db, string qemuSystemPath, string qemuImgPath, string ovmfDir,
        string bundledQemuMajor, IQemuProcessLauncher? launcher = null,
        Func<string, IQmpTransport?>? qmpTransportFactory = null,
        string? helperExecutablePath = null)
    {
        _db = db;
        _qemuSystemPath = qemuSystemPath;
        _qemuImgPath = qemuImgPath;
        _ovmfDir = ovmfDir;
        _bundledQemuMajor = bundledQemuMajor;
        _helperExecutablePath = helperExecutablePath;
        _launcher = launcher ?? new QemuProcessLauncher(qemuSystemPath);
        _qmpTransportFactory = qmpTransportFactory ?? DefaultQmpTransport;
    }

    /// <summary>QMP 传输：Windows Named Pipe；开发机（非 Windows）上电源动作不可用（需实机联调）。</summary>
    private static IQmpTransport? DefaultQmpTransport(string pipeName) =>
        OperatingSystem.IsWindows() ? new WindowsNamedPipeTransport(pipeName) : null;

    private readonly Func<string, IQmpTransport?> _qmpTransportFactory;

    public sealed record RunningVm(string PackagePath, RuntimeSession Session, Process Process, VmLock Lock)
    {
        public QmpClient? Qmp { get; set; }
        /// <summary>已发送 ACPI 电源按钮请求（正常关机的唯一可信信号；quit/崩溃不算）。</summary>
        public volatile bool AcpiShutdownRequested;
        /// <summary>
        /// 本次是挂起恢复（-incoming）。退出时若挂起标记还在（恢复确认循环没能确认
        /// running），保存的内存状态不可再重放——客户机可能已跑过并写过磁盘。
        /// </summary>
        public volatile bool ResumedFromSuspend;
        /// <summary>
        /// 退出收尾由监督方负责（挂起流程）：它持有包级门并自己放锁。监视器此时
        /// 必须让位——否则同步派发（WaitForExit 在持门线程上触发 Exited）会自死锁，
        /// 异步派发也会抢在监督方之前放锁/清 runtime
        /// </summary>
        public volatile bool ExitCleanupSuppressed;
    }

    /// <summary>当前 Library Root（宿主只有一个；更改只影响之后创建/导入）。</summary>
    public string? LibraryRoot => _db.LibraryRoot;
    /// <summary>当前宿主会话标识，供启动恢复扫描器做跨机器会话隔离。</summary>
    public string CurrentMachineId => MachineId;

    /// <summary>设置 Library Root。旧目录 VM 不迁移、不再出现在主界面。</summary>
    public object SetLibraryRoot(string newRoot)
    {
        _libraryRootGate.EnterWriteLock();
        try
        {
        var targetRoot = Path.GetFullPath(newRoot);
        if (GrassVmPackage.IsGrassVmDirectory(targetRoot))
            throw new GrassCoreException("存档位置不能设置为虚拟机包目录。");
        if (GrassVmPackage.ContainsReparsePoint(targetRoot))
            throw new GrassCoreException("存档位置不能位于符号链接或目录联接路径下。");
        if (!_running.IsEmpty
            || Volatile.Read(ref _inFlightDiskJobs) > 0
            || !_lifecycleInFlight.IsEmpty)
            throw new GrassCoreException("存在运行中的虚拟机，关闭后才能切换存档位置。");
        if (!Directory.Exists(newRoot)) Directory.CreateDirectory(newRoot);
        _db.ChangeLibraryRoot(targetRoot);
        return new { libraryRoot = _db.LibraryRoot };
        }
        finally { _libraryRootGate.ExitWriteLock(); }
    }

    // ---------- Library ----------

    public ScanLibraryResult ScanLibrary()
    {
        _libraryRootGate.EnterReadLock();
        try
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
                // 进行中的生命周期（启动/挂起可能长达数分钟）：如实上报，UI 才不会
                // 在"实际已暂停"的挂起途中显示 正在运行 + 可点电源菜单
                if (_lifecycleInFlight.TryGetValue(p.Path, out var phase))
                    state = phase;
                else if (_running.ContainsKey(p.Path)) state = "running";
                // 锁+会话残留但【本 Core 没在运行它】（接管被合法拒绝：会话缺
                // MachineId / 损坏 / 别的机器的会话）：按 stopped 如实列出
                // （Locked=true 已经表达了"有残锁"）。报成 running 会让 UI 禁用
                // 解除锁定按钮——唯一诚实的恢复路径被掐死，卡片永久卡死
                return new VmSummaryDto(
                    Path: p.Path, Name: p.Name, OsProfileId: osProfile, State: state,
                    CpuCores: cpu, MemoryMiB: mem,
                    Locked: File.Exists(p.LockPath),
                    HasAutostart: autostart.GetValueOrDefault(p.Path, false));
            })
            .ToList();
        return new ScanLibraryResult(root, vms);
        }
        finally { _libraryRootGate.ExitReadLock(); }
    }

    public sealed record ScanLibraryResult(string LibraryRoot, List<VmSummaryDto> Vms);

    public sealed record VmSummaryDto(
        string Path, string Name, string OsProfileId, string State,
        int CpuCores, int MemoryMiB, bool Locked, bool HasAutostart);

    public object GetProfiles() => OsProfileLibrary.List()
        // Windows 11 需要 TPM 2.0 后端；在模拟器随安装包交付前不展示该 Profile，
        // 避免向导创建出必然无法启动的虚拟机。已有/导入的 windows-11 配置仍保留，
        // 启动时继续给出明确诊断。
        .Where(p => p.Id != "windows-11")
        .Select(p => new
    {
        id = p.Id, name = p.DisplayName, family = p.Family, verified = p.Verified,
        cpu = p.RecommendedCpuCores, memoryMiB = p.RecommendedMemoryMiB, diskGiB = p.RecommendedDiskBytes / 1024 / 1024 / 1024,
    });

    private static void ValidateConfigForLaunch(VmConfiguration config)
    {
        var maxCpu = Math.Max(1, Environment.ProcessorCount);
        var maxMem = Math.Max(512L, GetTotalHostMemoryMiB() / 2);
        if (config.CpuCores is < 1 || config.CpuCores > maxCpu)
            throw new GrassCoreException($"虚拟机 CPU 数量无效（必须为 1 到 {maxCpu}）。请在设置中修正后重试。");
        if (config.MemoryMiB < 512 || config.MemoryMiB > maxMem)
            throw new GrassCoreException($"虚拟机内存无效（必须为 512 MiB 到 {maxMem} MiB）。请在设置中修正后重试。");
        if (config.Devices.Count > 128)
            throw new GrassCoreException("虚拟机设备数量超过安全上限，请删除多余设备后重试。");
        if (!DeviceNamer.HasUniqueCreatedOrders(config))
            throw new GrassCoreException("虚拟机配置包含重复的设备添加顺序，无法安全启动。请在设置中重新保存设备配置。");
        if (config.Devices.Select(d => d.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Devices.Count)
            throw new GrassCoreException("虚拟机配置包含重复设备标识，无法安全启动。");
        if (config.Devices.OfType<NetworkDevice>().Any(n => n.Mode is NetworkMode.Bridged or NetworkMode.HostOnly))
            throw new GrassCoreException("桥接和 Host-only 网络尚未在当前版本交付，请改用 NAT 或断开模式。");
        foreach (var disk in config.Devices.OfType<DiskDevice>())
            if (disk.SizeBytes < 0 || disk.SizeBytes > 256L * 1024 * 1024 * 1024 * 1024)
                throw new GrassCoreException("虚拟磁盘容量超出安全上限，无法启动。");
    }

    // ---------- 创建（向导 → Core 全权落盘）----------

    public object CreateVm(JsonElement args) =>
        WithLibraryOperation(() => CreateVmCore(args));

    private object CreateVmCore(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!;
        var profileId = args.GetProperty("profileId").GetString()!;
        if (string.Equals(profileId, "windows-11", StringComparison.OrdinalIgnoreCase))
            throw new GrassCoreException("Windows 11 Profile 暂不可用：当前发布版尚未包含 TPM 2.0 模拟器。");
        // 与 cpuCores/memoryMiB/resizeDisk 同一道防线：向导输入 1e999 时
        // Number → Infinity，JSON.stringify 把它序列化成 null，GetInt64 对
        // Null 元素裸抛英文 InvalidOperationException——按产品规则给可读错误
        var diskGiBEl = args.GetProperty("diskGiB");
        var diskGiB = diskGiBEl.ValueKind == JsonValueKind.Number && diskGiBEl.TryGetInt64(out var diskGiBValue)
            ? diskGiBValue
            : throw new GrassCoreException("磁盘大小必须是整数 GB。");
        // 名称同时是包目录名与 QEMU -name 值：文件系统非法字符、逗号（QEMU 选项
        // 解析分隔符）与等号（会被 -name 当成 key=value 键，启动即失败）都拒绝
        if (IsInvalidVmName(name))
            throw new GrassCoreException(
                $"虚拟机名称不合法：{name}（不能为空，不能包含文件系统不允许的字符、逗号或等号）");
        var isoPath = args.TryGetProperty("isoPath", out var iso) && iso.ValueKind != JsonValueKind.Null ? iso.GetString() : null;

        // 向导可调参数：cpuCores / memoryMiB（缺省 = Profile 推荐；钳制到安全范围）
        var root = _db.LibraryRoot ?? throw new InvalidOperationException("尚未设置虚拟机存档位置。");
        var profile = OsProfileLibrary.ById(profileId);
        var pkg = GrassVmPackage.CreateNew(root, name);
        try
        {
        var config = OsProfileLibrary.CreateDefaultConfig(profileId, name);
        // 向导的数字输入是自由文本：越界值要【钳制】而不是裸抛（GetInt32 对
        // 超 int 的 JSON 数抛 FormatException、英文原文直接进错误横幅——
        // "宁可钳制不可拒绝"）。磁盘 GiB 同理钳到产品域上限，防止
        // GiB→字节的乘法静默回绕成负 size 交给 qemu-img
        if (args.TryGetProperty("cpuCores", out var cpuEl) && cpuEl.ValueKind == JsonValueKind.Number
            && cpuEl.TryGetInt64(out var cpu64))
            config.CpuCores = (int)Math.Clamp(cpu64, 1, Math.Max(1, Environment.ProcessorCount));
        if (args.TryGetProperty("memoryMiB", out var memEl) && memEl.ValueKind == JsonValueKind.Number
            && memEl.TryGetInt64(out var mem64))
            config.MemoryMiB = Library.HostResources.ClampMemoryMiB((int)Math.Clamp(mem64, 512, int.MaxValue));
        // 产品域：单盘 1..2048 GiB（任何真实 Profile 之上；2048TiB 级别的
        // 请求既放不下也建不出，钳到上限后由空间预检/真实 qemu-img 报可读错误）
        diskGiB = Math.Clamp(diskGiB, 1, 2048);

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
            var resolvedIso = PathPolicy.Resolve(pkg, isoPath);
            if (!File.Exists(resolvedIso) || (File.GetAttributes(resolvedIso) & FileAttributes.ReparsePoint) != 0)
                throw new GrassCoreException("安装镜像不存在或不是可用的普通文件。");
            if (!string.Equals(Path.GetExtension(resolvedIso), ".iso", StringComparison.OrdinalIgnoreCase))
                throw new GrassCoreException("安装镜像必须是 ISO 文件。");
            config.Devices.Add(new CdromDevice
            {
                IsoPath = PathPolicy.NormalizeReference(pkg, resolvedIso),
                CreatedOrder = DeviceNamer.NextCreatedOrder(config),
            });
        }
        if (profile.Firmware == FirmwareKind.Uefi)
        {
            // NVRAM 变量卷从模板复制，每 VM 独立。
            // 模板缺失必须当场失败（回滚包目录）：静默跳过的话，建出来的是一台
            // 预检永远拒绝启动的 VM，且提示语指向一个不存在的"设置里重建"入口
            // ——死路一条。与 OVF 导入的同场景处理（警告）至少同样显式
            var template = Path.Combine(_ovmfDir, "OVMF_VARS.fd");
            if (!File.Exists(template))
                throw new GrassCoreException(
                    $"无法创建 UEFI 虚拟机：固件模板缺失（{template}）。请重新安装或修复应用后重试。");
            Directory.CreateDirectory(pkg.FirmwarePath);
            File.Copy(template, Path.Combine(pkg.FirmwarePath, "VARS.fd"));
        }

        // 稀疏 QCOW2 创建（事务式）
        var diskOps = new TransactionalDiskOps(_qemuImgPath);
        diskOps.CreateSparseQcow2Async(diskPath, diskGiB * 1024 * 1024 * 1024).GetAwaiter().GetResult();

        new ConfigStore(pkg).Save(config);
        _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        }
        catch
        {
            // 创建失败回滚包目录：半成品（无 config.json 的骨架）会被 ScanLibrary
            // 当成坏 VM 列出来，且没有删除入口——用户只能去文件管理器里刨
            try { if (Directory.Exists(pkg.Path)) Directory.Delete(pkg.Path, recursive: true); }
            catch { /* 残留骨架：StartVm 预检会给诊断 */ }
            throw;
        }
        // "创建后立即启动"（向导默认开）：由 Core 一并完成，UI 只负责刷新。
        // 在回滚范围【之外】：VM 已完整建成，启动失败不该连带删掉它
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

    public object StartVm(string packagePath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => WithLifecyclePhase(managed, "starting", () => StartVmCore(managed)));
    }

    private object StartVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        // 已在运行（本 Core 实例）：幂等拒绝，不触碰任何状态
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("此虚拟机已经在运行。");

        // 先持有跨进程锁，再执行可能修改磁盘的收尾与预检。
        // 关键：②必须在磁盘预检【之前】——Create/Restore 的换入事务崩溃现场是
        // "工作盘缺失 + 暂存 overlay 完好"，那正是修复要补齐的形态；预检先跑
        // 会把可自愈的现场误报成"磁盘文件丢失"的死路（内部盘没有重定位入口）
        var @lock = new VmLock(pkg);
        @lock.Acquire();
        bool lockAcquired = true;
        VmConfiguration config;
        VmState state;
        try
        {
            // 配置和状态必须在取得跨进程锁后读取；否则停机操作在等待锁期间
            // 保存的新配置会被本次启动用旧快照覆盖/忽略。
            config = new ConfigStore(pkg).LoadAndUpgrade();
            ValidateConfigForLaunch(config);
            state = VmState.Load(pkg);
            if (state.SuspendedStatePath is not null)
                throw new GrassCoreException(
                    "此虚拟机已挂起。请使用“恢复”回到挂起时的状态；如不需要保存的状态，请先恢复后再正常关机。");
            var view = ToConfigView(config);
            RepairThenPreflight(pkg, view, lockHeld: true);
            if (config.Firmware.Tpm && config.DevicesOfType<TpmDevice>().Any(t => t.Enabled))
                throw new GrassCoreException("此虚拟机需要 TPM 2.0，但当前版本尚未提供 TPM 模拟器。请安装支持 TPM 的版本后重试。");
            if (OperatingSystem.IsWindows())
            {
                var whpx = WhpxCapability.CheckWindows();
                if (!whpx.Available)
                    throw new GrassCoreException($"此电脑无法使用硬件虚拟化（WHPX）：{whpx.UserGuidance}");
            }
        }
        catch
        {
            @lock.Release();
            lockAcquired = false;
            throw;
        }

        Process? proc = null;
        RuntimeSession? startedSession = null;
        RunningVm? startedVm = null;
        try
        {
            // 升级保护：跨 QEMU major 首启 → 备份元数据 + 隐藏保护快照（由快照服务落盘）。
            // 已存在未清理的保护快照就不再造：启动若在"快照已落、LastQemuMajor 还没写"
            // 之间失败，重试会叠一层又一层的保护快照（删除计时只认正常关机）。
            var protectionCreated = false;
            if (UpgradeProtection.NeedsProtection(state.LastQemuMajor, _bundledQemuMajor)
                && !SnapshotService.LoadTree(pkg).All.Any(s => s.IsUpgradeProtection))
            {
                var plan = UpgradeProtection.CreatePlan(pkg, state.LastQemuMajor!, _bundledQemuMajor);
                UpgradeProtection.BackupMetadata(pkg, plan.MetadataBackupDir);
                SnapshotService.Create(pkg, config, name: "升级保护快照", isUpgradeProtection: true,
                    diskOps: new Qemu.TransactionalDiskOps(_qemuImgPath),
                    upgradeProtectionBackupDir: plan.MetadataBackupDir);
                protectionCreated = true;
            }

            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var spicePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var cmd = new QemuCommandBuilder(config, _ovmfDir).Build(pkg.Path, sessionId, spicePassword: spicePassword);
            var session = new RuntimeSession
            {
                SessionId = sessionId,
                QmpPipe = cmd.QmpPipeName,
                StartedAt = DateTimeOffset.UtcNow,
                QemuMajorAtStart = _bundledQemuMajor,
                MachineId = MachineId,
                QemuExecutablePath = _qemuSystemPath,
                CommandLineFingerprint = CommandFingerprint(cmd),
                SpicePassword = spicePassword,
            };
            startedSession = session;
            Directory.CreateDirectory(pkg.RuntimePath);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            proc = _launcher.Start(cmd);
            session.QemuPid = proc.Id;
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            // 跨 major 首启：5 秒早期失败检测（升级保护的配套——QemuSurvivesEarlyWindow
            // 此前没有任何调用方，承诺的"提示回退上一 major"从未发生）。只在这条稀有
            // 路径上等 5 秒，正常启动不付出这个延迟。失败时走 catch 的 RollbackStart
            // （放锁/清 runtime）；保护快照与备份刻意保留
            if (protectionCreated
                && !WhpxCapability.QemuSurvivesEarlyWindowAsync(proc).GetAwaiter().GetResult())
            {
                throw new GrassCoreException(
                    "新版 QEMU 在 5 秒内退出，可能与此虚拟机不兼容。诊断信息已写入 logs/qemu.log；" +
                    "升级保护快照与元数据备份已保留（24 小时内不会自动删除），" +
                    "可回退到上一版本，或导出脱敏日志反馈问题。");
            }

            // 注意：升级保护快照已在磁盘上推进了 CurrentSnapshotUuid——必须重读，
            // 不能用过期实例覆盖（否则位置标记回退，后续自动删除会误判"位置不在这"，
            // 跳过工作 overlay 的 rebase → 链断）。只 patch 本函数拥有的字段。
            var fresh = VmState.Load(pkg);
            fresh.LastQemuMajor = _bundledQemuMajor;
            fresh.LastStartedAt = DateTimeOffset.UtcNow;
            fresh.SuspendedStatePath = null;
            fresh.Save(pkg);

            var vm = new RunningVm(pkg.Path, session, proc, @lock);
            startedVm = vm;
            vm.Qmp = TryConnectQmp(session.QmpPipe);
            if (vm.Qmp is null)
                throw new GrassCoreException("QMP 控制通道未就绪，虚拟机启动已回滚，请稍后重试。");
            StartHelper(pkg, session, vm.Qmp);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());
            _running[pkg.Path] = vm;
            WatchQemuProcess(vm);
            startedVm = null; // 所有权已转移给 _running/退出监视器
            return new { qemuPid = proc.Id, qmpPipe = cmd.QmpPipeName, sessionId };
        }
        catch
        {
            // 启动失败回滚：只清理【本次调用】获得的资源（锁/进程/runtime）。
            // 若失败发生在取锁之前（预检拒绝），不触碰磁盘上的任何状态——那是别人的锁。
            // WatchQemuProcess 或其前置落盘若失败，不能留下一个已退出/不可控的
            // RunningVm 条目；仅移除仍指向本次会话的实例，避免误删并发替换的会话。
            if (startedVm is not null
                && _running.TryGetValue(pkg.Path, out var current)
                && ReferenceEquals(current, startedVm))
                _running.TryRemove(pkg.Path, out _);
            if (lockAcquired) RollbackStart(pkg, proc, startedSession, @lock);
            throw;
        }
    }

    /// <summary>启动失败回滚（QEMU 未成功进入可控状态）。调用方必须持有 vm.lock。</summary>
    private static void RollbackStart(GrassVmPackage pkg, Process? proc, RuntimeSession? session, VmLock @lock)
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
            StopHelper(session);
            if (Directory.Exists(pkg.RuntimePath))
                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
            @lock.Release();
        }
        catch { /* 下次启动预检会给残留锁诊断 */ }
    }

    /// <summary>挂起恢复：-incoming 从保存状态回到挂起瞬间。只保证相同宿主 CPU + 同一 QEMU major。</summary>
    public object ResumeVm(string packagePath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => WithLifecyclePhase(managed, "starting", () => ResumeVmCore(managed)));
    }

    private object ResumeVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("此虚拟机已经在运行。");

        var @lock = new VmLock(pkg);
        @lock.Acquire();
        bool lockAcquired = true;
        VmConfiguration config;
        VmState state;
        string suspendFile;
        try
        {
            // 与 StartVm 相同：配置/挂起状态必须在取得锁后读取，避免与设置保存
            // 或其他停机操作交错而恢复旧配置。
            config = new ConfigStore(pkg).LoadAndUpgrade();
            ValidateConfigForLaunch(config);
            state = VmState.Load(pkg);
            if (state.SuspendedStatePath is null)
                throw new GrassCoreException("此虚拟机没有保存的挂起状态。");
            suspendFile = PathPolicy.Resolve(pkg, state.SuspendedStatePath);
            if (!File.Exists(suspendFile))
                throw new GrassCoreException("找不到挂起时保存的状态文件。该状态已丢失，只能重新启动虚拟机。");

            // 指纹校验：不同宿主 CPU / 不同 QEMU major 的挂起状态不保证可恢复
            var fingerprint = CurrentHostFingerprint();
            if (state.SuspendFingerprint is not null && state.SuspendFingerprint != fingerprint)
                throw new GrassCoreException(
                    "此挂起状态是在不同的硬件或 QEMU 版本上保存的，无法保证正确恢复。建议重新启动虚拟机。");
            var view = ToConfigView(config);
            RepairThenPreflight(pkg, view, lockHeld: true);
        if (config.Firmware.Tpm && config.DevicesOfType<TpmDevice>().Any(t => t.Enabled))
            throw new GrassCoreException("此虚拟机需要 TPM 2.0，但当前版本尚未提供 TPM 模拟器。请安装支持 TPM 的版本后重试。");
        if (OperatingSystem.IsWindows())
        {
            var whpx = WhpxCapability.CheckWindows();
            if (!whpx.Available)
                throw new GrassCoreException($"此电脑无法使用硬件虚拟化（WHPX）：{whpx.UserGuidance}");
        }

        }
        catch
        {
            @lock.Release();
            lockAcquired = false;
            throw;
        }

        Process? proc = null;
        RuntimeSession? startedSession = null;
        bool registered = false;
        try
        {
            var sessionId = Guid.NewGuid().ToString("N")[..12];
            var spicePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var cmd = new QemuCommandBuilder(config, _ovmfDir).Build(pkg.Path, sessionId, suspendFile, spicePassword);
            var session = new RuntimeSession
            {
                SessionId = sessionId,
                QmpPipe = cmd.QmpPipeName,
                StartedAt = DateTimeOffset.UtcNow,
                QemuMajorAtStart = _bundledQemuMajor,
                MachineId = MachineId,
                QemuExecutablePath = _qemuSystemPath,
                CommandLineFingerprint = CommandFingerprint(cmd),
                SpicePassword = spicePassword,
            };
            startedSession = session;
            Directory.CreateDirectory(pkg.RuntimePath);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());
            proc = _launcher.Start(cmd);
            session.QemuPid = proc.Id;
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());

            var vm = new RunningVm(pkg.Path, session, proc, @lock) { ResumedFromSuspend = true };
            vm.Qmp = TryConnectQmp(session.QmpPipe);
            if (vm.Qmp is null)
                throw new GrassCoreException("QMP 控制通道未就绪，虚拟机恢复已回滚，请稍后重试。");
            StartHelper(pkg, session, vm.Qmp);
            AtomicFile.WriteJsonValidated(pkg.SessionPath, session.Serialize());
            _running[pkg.Path] = vm;
            registered = true;
            WatchQemuProcess(vm);

            // 恢复确认：-incoming 迁移完成（query-status=running）后才清除挂起标记。
            // 不设时限：大内存 + 慢存储的迁移可能远超几分钟——超时放弃的话，迁移稍后
            // 完成、客户机开始写盘，而标记还在 = 之后把旧 RAM 重放到新磁盘（挂起语义
            // 要防的损坏）。循环在 running 确认或进程退出时结束（QMP 连不上的重试
            // 逐渐退避，避免对死管道高频敲门）。
            _ = Task.Run(async () =>
            {
                try
                {
                    var qmpMisses = 0;
                    while (true)
                    {
                        if (vm.Process.HasExited)
                        {
                            // -incoming 之后进程退出：客户机可能已经跑过并写过磁盘，
                            // 旧内存快照不再匹配磁盘——留着它 = 下次恢复把旧 RAM 重放到
                            // 新磁盘上。作废状态。
                            // 例外：用户在确认窗口里挂起了——退出是【合法的新挂起】，
                            // suspend.state 是刚保存的新状态（挂起流程写标记前会清
                            // ResumedFromSuspend）。作废它 = 用户 RAM 凭空消失
                            if (!vm.ResumedFromSuspend) return;
                            break;
                        }
                        QmpClient? q;
                        if (vm.Qmp is not null)
                        {
                            q = vm.Qmp;
                        }
                        else if (qmpMisses % 60 == 0) // 每 ~30 秒重试一次连接
                        {
                            q = TryConnectQmp(session.QmpPipe);
                        }
                        else
                        {
                            q = null;
                        }
                        if (q is not null)
                        {
                            vm.Qmp = q;
                            qmpMisses = 0;
                            if (await q.VmStatusAsync() == "running")
                            {
                                // 与进程退出分支同一道防线：响应在途期间用户可能
                                // 已挂起（新状态刚落盘、ResumedFromSuspend 已被挂起
                                // 流程复位）——此刻清标记/删状态文件 = 把用户刚
                                // 保存的新 RAM 状态静默销毁。
                                // 清尾整体放包级门内 + 门内复核标志：标志检查与
                                // 落盘之间的窗口（GC 暂停/线程冻结足以撕开）里
                                // 一次并发挂起会写同一份 state.json/同一个
                                // suspend.state——不互斥就被这里静默抹掉
                                if (!vm.ResumedFromSuspend) return;
                                WithPackageGate(pkg.Path, () =>
                                {
                                    if (!vm.ResumedFromSuspend) return true; // 挂起已完整接管，新状态归它
                                    ClearResumedSuspendMarker(pkg, suspendFile);
                                    return true;
                                });
                                return;
                            }
                        }
                        else
                        {
                            qmpMisses++;
                        }
                        await Task.Delay(500);
                    }
                    // 确认循环超时兜底：同样走门 + 标志复核（并发挂起的窗口不分分支）
                    WithPackageGate(pkg.Path, () =>
                    {
                        if (!vm.ResumedFromSuspend) return true;
                        ClearResumedSuspendMarker(pkg, suspendFile);
                        return true;
                    });
                }
                catch
                {
                    // 确认失败且未能判定：退出路径的兜底（ResumedFromSuspend 标记 +
                    // WatchQemuProcess）仍会处理；这里保留标记不冒险
                }
            });
            return new { qemuPid = proc.Id, resumed = true };
        }
        catch
        {
            // 失败回滚与 StartVm 同规则：只清理本次调用获得的资源；
            // 仅在确实注册进 _running 后才移除（别人的运行实例绝不动）
            if (registered) _running.TryRemove(pkg.Path, out _);
            if (lockAcquired) RollbackStart(pkg, proc, startedSession, @lock);
            throw;
        }
    }

    /// <summary>
    /// 恢复确认后的旧挂起状态清尾。只允许在包级门【内】、且门内复核
    /// <c>ResumedFromSuspend</c> 仍为 true 后调用：并发挂起写的是同一份
    /// state.json 与同一个 suspend.state 文件，不互斥的清尾会把它静默抹掉。
    /// </summary>
    private static void ClearResumedSuspendMarker(GrassVmPackage pkg, string suspendFile)
    {
        var st = VmState.Load(pkg);
        st.SuspendedStatePath = null;
        st.SuspendFingerprint = null;
        st.Save(pkg);
        try { File.Delete(suspendFile); } catch { /* 空间回收失败不致命 */ }
    }

    /// <summary>
    /// QEMU 进程退出监视：ACPI 关机/quit/客户机内关机最终都表现为进程退出。
    /// 在这里做干净关机记账：清空 runtime/、释放 vm.lock、
    /// 升级保护快照满 24 小时后自动删除（§22.3）。
    /// </summary>
    private void WatchQemuProcess(RunningVm vm)
    {
        var cleanupStarted = 0;
        void HandleExit()
        {
            try
            {
                // 挂起流程正在监督退出（它持有包门、自己收尾放锁）：这里让位，
                // 否则同步派发时自死锁、异步派发时抢放锁
                if (vm.ExitCleanupSuppressed) return;
                // 误报防线：Exited 触发但 PID 还在【跑】（且还是 QEMU）= 事件系统误报，
                // 绝不能放锁/清 runtime——那会让第二台 QEMU 拿到同一块盘的双写权。
                // 必须同时看 HasExited：Windows 上本组件持有的句柄会让刚退出的进程
                // 在进程表里"僵尸式存活"（GetProcessById 成功、ProcessName 还是
                // qemu-system-x86_64）——不看 HasExited 会把每一次真实退出都当成
                // 误报跳过收尾，vm.lock 永不释放、Core 永不空闲退出
                try
                {
                    var still = System.Diagnostics.Process.GetProcessById(vm.Session.QemuPid);
                    if (!still.HasExited
                        && still.ProcessName.Contains("qemu", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine(
                            $"[grass] QEMU 退出监视误报（pid={vm.Session.QemuPid} 仍在运行），跳过收尾。");
                        return;
                    }
                }
                catch (ArgumentException) { /* PID 确实没了：正常路径 */ }
                catch (InvalidOperationException) { /* 同上 */ }

                // 只有确认进程确实退出后才消费这次收尾机会。事件系统偶发误报
                // 时必须保留后续真实 Exited 事件，否则锁和 runtime 将永久残留。
                if (Interlocked.Exchange(ref cleanupStarted, 1) != 0) return;

                if (!_running.Remove(vm.PackagePath, out var removed)) return; // 已被挂起/强制路径处理
                removed.Qmp?.Dispose();
                var pkg = new GrassVmPackage(vm.PackagePath);
                // 挂起恢复的 VM 退出时挂起标记还在（确认循环没能确认 running）：
                // 客户机可能已跑过并写过磁盘，保存的内存状态不可再重放——作废。
                // 这是确认循环（无时限轮询）之外的最后兜底：QMP 全程不可用 + 进程退出
                // 的组合下，循环里的 break 分支可能没来得及清标记
                if (vm.ResumedFromSuspend)
                {
                    var st = VmState.Load(pkg);
                    if (st.SuspendedStatePath is not null)
                    {
                        var sf = PathPolicy.Resolve(pkg, st.SuspendedStatePath);
                        st.SuspendedStatePath = null;
                        st.SuspendFingerprint = null;
                        st.Save(pkg);
                        try { if (File.Exists(sf)) File.Delete(sf); } catch { /* 回收失败不致命 */ }
                    }
                }
                // 收尾（升级保护清理 + runtime 清空 + 放锁）全部进包级互斥门：
                // 退出瞬间 UI 就显示"已关机"，此刻的 解除锁定→启动 / 快照 RPC 都
                // 拿得到空闲的门。门外的清理（尤其几分钟的 qemu-img commit + NAS/
                // 杀毒拖长的 runtime 删除）会在等门用户操作【之后】醒来，把新会话
                // 刚写好的 session.json 删掉、把新会话的活锁放掉——双写者灾难正是
                // vm.lock 要防的事。门内复核运行状态 + 会话身份，都不是本会话就不动。
                // 必须挪到线程池跑：WaitForExit（挂起流程）会在【持门的调用线程】上
                // 同步派发 Exited——同一根线程再进 SemaphoreSlim = 自死锁（实测挂死）
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        WithDiskJob(() => WithPackageGate(vm.PackagePath, () =>
                        {
                            if (_running.ContainsKey(vm.PackagePath)) return; // 等门期间已被再次启动
                            // 双保险：runtime 里的 session 不是本会话 = 有人已解锁并启动过
                            //（新会话可能又已退出）——残局属于别人，不碰
                            try
                            {
                                if (File.Exists(pkg.SessionPath)
                                    && RuntimeSession.Deserialize(File.ReadAllText(pkg.SessionPath)) is { } nowSession
                                    && nowSession.SessionId != vm.Session.SessionId)
                                    return;
                            }
                            catch { /* 读不了就按本会话处理（正常路径） */ }
                            // 正常关机 = 我们发出过 ACPI 电源按钮请求后的退出。
                            // 强制关机（quit）/崩溃/宿主断电都不算——升级保护快照只在真正
                            // 正常关机后才开始 24h 计时
                            if (vm.AcpiShutdownRequested)
                                TryDeleteExpiredUpgradeProtection(pkg);
                            StopHelper(vm.Session);
                            // 正常退出路径：清空 runtime/ 并删除 vm.lock
                            if (Directory.Exists(pkg.RuntimePath))
                                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                            removed.Lock.Release();
                        }));
                    }
                    catch
                    {
                        // 收尾失败：残留锁由下次启动预检诊断（与旧的同步路径语义一致）
                    }
                });
            }
            catch
            {
                // 监视失败不影响 QEMU 已退出的事实；下次启动预检会给出残留锁诊断
            }
        }

        // 必须先订阅再开启事件。反过来时，QEMU 若正好在两句之间退出，
        // Exited 只发一次而无人接收，_running/runtime/vm.lock 会永久残留。
        vm.Process.Exited += (_, _) => HandleExit();
        vm.Process.EnableRaisingEvents = true;
        // Process 在订阅前就已退出时，部分平台不会补发事件；主动复核并走同一个
        // 幂等处理器。与事件并发时 cleanupStarted 保证只收尾一次。
        try
        {
            if (vm.Process.HasExited) HandleExit();
        }
        catch (InvalidOperationException)
        {
            HandleExit();
        }
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
                // 对应的元数据备份一并回收（temp/upgrade-backup-* 不再无限累积）
                var backupDir = s.UpgradeProtectionBackupDir;
                if (backupDir is not null
                    && Path.GetFullPath(backupDir).StartsWith(Path.GetFullPath(pkg.TempPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && Directory.Exists(backupDir))
                    Directory.Delete(backupDir, recursive: true);
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
        var scanner = new CoreCrashRecovery(sessionValidator: s => s.MachineId == MachineId);
        foreach (var result in scanner.ScanAdoptable(_db.LibraryRoot ?? string.Empty))
        {
            try
            {
                var pkg = result.Package;
                var session = result.Session;
                // 归属判定：别机的会话一概不动（库可能在 NAS/同步盘上；别机 PID 在本机
                // 必然"不存在"，按死了处理会解锁别人正在运行的 VM）。
                // 无 MachineId 的旧/手工会话同样保守跳过：分不清归属时宁可留给
                // 用户手动解锁（vm.lock 永不自动清除的不变式）
                if (session.MachineId is null || session.MachineId != MachineId)
                    continue;
                // PID 未写入（Core 在 Start 与回写 session 之间崩溃）≠ 死了。
                // 先依据持久化的启动意图寻找候选 QEMU，随后仍必须完成 QMP 握手才接管；
                // 找不到或无法验证时保留锁和 runtime，绝不把不确定现场当尸体清理。
                // 一切读改写都在包级互斥门内做：扫描快照可能在门排队期间过期——
                // 两次并发 adopt（Core 启动 + UI 重连各发一次）交错时，无门的清理
                // 会按【旧快照】删掉新会话刚写好的 session.json、放掉活锁。
                // 门内先重读 session 再判死活，扫描结果只当"候选线索"
                WithPackageGate(pkg.Path, () =>
                {
                    var fresh = RuntimeSession.Deserialize(File.ReadAllText(pkg.SessionPath));
                    if (fresh is null || fresh.SessionId != session.SessionId)
                        return; // 会话已被替换（有人刚启动了它）——快照过期，不动
                    if (fresh.MachineId is null || fresh.MachineId != MachineId)
                        return;
                    // QMP 管道必须由本会话 ID 确定生成，不能接受 session.json 被
                    // 篡改后指向另一台 VM 的任意管道；握手本身只能证明“某个 QEMU”存在。
                    var expectedQmpPipe = $@"\\.\pipe\grassvm-qmp-{fresh.SessionId}";
                    if (!string.Equals(fresh.QmpPipe, expectedQmpPipe, StringComparison.OrdinalIgnoreCase))
                        return;
                    // 幂等（先于任何新连接！）：UI 每次重启都会对还活着的 Core 重发
                    // adoptRunningVms。已在 _running 里的 VM 直接跳过，避免为重复接管
                    // 新建 QMP 连接后才发现 VM 已经登记。
                    if (_running.ContainsKey(pkg.Path))
                    {
                        adopted.Add(pkg.Path);
                        return;
                    }
                    // 存活判定与扫描器同语义（GetProcessById 成功 = 活着）：不要用
                    // HasExited——非本组件启动的进程在部分平台上会误报"已退出"，
                    // 把活着的 QEMU 当尸体清掉 runtime/放锁
                    System.Diagnostics.Process? proc = null;
                    QmpClient? candidateQmp = null;
                    try { if (fresh.QemuPid > 0) proc = System.Diagnostics.Process.GetProcessById(fresh.QemuPid); }
                    catch (ArgumentException) { proc = null!; }
                    catch (InvalidOperationException) { proc = null!; }
                    if (proc is null && fresh.QemuPid <= 0)
                    {
                        // Core 在“启动进程后、回写 PID 前”崩溃时可能同时有多台同版本
                        // QEMU。仅凭同一路径 + 启动时间无法把 QMP 管道归属到某个 PID；
                        // 在无法取得唯一候选时宁可保留现场，绝不把第一个进程错绑到本会话。
                        var candidates = FindQemuForLaunchIntent(fresh);
                        if (candidates.Count == 1)
                        {
                            proc = candidates[0];
                            candidateQmp = TryConnectQmp(fresh.QmpPipe);
                            if (candidateQmp is null) { proc.Dispose(); proc = null; }
                        }
                        else foreach (var candidate in candidates) candidate.Dispose();
                    }
                    if (proc is null)
                    {
                        // 没有可验证的候选进程：启动窗口可能仍在进行，保持现场供下一轮接管。
                        if (fresh.QemuPid <= 0 && fresh.QemuExecutablePath is not null)
                            return;
                        // QEMU 已不在（比如 Core 崩溃期间客户机内正常关机）：做干净收尾
                        InvalidateStaleSuspendMarker(pkg, fresh);
                        StopHelper(fresh);
                        if (Directory.Exists(pkg.RuntimePath))
                            foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                        new VmLock(pkg).Release();
                        dead.Add(pkg.Path);
                        return;
                    }
                    // PID 复用防护：进程名必须还是 QEMU（否则是别人复用了 PID——把它当 QEMU 接管
                    // 会在错误进程退出时误清 runtime/误放锁）
                    if (!proc.ProcessName.Contains("qemu", StringComparison.OrdinalIgnoreCase))
                    {
                        InvalidateStaleSuspendMarker(pkg, fresh);
                        StopHelper(fresh);
                        if (Directory.Exists(pkg.RuntimePath))
                            foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
                        new VmLock(pkg).Release();
                        dead.Add(pkg.Path);
                        return;
                    }
                    // 候选进程必须能通过持久化 QMP 管道握手；否则不能确认它属于本会话。
                    candidateQmp ??= TryConnectQmp(fresh.QmpPipe);
                    if (candidateQmp is null)
                    {
                        // PID/进程名只能缩小候选范围，不能证明管道属于本会话；
                        // QMP 握手失败时一律保留现场，避免误接管/误释放锁。
                        return;
                    }
                    else
                    {
                        candidateQmp.Dispose();
                        if (fresh.QemuPid <= 0)
                        {
                            fresh.QemuPid = proc.Id;
                            AtomicFile.WriteJsonValidated(pkg.SessionPath, fresh.Serialize());
                        }
                    }
                // 接管活 QEMU 时必须同时接管 vm.lock 的排他句柄；只记录路径而不持有
                // 句柄会让后续 ReleaseVm/退出监视无法释放本进程自己的锁。
                var adoptedLock = new VmLock(pkg);
                if (!adoptedLock.TryAcquireExisting())
                {
                    proc.Dispose();
                    return;
                }
                var registered = false;
                RunningVm? adoptedVm = null;
                try
                {
                    var vm = new RunningVm(pkg.Path, fresh, proc, adoptedLock);
                    adoptedVm = vm;
                    vm.Qmp = TryConnectQmp(fresh.QmpPipe);
                    // Core 重启后内存中的 Helper 令牌已经丢失，旧 Helper 不能安全复用。
                    // 先结束旧实例，再以新令牌重新握手；QEMU 本体不受影响。
                    if (vm.Qmp is not null)
                    {
                        // 旧 Core 的 ticket 只存在于其内存，接管后无法复用。通过已验证
                        // 的 QMP 通道立刻轮换为新 ticket；失败则保持未知凭据并继续接管，
                        // GetDisplayInfo 会要求重启而不会暴露未认证 SPICE。
                        var adoptedSpicePassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                        try
                        {
                            vm.Qmp.SetSpicePasswordAsync(adoptedSpicePassword).GetAwaiter().GetResult();
                            vm.Session.SpicePassword = adoptedSpicePassword;
                        }
                        catch { vm.Session.SpicePassword = null; }
                        StopHelper(fresh);
                        fresh.HelperPid = null;
                        fresh.HelperPort = null;
                        StartHelper(pkg, fresh, vm.Qmp);
                        AtomicFile.WriteJsonValidated(pkg.SessionPath, fresh.Serialize());
                    }
                    // 挂起标记 + 活着的 QEMU = 上次 Core 在"标记已落盘、quit 未送达"窗口崩溃。
                    // 原进程仍握有完整状态：直接 cont 让它继续跑，清掉标记并回收 suspend.state
                    // （否则库列表显示"已挂起"，之后"恢复"会把旧内存重放到已前进的磁盘上）。
                    var staleState = VmState.Load(pkg);
                    if (staleState.SuspendedStatePath is not null)
                    {
                        // QMP 不可达时空过整个 try（?. 短路）会把标记当"已恢复"清掉、
                        // 删掉唯一的保存状态，而 QEMU 可能永远停在 paused。必须有真实连接
                        // 且 cont 真正送达，才有资格动标记。
                        if (vm.Qmp is not null)
                        {
                            try
                            {
                                vm.Qmp.ExecuteAsync("cont").GetAwaiter().GetResult();
                                var suspendFile = PathPolicy.Resolve(pkg, staleState.SuspendedStatePath);
                                staleState.SuspendedStatePath = null;
                                staleState.SuspendFingerprint = null;
                                staleState.Save(pkg);
                                if (File.Exists(suspendFile)) File.Delete(suspendFile);
                            }
                            catch
                            {
                                // cont 送不出去：保守保留标记与状态文件（用户仍可恢复/手动
                                // 处理）。但有一种形态必须防住——客户机其实已经在跑
                                //（-incoming 迁移完成、Core 死在确认循环之前）：对运行中的
                                // VM 发 cont 会报错走到这里，标记却还挂着。给这台 VM 打上
                                // "恢复待确认"标记：它退出时退出监视会作废这份旧状态——
                                // 否则之后"恢复"= 把旧 RAM 重放到已被写过的磁盘（静默损毁）
                                vm.ResumedFromSuspend = true;
                            }
                        }
                        else
                        {
                            // 连 QMP 都连不上：同样无法证明客户机没跑过。同样的防线
                            vm.ResumedFromSuspend = true;
                        }
                    }
                    _running[pkg.Path] = vm;
                    WatchQemuProcess(vm);
                    registered = true;
                    adopted.Add(pkg.Path);
                }
                finally
                {
                    if (!registered)
                    {
                        _running.TryRemove(pkg.Path, out _);
                        adoptedVm?.Qmp?.Dispose();
                        if (adoptedVm is not null) StopHelper(adoptedVm.Session);
                        adoptedLock.Release();
                    }
                }
                });
            }
            catch
            {
                // 单台接管失败不影响其他；预检/手动解锁兜底
            }
        }
        return new { adopted, cleanedUp = dead };
    }

    /// <summary>
    /// 名称同时是包目录名与 QEMU -name 的值：文件系统非法字符、逗号（QemuOpts
    /// 分隔符）、等号（-name 会按 key=value 解析，未知键让 QEMU 启动即退）都不行
    /// </summary>
    internal static bool IsInvalidVmName(string name) =>
        string.IsNullOrWhiteSpace(name)
        || name.StartsWith('.')
        || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || name.Contains(',')
        || name.Contains('=');

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            using (p)
            {
                return !p.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsHelperProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.Contains("GrassSpiceHelper", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string CurrentHostFingerprint()
    {
        // CPU 型号/厂商 + 运行时可见的 x86 feature flags 入指纹：-cpu max 暴露宿主
        // 特性，同核数但指令集不同的 Intel/AMD 之间不保证可回放。宿主 OS 版本
        // 【不】入指纹，避免 Windows 功能更新把仍可用的挂起机全部判成不兼容。
        var features = string.Join(';',
            $"x86={System.Runtime.Intrinsics.X86.X86Base.IsSupported}",
            $"sse={System.Runtime.Intrinsics.X86.Sse.IsSupported}",
            $"sse2={System.Runtime.Intrinsics.X86.Sse2.IsSupported}",
            $"sse3={System.Runtime.Intrinsics.X86.Sse3.IsSupported}",
            $"ssse3={System.Runtime.Intrinsics.X86.Ssse3.IsSupported}",
            $"sse41={System.Runtime.Intrinsics.X86.Sse41.IsSupported}",
            $"sse42={System.Runtime.Intrinsics.X86.Sse42.IsSupported}",
            $"avx={System.Runtime.Intrinsics.X86.Avx.IsSupported}",
            $"avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}",
            $"fma={System.Runtime.Intrinsics.X86.Fma.IsSupported}",
            $"bmi1={System.Runtime.Intrinsics.X86.Bmi1.IsSupported}",
            $"bmi2={System.Runtime.Intrinsics.X86.Bmi2.IsSupported}",
            $"aes={System.Runtime.Intrinsics.X86.Aes.IsSupported}",
            $"pclmul={System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported}");
        var featureHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(features))).ToLowerInvariant();
        return $"{Environment.ProcessorCount}cpus|{_bundledQemuMajor}"
            + $"|{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown-cpu"}"
            + $"|{Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "unknown-arch"}"
            + $"|{Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? ""}"
            + $"|{Environment.GetEnvironmentVariable("PROCESSOR_LEVEL") ?? ""}"
            + $"|{Environment.GetEnvironmentVariable("PROCESSOR_REVISION") ?? ""}"
            + $"|{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"
            + $"|features={featureHash}";
    }

    private QmpClient? TryConnectQmp(string pipeName)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            QmpClient? client = null;
            try
            {
                var transport = _qmpTransportFactory(pipeName);
                if (transport is null) return null;
                client = new QmpClient(transport);
                Task.Run(() => client.ConnectAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                return client;
            }
            catch
            {
                client?.Dispose();
                if (attempt < 11) Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1000, 100 + attempt * 100)));
            }
        }
        return null;
    }

    private QmpClient RequireQmp(string packagePath)
    {
        if (!_running.TryGetValue(packagePath, out var vm))
            throw new GrassCoreException("此虚拟机没有在运行。");
        // 死连接要替换：QMP 管道中途断掉（对端重置/读循环退出）后 vm.Qmp 还是那个
        // 已 Dispose 的实例——只补 null 的话，shutdown/suspend/forceOff 从此全部
        // 抛"连接已关闭"直到 QEMU 自己退出（最坏：挂起中途 stop 后既不能继续也不
        // 能强制关机）
        if (vm.Qmp is { IsDisposed: true })
        {
            try { vm.Qmp.Dispose(); } catch { /* 已关 */ }
            vm.Qmp = null;
        }
        // QMP 可能在启动窗口内未就绪：后续操作在这里重试连接
        vm.Qmp ??= TryConnectQmp(vm.Session.QmpPipe);
        return vm.Qmp ?? throw new GrassCoreException("QMP 控制通道不可用（QEMU 可能仍在启动），请稍后重试。");
    }

    public object PowerAction(string packagePath, string action)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => PowerActionCore(managed, action));
    }

    private object PowerActionCore(string packagePath, string action)
    {
        switch (action)
        {
            // 正常关机 = ACPI 电源按钮请求（QGA/ACPI）；不是 kill process。
            // 只有这条路径标记为"正常关机"信号（升级保护快照的 24h 计时以此为前提）。
            case "shutdown":
            {
                var qmp = RequireQmp(packagePath);
                if (!_running.TryGetValue(packagePath, out var vm))
                    throw new GrassCoreException("虚拟机已不在运行。");
                // 标志先于发送：客户机可能在响应到达前就完成关机退出（监视器会读它）。
                // 发送失败必须复位——否则这台 VM 之后 quit（用户强制/崩溃）会被
                // 监视器误判成"正常关机"，升级保护快照被提前删除
                vm.AcpiShutdownRequested = true;
                try
                {
                    var r = qmp.AcpiShutdownAsync().GetAwaiter().GetResult();
                    return QmpResult(r);
                }
                catch
                {
                    vm.AcpiShutdownRequested = false;
                    throw;
                }
            }
            // 强制关机 = 电源菜单 + 二次确认后调用方才允许（不算正常关机）。
            // 清掉 ACPI 标记：此前被忽略的关机请求不算数——强退被计成"正常关机"会让
            // 升级保护快照提前进入 24h 删除计时
            case "forceOff":
            {
                if (_running.TryGetValue(packagePath, out var fo))
                    fo.AcpiShutdownRequested = false;
                return QmpResult(RequireQmp(packagePath).ForceQuitAsync().GetAwaiter().GetResult());
            }
            // 挂起 = 保存完整运行状态后完全退出 QEMU（不是 pause；1.0 无"暂停"）。
            // 直接调内部实现：PowerAction 已持有该包的门，再进公共 SuspendVm 会同线程重入死锁。
            // 挂起阶段照样上报（大内存 VM 的保存可达数分钟）
            case "suspend":
                return WithLifecyclePhase(packagePath, "suspending", () => SuspendVmCore(packagePath));
            default:
                throw new GrassCoreException($"未知电源动作：{action}");
        }
    }

    private static object QmpResult(System.Text.Json.JsonElement e) => new { sent = true, result = e.ToString() };

    private object SuspendVm(string packagePath) =>
        WithPackageGate(packagePath, () =>
            WithLifecyclePhase(packagePath, "suspending", () => SuspendVmCore(packagePath)));

    private object SuspendVmCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var qmp = RequireQmp(packagePath);
        // 挂起序列：stop（稳定点）→ migrate file:（保存内存/CPU/设备状态）→ 轮询至完成 → quit。
        // migrate 命令在迁移【开始】时即返回；大内存 VM 需要真实等待。
        try
        {
            qmp.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // stop 可能已生效只是回包丢了（连接在收发之间断开）——尽力恢复运行；
            // cont 送不出去时抛带指引的话术（裸 IO 异常只会让用户一头雾水）
            try { qmp.ExecuteAsync("cont").GetAwaiter().GetResult(); }
            catch
            {
                throw new GrassCoreException(
                    "暂停虚拟机时控制通道中断，虚拟机可能仍处于暂停状态。请尝试恢复运行或强制关机。");
            }
            throw new GrassCoreException("暂停虚拟机时控制通道中断，虚拟机已恢复运行。");
        }
        // 挂起监督的武装/解除：从【migrate 开始写盘】那一刻起，退出监视器就必须
        // 让位——QEMU 若在"migrate 已完成、挂起标记还没落盘"之间被外杀/崩溃，
        // 监视器的 ResumedFromSuspend 分支会把【刚写入的】suspend.state 当作旧
        // 恢复状态删掉（RAM 状态的唯一副本凭空消失）。任何"VM 已恢复运行/
        // 挂起放弃"的失败路径都要解除，把收尾交还监视器
        RunningVm? suspendingVm = null;
        _running.TryGetValue(pkg.Path, out suspendingVm);
        var prevResumedFromSuspend = suspendingVm?.ResumedFromSuspend ?? false;
        var prevSuppressed = suspendingVm?.ExitCleanupSuppressed ?? false;
        void ArmSuspendSupervision()
        {
            if (suspendingVm is null) return;
            // 新状态即将落盘 = 本次会话不再是"待确认的恢复"；quit 不算正常关机
            //（升级保护计时只认 ACPI）；本流程接管退出收尾（持有包级门，确认
            // 退出后自己放锁——监视器插手会抢放锁/重入门自死锁）
            suspendingVm.ResumedFromSuspend = false;
            suspendingVm.AcpiShutdownRequested = false;
            suspendingVm.ExitCleanupSuppressed = true;
        }
        void DisarmSuspendSupervision()
        {
            if (suspendingVm is null) return;
            suspendingVm.ResumedFromSuspend = prevResumedFromSuspend;
            suspendingVm.AcpiShutdownRequested = false;
            suspendingVm.ExitCleanupSuppressed = prevSuppressed;
        }
        void TryCancelMigration()
        {
            // migrate_cancel 是幂等的最佳努力操作：即使 QEMU 已报告 failed/
            // cancelled 或控制通道已断开，也必须尝试终止残留迁移状态，再 cont。
            try { qmp.ExecuteAsync("migrate_cancel").GetAwaiter().GetResult(); } catch { }
        }
        bool QemuAlreadyExited()
        {
            try { return suspendingVm is not null && suspendingVm.Process.HasExited; }
            catch { return false; }
        }
        void ReleaseExitedQemu()
        {
            if (!QemuAlreadyExited()) return;
            if (suspendingVm is not null) suspendingVm.ExitCleanupSuppressed = false;
            ReleaseVm(pkg, clearRuntime: true);
        }
        ArmSuspendSupervision();
        var stateFile = Path.Combine(pkg.FirmwarePath, "..", "suspend.state");
        stateFile = Path.GetFullPath(stateFile);
        try
        {
            qmp.MigrateToFileAsync(stateFile).GetAwaiter().GetResult();
        }
        catch
        {
            // migrate 命令本身失败（QMP 错误/连接死）：VM 还冻在 stop 状态——尽力恢复运行
            TryCancelMigration();
            DisarmSuspendSupervision();
            ReleaseExitedQemu();
            if (QemuAlreadyExited()) throw new GrassCoreException("保存挂起状态时 QEMU 已退出，虚拟机已完成收尾，请重新启动。");
            try { qmp.ExecuteAsync("cont").GetAwaiter().GetResult(); } catch { /* 连接已死则无法恢复 */ }
            throw;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        try
        {
            while (true)
            {
                var status = qmp.MigrationStatusAsync().GetAwaiter().GetResult();
                if (status == "completed") break;
                if (status is "failed" or "cancelled")
                {
                    // 保存失败：恢复运行而不是把 VM 冻在 stop 状态
                    TryCancelMigration();
                    DisarmSuspendSupervision();
                    ReleaseExitedQemu();
                    if (QemuAlreadyExited())
                        throw new GrassCoreException($"保存挂起状态失败（{status}），QEMU 已退出。请重新启动虚拟机。");
                    qmp.ExecuteAsync("cont").GetAwaiter().GetResult();
                    throw new GrassCoreException($"保存挂起状态失败（{status}），虚拟机已恢复运行。");
                }
                if (DateTime.UtcNow > deadline)
                {
                    TryCancelMigration();
                    DisarmSuspendSupervision();
                    ReleaseExitedQemu();
                    if (QemuAlreadyExited())
                        throw new GrassCoreException("保存挂起状态超时且 QEMU 已退出，请重新启动虚拟机。");
                    qmp.ExecuteAsync("cont").GetAwaiter().GetResult();
                    throw new GrassCoreException("保存挂起状态超时，虚拟机已恢复运行。");
                }
                Thread.Sleep(500);
            }
        }
        catch (GrassCoreException)
        {
            throw; // 上面两条已自己 cont 过、带用户话术（Disarm 已在抛出前完成）
        }
        catch
        {
            // 轮询途中 QMP 断了（读循环自我 Dispose）：VM 冻在 stop 状态且没有
            // cont 的 RPC 入口——不恢复的话用户只剩"强制关机"一条路
            TryCancelMigration();
            DisarmSuspendSupervision();
            ReleaseExitedQemu();
            if (QemuAlreadyExited())
                throw new GrassCoreException("保存挂起状态期间 QEMU 已退出，虚拟机已完成收尾，请重新启动。");
            try { qmp.ExecuteAsync("cont").GetAwaiter().GetResult(); }
            catch
            {
                // 连接已死：RequireQmp 下次会重建，但本次无法恢复运行——
                // 抛出带指引的错（至少不是裸 IO 异常）
                throw new GrassCoreException(
                    "保存挂起状态期间控制通道中断，虚拟机可能仍处于暂停状态。请尝试恢复运行或强制关机。");
            }
            throw new GrassCoreException("保存挂起状态期间控制通道中断，虚拟机已恢复运行。");
        }

        // RAM 状态已在盘上（监视器自 migrate 起就让位，本流程全权负责）：
        // 落盘挂起标记。若 Core 在 quit 前后崩溃，VM 一致地处于"已挂起"，
        // suspend.state 不会被静默丢弃（顺序颠倒会让保存的会话凭空消失）。
        // Save 失败：什么都没持久化，解除监督交还监视器（原语义）
        var state = VmState.Load(pkg);
        state.SuspendedStatePath = PathPolicy.NormalizeReference(pkg, stateFile);
        state.SuspendFingerprint = CurrentHostFingerprint();
        try
        {
            state.Save(pkg);
        }
        catch
        {
            TryCancelMigration();
            DisarmSuspendSupervision();
            ReleaseExitedQemu();
            if (QemuAlreadyExited())
                throw new GrassCoreException("保存挂起状态期间 QEMU 已退出，虚拟机已完成收尾，请重新启动。");
            try { qmp.ExecuteAsync("cont").GetAwaiter().GetResult(); }
            catch { throw new GrassCoreException("挂起状态已保存但元数据写入失败，虚拟机可能仍处于暂停状态，请尝试强制关机。"); }
            throw new GrassCoreException("挂起状态已保存但元数据写入失败，虚拟机已恢复运行。");
        }

        try
        {
            qmp.ForceQuitAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // quit 失败 ≠ QEMU 还活着：migrate 已完成、进程可能恰好退出（死管道
            // 让 ForceQuit 抛错）。Exited 已在监督窗口里被让位、不会重发——不补
            // 这个检查，vm.lock 与运行表永久悬挂（关机/强退/解锁/接管全被挡死，
            // Core 永不空闲退出）。进程已死 = 挂起其实已成功，走确认退出路径
            var alreadyExited = false;
            try { if (suspendingVm is not null) alreadyExited = suspendingVm.Process.HasExited; }
            catch { /* 句柄查询失败按存活处理 */ }
            if (alreadyExited)
            {
                if (suspendingVm is not null) suspendingVm.ExitCleanupSuppressed = false;
                ReleaseVm(pkg, clearRuntime: false);
                return new { suspended = true, stateFile };
            }
            // 真还活着：标记回滚（QEMU 仍在运行/可控，用户可重试挂起或正常关机）；
            // 退出收尾的监督解除（进程还活着，监视器保持武装）。
            // ResumedFromSuspend 不回置 true：标记/状态文件已回滚到"未挂起"，
            // 旧恢复状态本就该作废（quit 失败 = 进程通常还活着）
            if (suspendingVm is not null) suspendingVm.ExitCleanupSuppressed = false;
            state.SuspendedStatePath = null;
            state.SuspendFingerprint = null;
            try { state.Save(pkg); }
            finally
            {
                try { qmp.ExecuteAsync("cont").GetAwaiter().GetResult(); }
                catch { throw new GrassCoreException("挂起确认失败且虚拟机无法恢复运行，请使用强制关机处理。"); }
            }
            // 标记已回滚 = 这个 suspend.state 是本次失败挂起写下的孤儿文件（RAM
            // 大小，没人会再引用）——当场删，别留着变成导出档案里的残渣
            try { if (File.Exists(stateFile)) File.Delete(stateFile); } catch { /* 占用：留给启动清理 */ }
            throw;
        }

        // quit 只是 QEMU 的确认——退出是异步的。vm.lock 的契约是"锁在 = 被占用"，
        // 立刻放锁会让快速重启的 StartVm 在 QEMU 还在冲刷磁盘时拿锁竞速。有界等它
        // 退完；超时仍活着就【不放锁也不摘表】——此刻放锁 = 锁没了但磁盘映像还被
        // 一个活进程持有，下一次 StartVm 拿锁成功就是双写者。解除监督、把收尾
        // 交还退出监视器，等进程真正死亡时做全套（放锁 + 清 runtime）
        if (_running.TryGetValue(pkg.Path, out var suspending))
        {
            var exited = false;
            try { exited = suspending.Process.WaitForExit(10_000); }
            catch { exited = true; /* 已退出/监视器竞争：按已退处理 */ }
            if (!exited)
            {
                suspending.ExitCleanupSuppressed = false; // 监视器重新接管收尾
                // 复核：QEMU 可能恰好在超时边界退出，而已派发的 Exited 在标志复位
                // 【之前】读到抑制让位返回——事件不会重发，从此谁也不收尾 =
                // vm.lock/运行表永久悬挂。此刻进程已死就直接走确认退出路径
                try
                {
                    if (suspending.Process.HasExited)
                    {
                        ReleaseVm(pkg, clearRuntime: false);
                        return new { suspended = true, stateFile };
                    }
                }
                catch { /* 句柄查询失败：按未退出处理，监视器接管 */ }
                return new
                {
                    suspended = true,
                    stateFile,
                    note = "QEMU 仍在退出中：已保存挂起状态，等它完全退出后虚拟机会自动回到“已关机”。",
                };
            }
        }
        if (suspendingVm is not null) suspendingVm.ExitCleanupSuppressed = false;
        ReleaseVm(pkg, clearRuntime: false); // 挂起后退出 QEMU，vm.lock 释放；suspend.state 在包内
        return new { suspended = true, stateFile };
    }

    private void ReleaseVm(GrassVmPackage pkg, bool clearRuntime = true)
    {
        if (_running.TryRemove(pkg.Path, out var vm))
        {
            vm.Qmp?.Dispose();
            StopHelper(vm.Session);
            // 正常关机后清空 runtime/ 并删除 vm.lock；挂起路径保留 runtime 记录片刻（session 已写入 state）
            if (clearRuntime && Directory.Exists(pkg.RuntimePath))
                foreach (var f in Directory.EnumerateFiles(pkg.RuntimePath)) File.Delete(f);
            vm.Lock.Release();
        }
    }

    private static void StopHelper(RuntimeSession? session)
    {
        if (session is null) return;
        if (session.HelperPid is not int pid || pid <= 0) return;
        try
        {
            using var helper = Process.GetProcessById(pid);
            if (!helper.ProcessName.Contains("GrassSpiceHelper", StringComparison.OrdinalIgnoreCase)) return;
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                helper.WaitForExit(2000);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// 启动每 VM 独立的 Helper，并完成一次性令牌握手。Helper 是增强能力进程，
    /// 启动失败不能回滚 QEMU；只有握手成功才把 PID/端口写入 session.json。
    /// </summary>
    private void StartHelper(GrassVmPackage package, RuntimeSession session, QmpClient qmp)
    {
        session.HelperPid = null;
        session.HelperPort = null;
        if (string.IsNullOrWhiteSpace(_helperExecutablePath)
            || !File.Exists(_helperExecutablePath)) return;

        var port = GetFreeLoopbackPort();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Process? helper = null;
        try
        {
            var spicePort = qmp.QuerySpicePortAsync().GetAwaiter().GetResult();
            var psi = new ProcessStartInfo
            {
                FileName = _helperExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--token");
            psi.ArgumentList.Add(token);
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(session.SessionId);
            psi.ArgumentList.Add("--spice-port");
            psi.ArgumentList.Add(spicePort.ToString());
            helper = Process.Start(psi);
            if (helper is null) return;
            _ = DrainHelperOutputAsync(helper.StandardOutput, package);
            _ = DrainHelperOutputAsync(helper.StandardError, package);
            if (!ProbeHelper(port, token, session.SessionId))
            {
                StopHelperPid(helper.Id);
                return;
            }
            session.HelperPid = helper.Id;
            session.HelperPort = port;
        }
        catch
        {
            try
            {
                if (helper is { HasExited: false })
                {
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(2000);
                }
            }
            catch { }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static bool ProbeHelper(int port, string token, string sessionId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None)
                    .Wait(TimeSpan.FromMilliseconds(250));
                if (socket.State != WebSocketState.Open) throw new IOException();
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    jsonrpc = "2.0", id = 1, method = "hello",
                    @params = new { token, sessionId },
                });
                socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                var buffer = new byte[64 * 1024];
                var result = socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(buffer[..result.Count]);
                return doc.RootElement.TryGetProperty("result", out var r)
                    && r.TryGetProperty("sessionId", out var sid)
                    && sid.GetString() == sessionId;
            }
            catch { Thread.Sleep(50); }
        }
        return false;
    }

    private static async Task DrainHelperOutputAsync(StreamReader reader, GrassVmPackage package)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var path = Path.Combine(package.LogsPath, "helper.log");
                try
                {
                    Directory.CreateDirectory(package.LogsPath);
                    File.AppendAllText(path, LogRedactor.Redact(line) + Environment.NewLine);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void StopHelperPid(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var helper = Process.GetProcessById(pid);
            if (!helper.ProcessName.Contains("GrassSpiceHelper", StringComparison.OrdinalIgnoreCase)) return;
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
                helper.WaitForExit(2000);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public object UnlockVm(string packagePath) =>
        WithPackageGate(packagePath, () => UnlockVmCore(packagePath));

    private object UnlockVmCore(string packagePath)
    {
        // 调用方必须已取得用户的风险确认（UI 弹窗）。Core 仍然自己执法：
        // 本实例在跑的 VM 绝不允许解锁（并发 start 已拿锁后，过期 UI 卡片的解锁请求
        // 会把活锁删掉 → 第二个 QEMU 打开同一张盘 = vm.lock 要防的事故本身）
        var pkg = new GrassVmPackage(packagePath);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，不能解锁。请先正常关机。");
        new VmLock(pkg).ForceUnlockByUser();
        return new { unlocked = true };
    }

    /// <summary>
    /// 放弃保存的挂起状态（用户明确确认后）：清标记、删 suspend.state。
    /// 挂起指纹不匹配（宿主 CPU 变更等）时这是唯一出路——没有它，挂起机永远
    /// 无法启动/恢复/修改/导出/删除（每条路都被"先恢复并正常关机"挡住）。
    /// 代价：挂起瞬间之后的内存状态丢弃（磁盘数据完好，从盘冷启动）。
    /// </summary>
    public object DiscardSuspendState(string packagePath) =>
        WithPackageGate(packagePath, () => DiscardSuspendStateCore(packagePath));

    private object DiscardSuspendStateCore(string packagePath)
    {
        var pkg = new GrassVmPackage(packagePath);
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，不能放弃保存的状态。");
        // 放弃挂起状态本身允许目标处于 suspended，但仍必须取得跨进程锁，
        // 不能让另一 Core 同时启动恢复并读取/删除同一个 suspend.state。
        var discardLock = new VmLock(pkg);
        discardLock.Acquire();
        try
        {
            var state = VmState.Load(pkg);
            if (state.SuspendedStatePath is null)
                throw new GrassCoreException("这台虚拟机没有保存的挂起状态。");
            var suspendFile = PathPolicy.Resolve(pkg, state.SuspendedStatePath);
            state.SuspendedStatePath = null;
            state.SuspendFingerprint = null;
            state.Save(pkg);
            try { if (File.Exists(suspendFile)) File.Delete(suspendFile); }
            catch (IOException) { /* 状态文件回收失败不阻断：标记已清，残留文件随包清理 */ }
            return new { discarded = true };
        }
        finally { discardLock.Dispose(); }
    }

    /// <summary>CD/DVD 热插拔（唯一允许运行中修改的设备）：换镜像 / 弹出。</summary>
    public object ChangeMedium(string packagePath, string deviceId, string? isoPath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => ChangeMediumCore(managed, deviceId, isoPath));
    }

    private object ChangeMediumCore(string packagePath, string deviceId, string? isoPath)
    {
        var pkg = new GrassVmPackage(packagePath);
        var config = new ConfigStore(pkg).Load();
        var cd = config.Devices.OfType<CdromDevice>().SingleOrDefault(d => d.DeviceId == deviceId)
                 ?? throw new GrassCoreException("找不到这台虚拟机的 CD/DVD 设备。");
        string? resolved;
        try { resolved = isoPath is null ? null : PathPolicy.Resolve(pkg, isoPath); }
        catch (ArgumentException) { throw new GrassCoreException("光盘镜像不能通过符号链接或目录联接访问。"); }
        if (resolved is not null && (!File.Exists(resolved)
            || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(Path.GetExtension(resolved), ".iso", StringComparison.OrdinalIgnoreCase)))
            throw new GrassCoreException("光盘镜像必须是存在且可读取的普通 ISO 文件。");
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
        var managedPath = ResolveManagedPackagePath(packagePath);
        if (!_running.TryGetValue(managedPath, out var vm))
            throw new GrassCoreException("此虚拟机没有在运行。");
        var qmp = RequireQmp(managedPath);
        var port = qmp.QuerySpicePortAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(vm.Session.SpicePassword))
            throw new GrassCoreException("当前虚拟机缺少 SPICE 会话凭据，请先正常关机后重新启动。");
        return new { spicePort = port, spicePassword = vm.Session.SpicePassword };
    }

    public object SendCtrlAltDel(string packagePath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () =>
        {
            var qmp = RequireQmp(managed);
            qmp.SendCtrlAltDelAsync().GetAwaiter().GetResult();
            return new { sent = true };
        });
    }

    /// <summary>
    /// 返回客户机帮助程序的可验证状态。不存在 Helper 时明确返回 not-installed，
    /// 不把“未知”伪装成已连接；后续 Helper 握手成功后由 session.json 写入端口并返回 connected。
    /// </summary>
    public object GetHelperStatus(string packagePath)
    {
        var managedPath = ResolveManagedPackagePath(packagePath);
        if (!_running.TryGetValue(managedPath, out var vm))
            return new { state = "stopped", connected = false, reason = "vm-stopped" };
        if (string.IsNullOrWhiteSpace(_helperExecutablePath)
            || !File.Exists(_helperExecutablePath))
            return new { state = "not-installed", connected = false, reason = "helper-missing" };
        if (vm.Session.HelperPid is not int pid || !IsHelperProcessAlive(pid)
            || vm.Session.HelperPort is not int port || port is < 1 or > 65535)
            return new { state = "disconnected", connected = false, reason = "helper-disconnected" };
        // Helper 只有在自身启动并完成端点初始化后才会把 PID/端口写入会话；
        // 此处再核对进程名，避免 PID 复用把无关进程误报为 Helper。Guest Tools
        // 是否连接由显示器通过 SPICE agent 单独确认，因此 Core 可以如实报告
        // Helper 已就绪，让客户机能力正常时界面保持安静。
        return new { state = "connected", connected = true, port };
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

    public object ListNetworks() => _db.ListNetworks();

    /// <summary>读取完整配置（设置页数据源；直接返回 JSON 对象而非字符串）。</summary>
    public object GetConfig(string packagePath)
    {
        var pkg = new GrassVmPackage(ResolveManagedPackagePath(packagePath));
        var config = new ConfigStore(pkg).LoadAndUpgrade();
        return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            ConfigJson.Serialize(config));
    }

    /// <summary>
    /// 永久重定位包外资源。UI 只能把用户挑选的路径交给 Core；Core 负责校验
    /// 类型、普通文件/目录属性并原子写回 config，避免“仅本次使用”导致下一次
    /// 启动再次悬空。运行中或挂起状态不允许改写硬件引用。
    /// </summary>
    public object RelocateResource(string packagePath, string deviceId, string newPath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () =>
        {
            var pkg = new GrassVmPackage(managed);
            using var mutationLock = EnsureStoppedForMutation(pkg);
            var config = new ConfigStore(pkg).LoadAndUpgrade();
            var device = config.Devices.SingleOrDefault(d => d.DeviceId == deviceId)
                ?? throw new GrassCoreException("找不到需要重定位的设备。");
            if (string.IsNullOrWhiteSpace(newPath))
                throw new GrassCoreException("资源路径不能为空。");
            var full = Path.GetFullPath(newPath);
            var attrs = File.Exists(full) || Directory.Exists(full) ? File.GetAttributes(full) : 0;
            // 不能只看最终资源本身：符号链接父目录同样会把宿主访问解析到
            // 用户未选择的实际位置（以及在共享/NAS 路径上绕过包边界）。
            // 对不存在的路径先保留下面按设备类型给出的可读错误；存在时检查
            // 整条父目录链，和包内 PathPolicy 使用同一安全约束。
            if (attrs != 0 && ((attrs & FileAttributes.ReparsePoint) != 0
                || GrassVmPackage.ContainsReparsePoint(full)))
                throw new GrassCoreException("资源不能是符号链接或目录联接。");

            string stored;
            switch (device)
            {
                case DiskDevice disk when File.Exists(full)
                    && string.Equals(Path.GetExtension(full), ".qcow2", StringComparison.OrdinalIgnoreCase):
                    stored = PathPolicy.NormalizeReference(pkg, full);
                    disk.Path = stored;
                    break;
                case CdromDevice cd when File.Exists(full)
                    && string.Equals(Path.GetExtension(full), ".iso", StringComparison.OrdinalIgnoreCase):
                    stored = PathPolicy.NormalizeReference(pkg, full);
                    cd.IsoPath = stored;
                    break;
                case SharedFolderDevice folder when Directory.Exists(full):
                    stored = PathPolicy.NormalizeReference(pkg, full);
                    folder.HostPath = stored;
                    break;
                case DiskDevice:
                    throw new GrassCoreException("虚拟硬盘必须是存在且可读取的 QCOW2 文件。");
                case CdromDevice:
                    throw new GrassCoreException("光盘镜像必须是存在且可读取的 ISO 文件。");
                case SharedFolderDevice:
                    throw new GrassCoreException("共享文件夹必须是存在且可读取的普通目录。");
                default:
                    throw new GrassCoreException("该设备类型不支持资源重定位。");
            }
            new ConfigStore(pkg).Save(config);
            return new { relocated = true, deviceId, path = stored };
        });
    }

    /// <summary>
    /// 保存配置：仅关机状态允许（运行中唯一可改的是 CD/DVD，走 changeMedium）。
    /// 值域钳制：CPU 1..宿主核数，内存 512MB..宿主一半，磁盘/设备数量上限。
    /// </summary>
    public object UpdateConfig(string packagePath, string configJson)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => UpdateConfigCore(managed, configJson));
    }

    private object UpdateConfigCore(string packagePath, string configJson)
    {
        var pkg = new GrassVmPackage(packagePath);
        // 持有跨进程排他锁覆盖整个校验、资源检查和配置写回，避免另一 Core
        // 在停机检查后抢先启动 QEMU 或修改同一份磁盘。
        using var mutationLock = EnsureStoppedForMutation(pkg);

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
        // 上限与【已保存值】取大：资源查询失败（退回保守值）时不能把已保存的配置
        // 静默腰斩——用户改个无关设置，8GB 变 4GB 无任何提示。注意基线是盘上的
        // 旧值而不是提交值本身：拿提交值当基线，任意超额内存都能原样通过
        var savedMem = new ConfigStore(pkg).Load().MemoryMiB;
        var maxMem = Math.Max(Math.Max(512, (int)(GetTotalHostMemoryMiB() / 2)), savedMem);
        config.MemoryMiB = Math.Clamp(config.MemoryMiB, 512, maxMem);
        if (string.IsNullOrWhiteSpace(config.Name)) throw new GrassCoreException("虚拟机名称不能为空。");
        if (IsInvalidVmName(config.Name))
            throw new GrassCoreException(
                "名称包含文件系统或 QEMU 不允许的字符（含逗号、等号）。");
        var profile = OsProfileLibrary.ById(config.OsProfileId);
        if (config.Firmware.Kind != profile.Firmware)
            throw new GrassCoreException("固件类型必须与 OS Profile 保持一致。");
        if (config.Devices.Count > 16)
            throw new GrassCoreException("设备数量超出上限（16）。");
        if (!DeviceNamer.HasUniqueCreatedOrders(config))
            throw new GrassCoreException("配置包含重复的设备添加顺序，无法保存。请在设置中重新生成设备配置。");
        if (config.Devices.Select(d => d.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Devices.Count)
            throw new GrassCoreException("配置包含重复设备标识，无法保存。请在设置中重新生成设备配置。");
        if (config.Devices.OfType<NetworkDevice>().Any(n => n.Mode is NetworkMode.Bridged or NetworkMode.HostOnly))
            throw new GrassCoreException("桥接和 Host-only 网络尚未在当前版本交付，请改用 NAT 或断开模式。");

        foreach (var disk in config.Devices.OfType<DiskDevice>())
            ValidateResourcePath(pkg, disk.Path, requireFile: true, "硬盘");
        foreach (var cd in config.Devices.OfType<CdromDevice>())
            if (cd.IsoPath is not null) ValidateIsoPath(pkg, cd.IsoPath);
        foreach (var folder in config.Devices.OfType<SharedFolderDevice>())
            ValidateResourcePath(pkg, folder.HostPath, requireFile: false, "共享文件夹");

        new ConfigStore(pkg).Save(config);
        return new { saved = true, cpuCores = config.CpuCores, memoryMiB = config.MemoryMiB };
    }

    private static long GetTotalHostMemoryMiB() => Library.HostResources.TotalMemoryMiB();

    private static void ValidateIsoPath(GrassVmPackage package, string storedPath)
    {
        string path;
        try { path = PathPolicy.Resolve(package, storedPath); }
        catch (ArgumentException) { throw new GrassCoreException("光盘镜像不能通过符号链接或目录联接访问。"); }
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
            throw new GrassCoreException("光盘镜像必须是存在且可读取的普通 ISO 文件。");
    }

    private static void ValidateResourcePath(GrassVmPackage package, string storedPath, bool requireFile, string label)
    {
        string path;
        try { path = PathPolicy.Resolve(package, storedPath); }
        catch (ArgumentException) { throw new GrassCoreException($"{label}路径不存在或包含不受支持的符号链接。"); }
        var exists = requireFile ? File.Exists(path) : Directory.Exists(path);
        if (!exists || GrassVmPackage.ContainsReparsePoint(path))
            throw new GrassCoreException($"{label}路径不存在或包含不受支持的符号链接。");
    }

    /// <summary>扩容硬盘（只能扩大；qemu-img resize 事务化执行 + 魔数校验保持链完整）。</summary>
    public object ResizeDisk(string packagePath, string deviceId, long newGiB)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => ResizeDiskCore(managed, deviceId, newGiB));
    }

    private object ResizeDiskCore(string packagePath, string deviceId, long newGiB)
    {
        var pkg = new GrassVmPackage(packagePath);
        // 与快照/克隆同一套守卫：resize 是对工作盘的破坏性写（copy + 原子改名），
        // 别机正在运行这台 VM（NAS/共享库 + 残留 vm.lock）时动它 = 双写者损盘；
        // Restore 日志没收尾时动它 = 扩容结果随后被回滚成旧纪元的盘
        using var mutationLock = EnsureStoppedForMutation(pkg);
        var config = new ConfigStore(pkg).Load();
        var disk = config.Devices.OfType<DiskDevice>().SingleOrDefault(d => d.DeviceId == deviceId)
                   ?? throw new GrassCoreException("找不到这块硬盘。");
        // 与 CreateVm 同一套域钳制（"宁可钳制不可拒绝"）：设置页的 max 只是
        // 建议值，999999 这种输入要么被乘法回绕成负数、要么把荒谬尺寸交给
        // qemu-img 报原始错误
        newGiB = Math.Clamp(newGiB, 1, 2048);
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
    /// <remarks>返回的锁必须由调用方 Dispose，且覆盖整个磁盘/配置操作。</remarks>
    private VmLock EnsureStoppedForMutation(GrassVmPackage pkg)
    {
        if (_running.ContainsKey(pkg.Path))
            throw new GrassCoreException("虚拟机正在运行，关机后才能执行此操作。");
        // 先原子取得跨进程锁，再执行修复和后续磁盘操作。仅检查 vm.lock
        // 存在性会留下 TOCTOU 窗口，另一 Core 可在检查后抢先启动 QEMU。
        var mutationLock = new VmLock(pkg);
        try { mutationLock.Acquire(); }
        catch
        {
            mutationLock.Dispose();
            throw;
        }
        // 换入中断修复（幂等）：残留暂存 overlay 不清掉，CreateOverlay 会因 overwrite:false 失败。
        // Restore 日志收不了尾 = 磁盘撕裂态，变更操作（快照/克隆/删除）一律拒绝
        try
        {
            // 取得锁后重读状态，覆盖另一 Core 在初始检查之后完成挂起的窗口。
            if (VmState.Load(pkg).SuspendedStatePath is not null)
                throw new GrassCoreException("虚拟机已挂起。请先恢复并正常关机后再执行此操作。");
            if (!SnapshotService.RepairStagedOverlays(pkg, new Qemu.TransactionalDiskOps(_qemuImgPath)))
                throw new GrassCoreException("此虚拟机有一个未完成的恢复操作正在收尾（磁盘可能被占用）。请关闭占用它的程序后重试。");
            return mutationLock;
        }
        catch
        {
            mutationLock.Dispose();
            throw;
        }
    }

    public object FullClone(string packagePath, string newName)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => FullCloneCore(managed, newName));
    }

    private object FullCloneCore(string packagePath, string newName)
    {
        if (IsInvalidVmName(newName)) throw new GrassCoreException("新虚拟机名称不合法（不能含逗号、等号或文件系统不允许的字符）。");
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.FullCloneAsync(pkg, newName).GetAwaiter().GetResult();
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    public object LinkedClone(string packagePath, string snapshotUuid, string newName)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => LinkedCloneCore(managed, snapshotUuid, newName));
    }

    private object LinkedCloneCore(string packagePath, string snapshotUuid, string newName)
    {
        if (IsInvalidVmName(newName)) throw new GrassCoreException("新虚拟机名称不合法（不能含逗号、等号或文件系统不允许的字符）。");
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        var cloner = new Clone.CloneService(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var target = cloner.LinkedClone(pkg, snapshotUuid, newName);
        _db.UpsertIndex(new HostDb.VmIndexEntry(target.Path, target.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new { path = target.Path, name = target.Name };
    }

    // ---------- 导入导出（导出前必须关机）----------

    public object ExportZip(string packagePath, string zipPath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        var output = Path.GetFullPath(zipPath);
        if (output.StartsWith(managed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new GrassCoreException("导出档案必须保存到虚拟机包目录之外。");
        return WithPackageGate(managed, () => ExportZipCore(managed, output));
    }

    private object ExportZipCore(string packagePath, string zipPath)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        ExportImport.GrassVmZip.EnsureExportable(pkg, isRunning: false, lockHeld: true);
        ExportImport.GrassVmZip.Export(pkg, zipPath);
        return new { exported = zipPath };
    }

    public object ImportZip(string zipPath) =>
        WithLibraryOperation(() => ImportZipCore(zipPath));

    private object ImportZipCore(string zipPath)
    {
        var root = _db.LibraryRoot ?? throw new GrassCoreException("尚未设置虚拟机存档位置。");
        var pkg = ExportImport.GrassVmZip.Import(zipPath, root, _qemuImgPath);
        _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
        return new CreateVmResult(pkg.Path, pkg.Name);
    }

    /// <summary>OVA/OVF 导入分析：配置预览 + 磁盘清单 + 警告 + 完全无法支持的设备 + 空间预估。</summary>
    public object PlanImportOvf(string ovfOrOvaPath) =>
        WithLibraryOperation(() => PlanImportOvfCore(ovfOrOvaPath));

    private object PlanImportOvfCore(string ovfOrOvaPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "grassvm-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var importer = new ExportImport.OvfImporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
            ExportImport.OvfImporter.ImportPlan plan;
            try
            {
                // 解包（.ova 的 tar 层）也在防护内：损坏/截断/贴错扩展名的档案
                // 抛 InvalidDataException/EndOfStream——英文原文不进错误横幅。
                // 解包前先核【临时盘】空间（tar 全量解到 %TMP%）：临时盘 ≠ 存档
                // 盘（典型 C: vs D:），满了会让用户拿到"档案无效"的误导性结论
                string ovfPath;
                if (ovfOrOvaPath.EndsWith(".ova", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var need = new FileInfo(ovfOrOvaPath).Length;
                        var tmpAvail = new DriveInfo(
                            Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath()).AvailableFreeSpace;
                        if (tmpAvail < need)
                            throw new GrassCoreException(
                                $"解包 OVA 约需 {Math.Max(1, need / 1024 / 1024 / 1024)} GB 临时空间，" +
                                $"但系统临时目录所在磁盘只剩 {tmpAvail / 1024 / 1024 / 1024} GB。请先清理系统盘空间后重试。");
                    }
                    catch (GrassCoreException) { throw; }
                    catch { /* 盘信息读不到：让后续真实 IO 错误兜底 */ }
                    var dir = Path.Combine(tempRoot, "ova");
                    ovfPath = ExportImport.OvfImporter.ExtractOva(ovfOrOvaPath, dir);
                }
                else
                {
                    ovfPath = ovfOrOvaPath;
                }
                plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath),
                    Path.GetDirectoryName(Path.GetFullPath(ovfPath))!);
            }
            catch (GrassCoreException)
            {
                throw; // 产品化错误原样上抛
            }
            catch (System.IO.FileNotFoundException)
            {
                throw new GrassCoreException("找不到这份档案文件（可能已被移动、重命名或删除）。");
            }
            catch (Exception e) when (e is System.Xml.XmlException or ArgumentException or InvalidOperationException
                     or System.IO.InvalidDataException or System.IO.EndOfStreamException or NotSupportedException)
            {
                // 非 XML / 结构坏的档案 / 损坏截断的 tar，与 zip 路径同标准：
                // 产品化措辞，不把英文 .NET 异常原文甩进横幅
                throw new GrassCoreException("这份档案不是有效的 OVF/OVA 描述文件。");
            }
            catch (System.IO.IOException e)
            {
                // 真实 IO 问题（临时盘满、源文件被占用）不是"档案坏"——指引用户
                // 清空间/关占用程序，而不是去重下重导档案
                throw new GrassCoreException($"读取或解包档案失败（磁盘空间不足或文件被其他程序占用）：{e.Message}");
            }
            var required = ExportImport.OvfImporter.EstimateRequiredBytes(plan.Disks);
            // §15.1"转换前估算空间，不足直接给出所需 GB 数"：计划阶段就对存档
            // 位置核对并警告（UI 会把 warnings 逐条过给用户），别等转换中途
            // 才以裸 IO 错误失败
            if (_db.LibraryRoot is not null)
            {
                try
                {
                    var libRoot = Path.GetFullPath(_db.LibraryRoot);
                    var avail = new DriveInfo(Path.GetPathRoot(libRoot) ?? libRoot).AvailableFreeSpace;
                    if (avail < required)
                        plan.Warnings.Add(
                            $"导入约需 {Math.Max(1, required / 1024 / 1024 / 1024)} GB 空闲空间，" +
                            $"但存档位置当前只有 {avail / 1024 / 1024 / 1024} GB 可用，导入很可能中途失败。" +
                            "请先清理空间或更换存档位置。");
                }
                catch { /* 盘信息读不到：执行阶段还有一道硬校验兜底 */ }
            }
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
            // 清理失败不能改写结果：导入已成功（包 + 索引都在）却报错，重试就撞
            // "同名 VM 已存在"；杀毒/索引器短暂占用刚转换的磁盘是常态
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { /* 残留临时目录交给系统清理 */ }
        }
    }

    public object ExecuteImportOvf(string ovfOrOvaPath, string vmName, bool allowUnsupported) =>
        WithLibraryOperation(() => ExecuteImportOvfCore(ovfOrOvaPath, vmName, allowUnsupported));

    private object ExecuteImportOvfCore(string ovfOrOvaPath, string vmName, bool allowUnsupported)
    {
        if (IsInvalidVmName(vmName)) throw new GrassCoreException("虚拟机名称不合法（不能含逗号、等号或文件系统不允许的字符）。");
        var root = _db.LibraryRoot ?? throw new GrassCoreException("尚未设置虚拟机存档位置。");
        var tempRoot = Path.Combine(Path.GetTempPath(), "grassvm-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var importer = new ExportImport.OvfImporter(new Qemu.TransactionalDiskOps(_qemuImgPath),
                Path.Combine(_ovmfDir, "OVMF_VARS.fd"));
            ExportImport.OvfImporter.ImportPlan plan;
            try
            {
                // 解包（tar 层）+ XML 解析同在防护内（与计划路径同标准），解包前
                // 同样先核临时盘空间
                string ovfPath;
                if (ovfOrOvaPath.EndsWith(".ova", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var need = new FileInfo(ovfOrOvaPath).Length;
                        var tmpAvail = new DriveInfo(
                            Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath()).AvailableFreeSpace;
                        if (tmpAvail < need)
                            throw new GrassCoreException(
                                $"解包 OVA 约需 {Math.Max(1, need / 1024 / 1024 / 1024)} GB 临时空间，" +
                                $"但系统临时目录所在磁盘只剩 {tmpAvail / 1024 / 1024 / 1024} GB。请先清理系统盘空间后重试。");
                    }
                    catch (GrassCoreException) { throw; }
                    catch { /* 盘信息读不到：让后续真实 IO 错误兜底 */ }
                    ovfPath = ExportImport.OvfImporter.ExtractOva(ovfOrOvaPath, Path.Combine(tempRoot, "ova"));
                }
                else
                {
                    ovfPath = ovfOrOvaPath;
                }
                plan = importer.PlanFromOvf(System.Xml.Linq.XDocument.Load(ovfPath),
                    Path.GetDirectoryName(Path.GetFullPath(ovfPath))!);
            }
            catch (GrassCoreException)
            {
                throw;
            }
            catch (System.IO.FileNotFoundException)
            {
                throw new GrassCoreException("找不到这份档案文件（可能已被移动、重命名或删除）。");
            }
            catch (Exception e) when (e is System.Xml.XmlException or ArgumentException or InvalidOperationException
                     or System.IO.InvalidDataException or System.IO.EndOfStreamException or NotSupportedException)
            {
                throw new GrassCoreException("这份档案不是有效的 OVF/OVA 描述文件。");
            }
            catch (System.IO.IOException e)
            {
                throw new GrassCoreException($"读取或解包档案失败（磁盘空间不足或文件被其他程序占用）：{e.Message}");
            }
            // 空间预检（§15.1"不足直接给出所需 GB 数"）：执行前再核一次——
            // 计划到执行之间用户可能已把空间用掉/换过存档位置。
            // UNC 存档根（\\server\share\…）没有盘符，DriveInfo 构造直接抛
            // ArgumentException——与计划路径同标准：读不到就跳过硬校验，
            // 让转换阶段的真实 IO 错误兜底（计划阶段已给过空间警告）
            var requiredExec = ExportImport.OvfImporter.EstimateRequiredBytes(plan.Disks);
            var libRootAbs = Path.GetFullPath(root);
            long availBytes;
            try
            {
                availBytes = new DriveInfo(Path.GetPathRoot(libRootAbs) ?? libRootAbs).AvailableFreeSpace;
            }
            catch (Exception e) when (e is ArgumentException or System.IO.IOException)
            {
                availBytes = long.MaxValue; // UNC/不可枚举根：跳过
            }
            if (availBytes < requiredExec)
                throw new GrassCoreException(
                    $"导入需要约 {Math.Max(1, requiredExec / 1024 / 1024 / 1024)} GB 空闲空间，" +
                    $"但存档位置只剩 {availBytes / 1024 / 1024 / 1024} GB。请先清理空间或更换存档位置。");
            var pkg = importer.ExecuteAsync(plan, root, vmName, allowUnsupported).GetAwaiter().GetResult();
            _db.UpsertIndex(new HostDb.VmIndexEntry(pkg.Path, pkg.Name, null, DateTimeOffset.UtcNow.ToString("o"), null));
            return new { path = pkg.Path, name = pkg.Name, warnings = plan.Warnings };
        }
        finally
        {
            // 同上：清理失败不改写成功结果
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { /* 残留临时目录交给系统清理 */ }
        }
    }

    /// <summary>OVF 目录导出（当前有效状态；OVA 打包由导出器完成）。</summary>
    public object ExportOvf(string packagePath, string destDir)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        var output = Path.GetFullPath(destDir);
        if (IsInsideGrassVmPackage(output))
            throw new GrassCoreException("导出目录必须位于虚拟机包目录之外。");
        return WithPackageGate(managed, () => ExportOvfCore(managed, output));
    }

    private static bool IsInsideGrassVmPackage(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (GrassVmPackage.IsGrassVmDirectory(current.FullName)) return true;
        return false;
    }

    private object ExportOvfCore(string packagePath, string destDir)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        ExportImport.GrassVmZip.EnsureExportable(pkg, isRunning: false, lockHeld: true);
        var exporter = new ExportImport.OvfExporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var ovfPath = exporter.ExportAsync(pkg, destDir).GetAwaiter().GetResult();
        return new { ovf = ovfPath };
    }

    public object ExportOva(string packagePath, string ovaPath)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        var output = Path.GetFullPath(ovaPath);
        if (IsInsideGrassVmPackage(output))
            throw new GrassCoreException("导出档案必须保存到虚拟机包目录之外。");
        return WithPackageGate(managed, () => ExportOvaCore(managed, output));
    }

    private object ExportOvaCore(string packagePath, string ovaPath)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        ExportImport.GrassVmZip.EnsureExportable(pkg, isRunning: false, lockHeld: true);
        var exporter = new ExportImport.OvfExporter(new Qemu.TransactionalDiskOps(_qemuImgPath));
        var result = exporter.ExportOvaAsync(pkg, ovaPath, pkg.TempPath).GetAwaiter().GetResult();
        return new { ova = result };
    }

    // ---------- 快照 ----------

    public object CreateSnapshot(string packagePath, string name, string? description)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => CreateSnapshotCore(managed, name, description));
    }

    private object CreateSnapshotCore(string packagePath, string name, string? description)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        var config = new ConfigStore(pkg).Load();
        var snap = SnapshotService.Create(pkg, config, name, description,
            diskOps: new Qemu.TransactionalDiskOps(_qemuImgPath));
        return new { uuid = snap.Uuid };
    }

    public object ListSnapshots(string packagePath) => SnapshotService.List(new GrassVmPackage(ResolveManagedPackagePath(packagePath)));

    /// <summary>恢复前预览：把 Core 的警告（配置回滚、包外盘不回滚等）交给 UI 在确认框里如实展示。</summary>
    public object PlanRestoreSnapshot(string packagePath, string uuid)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => PlanRestoreSnapshotCore(managed, uuid));
    }

    private object PlanRestoreSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        var tree = SnapshotService.LoadTree(pkg);
        if (!tree.TryGet(uuid, out _))
            throw new GrassCoreException("快照不存在，请刷新列表。");
        var plan = SnapshotPlanner.PlanRestore(tree, uuid);
        return new { warnings = plan.Warnings };
    }

    public object RestoreSnapshot(string packagePath, string uuid)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => RestoreSnapshotCore(managed, uuid));
    }

    private object RestoreSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        var plan = SnapshotPlanner.PlanRestore(SnapshotService.LoadTree(pkg), uuid);
        SnapshotService.Restore(pkg, uuid, new Qemu.TransactionalDiskOps(_qemuImgPath));
        return new { restored = uuid, warnings = plan.Warnings };
    }

    public object PlanDeleteSnapshot(string packagePath, string uuid)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => PlanDeleteSnapshotCore(managed, uuid));
    }

    private object PlanDeleteSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        var tree = SnapshotService.LoadTree(pkg);
        // 陈旧 UUID（列表刷新前被别处删掉）→ 产品化措辞，而不是裸 KeyNotFound。
        // 必须放在 PlanDelete 之前——规划器内部同样按 uuid 索引
        if (!tree.TryGet(uuid, out var target) || target is null)
            throw new GrassCoreException("快照不存在，请刷新列表。");
        var clones = SnapshotService.FindLinkedCloneReferences(pkg);
        var plan = SnapshotPlanner.PlanDelete(tree, uuid, clones);
        // 链根基座（无父）删除不做 commit/rebase：物理文件保留（后代还指着它），
        // 只是元数据移除——又快，也没有"合并进前一个快照"这回事。非根删除是否
        // 合并取决于【物理依赖者】（孩子冻结层 / 实际压在这层上的活动盘）：没有
        // 依赖者的删除 = 直接丢弃（快、不 commit）；有依赖者才 commit 进父层。
        // UI 靠这两个字段给出如实的确认措辞
        var isChainRoot = target.ParentSnapshotUuid is null;
        var parent = isChainRoot ? null
            : tree.TryGet(target.ParentSnapshotUuid!, out var parentNode) ? parentNode : null;
        // 【逐盘】与执行侧同判：某盘有物理依赖者（孩子冻结层文件在场 / 活动盘
        // 压在这层上 / 指针查询失败按保守计）且父快照有它的引用 → 该盘 commit
        // 合并；父没有该盘引用（建盘时点落在父之后）→ 该盘按"保留基座"处置。
        // 两种处置会出现在同一次删除里（混合盘型）——措辞绝不能"全有或全无"：
        // 承诺"不合并"而实际跑分钟级 commit = 把用户当猴耍；承诺"合并"而实际
        // 只动元数据 = 用户为不存在的改写白等白担心
        var children = tree.ChildrenOf(uuid).ToList();
        var positionWasHere = string.Equals(
            GrassCore.Config.VmState.Load(pkg).CurrentSnapshotUuid, uuid, StringComparison.OrdinalIgnoreCase);
        bool mergeDevice = false, keepDevice = false;
        try
        {
            var ops = new Qemu.TransactionalDiskOps(_qemuImgPath);
            var config = new ConfigStore(pkg).Load();
            foreach (var (devId, frozenRel) in target.DiskOverlayRefs)
            {
                var frozenAbs = System.IO.Path.Combine(pkg.SnapshotsPath, uuid,
                    frozenRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var frozenExists = File.Exists(frozenAbs);
                // 执行侧对【目标自己的冻结文件缺失】整盘跳过（continue，无合并
                // 也无保留）——计划侧必须同判，否则缺失+孩子在场时承诺"合并"
                // 而实际是元数据删除
                if (!frozenExists) continue;
                // 父的该盘冻结文件路径（在场才存在合并语义；childHas 的"上一轮
                // 已 rebase"排除要用到）
                string? parentFrozenAbs = null;
                if (parent is not null && parent.DiskOverlayRefs.TryGetValue(devId, out var pr))
                {
                    var pAbs0 = System.IO.Path.Combine(pkg.SnapshotsPath, parent.Uuid,
                        pr.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (File.Exists(pAbs0)) parentFrozenAbs = pAbs0;
                }
                // 孩子在场 = 依赖者——除非它的 backing 已经指向父：那是上一轮删除
                // 自己的产物（物理 rebase 先于元数据改挂，崩溃夹在中间），执行侧
                // 会排除它、这层盘按"无依赖"直接丢弃。计划不排除的话，措辞会承诺
                // 一次并不存在的分钟级合并（同轮崩溃重试现场）
                var childHas = false;
                foreach (var c in children)
                {
                    if (!c.DiskOverlayRefs.TryGetValue(devId, out var r)) continue;
                    var cAbs = System.IO.Path.Combine(pkg.SnapshotsPath, c.Uuid,
                        r.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    if (!File.Exists(cAbs)) continue;
                    if (parentFrozenAbs is not null)
                    {
                        string? cb;
                        try { cb = ops.QueryBackingFileStrict(cAbs); }
                        catch (GrassCore.Qemu.QemuImgException) { cb = cAbs; } // 失败：保守按依赖计（同执行侧）
                        if (cb is not null
                            && string.Equals(System.IO.Path.GetFullPath(cb), System.IO.Path.GetFullPath(parentFrozenAbs),
                                StringComparison.OrdinalIgnoreCase))
                            continue; // 已被上一轮 rebase 到父：只差元数据改挂
                    }
                    childHas = true;
                    break;
                }
                // 活动盘判定（执行侧同一套规则，含失败保守计）
                var activeDependent = false;
                var disk = config.Devices.OfType<DiskDevice>().FirstOrDefault(d => d.DeviceId == devId);
                if (disk is not null)
                {
                    var active = GrassVm.PathPolicy.Resolve(pkg, disk.Path);
                    if (File.Exists(active) && frozenExists)
                    {
                        string? b;
                        try { b = ops.QueryBackingFileStrict(active); }
                        catch (GrassCore.Qemu.QemuImgException)
                        {
                            b = frozenAbs; // 查询失败：按依赖计（保守，同执行侧）
                        }
                        if (b is not null
                            && string.Equals(System.IO.Path.GetFullPath(b), System.IO.Path.GetFullPath(frozenAbs),
                                StringComparison.OrdinalIgnoreCase))
                            activeDependent = true;
                        else if (b is null && positionWasHere)
                            activeDependent = true;
                    }
                }
                if (!childHas && !activeDependent) continue; // 这层盘无依赖者：随删除丢弃
                // 合并（commit 进父层）要求父的该盘冻结文件【在场】（上面已算出）
                //——父文件缺失/损坏时执行侧走"保留基座"，计划承诺"合并"就是
                // 又一次措辞与事实的分叉
                var parentFrozenExists = parentFrozenAbs is not null;
                if (!parentFrozenExists) keepDevice = true;
                else mergeDevice = true;
            }
        }
        catch
        {
            mergeDevice = true; // 元数据/查询整体失败：按"有合并"提示（保守）
        }
        return new
        {
            isChainRoot,
            requiresMerge = !isChainRoot && mergeDevice,
            keepsBase = !isChainRoot && keepDevice,
            affectedLinkedClones = plan.AffectedLinkedClones.Select(c => new { c.ChildVmName, c.ChildVmPath }),
            rebindings = plan.Rebindings,
        };
    }

    public object DeleteSnapshot(string packagePath, string uuid)
    {
        var managed = ResolveManagedPackagePath(packagePath);
        return WithPackageGate(managed, () => DeleteSnapshotCore(managed, uuid));
    }

    private object DeleteSnapshotCore(string packagePath, string uuid)
    {
        var pkg = new GrassVmPackage(packagePath);
        using var mutationLock = EnsureStoppedForMutation(pkg);
        // 链接克隆位于不同包，父包的门无法阻止它在检查后启动。先为所有
        // 依赖父快照的克隆持有跨进程锁；任一克隆正在运行则安全拒绝删除，
        // 锁贯穿 rebase/删除直到完成。
        var cloneLocks = new List<VmLock>();
        try
        {
            foreach (var clone in SnapshotService.FindLinkedCloneReferences(pkg))
            {
                var cloneLock = new VmLock(new GrassVmPackage(clone.ChildVmPath));
                cloneLock.Acquire();
                cloneLocks.Add(cloneLock);
            }
            SnapshotService.Delete(pkg, uuid, new Qemu.TransactionalDiskOps(_qemuImgPath));
        }
        finally
        {
            foreach (var cloneLock in cloneLocks) cloneLock.Dispose();
        }
        return new { deleted = uuid };
    }

    // ---------- 自动启动（串行：启动 1 台 → 等默认 10 秒 → 下一台；失败只跳过该 VM）----------

    public object SetAutostart(string packagePath, bool enabled)
    {
        var pkg = new GrassVmPackage(ResolveManagedPackagePath(packagePath));
        if (!File.Exists(pkg.ConfigPath))
            throw new GrassCoreException("虚拟机不存在。");
        // 宿主级属性（不写入 .grassvm，迁移不继承）；路径规范化，防止相对/绝对混用
        _db.SetAutostart(Path.GetFullPath(pkg.Path), enabled);
        return new { path = pkg.Path, enabled };
    }

    public object RemoveAutostart(string packagePath)
    {
        var pkg = new GrassVmPackage(ResolveManagedPackagePath(packagePath));
        _db.RemoveAutostart(Path.GetFullPath(pkg.Path));
        return new { path = pkg.Path, removed = true };
    }

    public object SetAutostartOrder(string[] orderedVmPaths)
    {
        var managed = orderedVmPaths.Select(ResolveManagedPackagePath).ToArray();
        _db.SetAutostartOrder(managed);
        return new { count = orderedVmPaths.Length };
    }

    public object GetAutostartInterval() => new { intervalSeconds = _db.AutostartIntervalSeconds };

    public object SetAutostartInterval(int seconds)
    {
        _db.AutostartIntervalSeconds = seconds;
        return new { intervalSeconds = _db.AutostartIntervalSeconds };
    }

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

    /// <summary>
    /// 启动前收尾 + 预检（StartVm/ResumeVm/自启动共用）。次序是承诺：
    /// ① vm.lock 先拒——共享库上别机运行中（残留 vm.lock）时，修复会动它
    /// 正打开的冻结 backing，必须先拒绝、不碰磁盘；
    /// ② 修复换入中断残留（幂等）——快照/恢复事务的崩溃现场是"工作盘缺失 +
    ///    暂存 overlay 完好"，恰恰靠这步补齐；Restore 日志收不了尾 = 多盘撕裂
    ///    在两个时间点 → 修复返回 false → 拒绝启动；
    /// ③ 全量预检（资源/锁/显示设备/NVRAM/磁盘在位）。放在修复之后：先跑的话
    ///    会把可自愈的现场误报成"磁盘文件丢失"，而内部盘没有重定位入口——死路。
    /// </summary>
    private void RepairThenPreflight(GrassVmPackage pkg, VmConfigView view, bool lockHeld = false)
    {
        if (!lockHeld && File.Exists(pkg.LockPath))
            throw new GrassCoreException(
                "此虚拟机已被占用（vm.lock 存在）。只有在确认它没有在其他实例或其他电脑上运行时，才能解除锁定。");
        if (!SnapshotService.RepairStagedOverlays(pkg, new Qemu.TransactionalDiskOps(_qemuImgPath)))
            throw new GrassCoreException("此虚拟机有一个未完成的恢复操作正在收尾（磁盘可能被占用）。请关闭占用它的程序后重试。");
        var fatal = StartupPreflight.Check(pkg, view, lockHeld).Where(p => p.Fatal).ToList();
        if (fatal.Count > 0)
            throw new GrassCoreException(string.Join("\n", fatal.Select(p => p.UserMessage)));
    }

    /// <summary>
    /// 收养扫描的死进程分支专用：作废"会话其实是一次 -incoming 恢复"留下的
    /// 陈旧挂起标记。恢复会话会重放 suspend.state（-incoming），而标记在确认
    /// 完成前一直挂着；Core 崩了 + QEMU 随后退出时，包上只剩 state.json 的
    /// 挂起标记——下一次 ResumeVm 会把【旧 RAM 状态】回放到已经前进过的磁盘
    /// 上（静默损毁客户机文件系统）。判别：suspend.state 的写入时间 ≤ 会话
    /// 开始时间 = 文件是该会话之前就写好的（它是通过 -incoming 被消费的，
    /// 不是该会话产生的挂起）；文件比会话新 = 该会话自己在 SuspendVm 里写的
    /// （合法挂起，保留）。读不了时间戳/文件缺失时同样作废：标记已是无用残迹
    /// </summary>
    private static void InvalidateStaleSuspendMarker(GrassVmPackage pkg, RuntimeSession session)
    {
        try
        {
            var st = VmState.Load(pkg);
            if (st.SuspendedStatePath is null) return;
            var sf = PathPolicy.Resolve(pkg, st.SuspendedStatePath);
            DateTime? written = null;
            try { if (File.Exists(sf)) written = File.GetLastWriteTimeUtc(sf); }
            catch { /* 读不了时间戳：按作废处理 */ }
            if (written is not null && written.Value > session.StartedAt.UtcDateTime)
                return; // 文件是该会话在挂起流程里写的：合法挂起，保留
            st.SuspendedStatePath = null;
            st.SuspendFingerprint = null;
            st.Save(pkg);
            try { if (File.Exists(sf)) File.Delete(sf); } catch { /* 尽力 */ }
        }
        catch { /* state 读写失败：留待下轮，绝不阻断收养收尾 */ }
    }

    private static string CommandFingerprint(QemuCommandLine command)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\0", command.Args));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IReadOnlyList<Process> FindQemuForLaunchIntent(RuntimeSession session)
    {
        if (string.IsNullOrWhiteSpace(session.QemuExecutablePath)) return Array.Empty<Process>();
        var expected = Path.GetFullPath(session.QemuExecutablePath);
        var matches = new List<Process>();
        foreach (var candidate in Process.GetProcessesByName("qemu-system-x86_64"))
        {
            try
            {
                if (candidate.HasExited || candidate.StartTime.ToUniversalTime() < session.StartedAt.UtcDateTime.AddSeconds(-5))
                { candidate.Dispose(); continue; }
                var actual = candidate.MainModule?.FileName;
                if (actual is not null && string.Equals(Path.GetFullPath(actual), expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    // 启动意图必须绑定到本会话生成的 QMP 标识。WMI 在受限权限/非
                    // Windows 环境可能无法读取命令行，此时保留候选交给后续 QMP
                    // 握手确认；若能读取则拒绝同路径、同时间窗口的其他 QEMU。
                    var commandLine = TryGetProcessCommandLine(candidate.Id);
                    var expectedQmpToken = $"grassvm-qmp-{session.SessionId}";
                    if (commandLine is not null
                        && !commandLine.Contains(expectedQmpToken, StringComparison.OrdinalIgnoreCase))
                    {
                        candidate.Dispose();
                        continue;
                    }
                    matches.Add(candidate);
                    continue;
                }
                candidate.Dispose();
            }
            catch
            {
                candidate.Dispose();
            }
        }
        return matches;
    }

    private static string? TryGetProcessCommandLine(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 0) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject item in searcher.Get())
                return item["CommandLine"] as string;
        }
        catch
        {
            // 命令行读取是额外的绑定信号，不是唯一安全边界；后续 QMP
            // 握手仍会证明管道属于本次会话，读取失败不能误杀活 VM。
        }
        return null;
    }

    private static VmConfigView ToConfigView(VmConfiguration config)
    {
        var disks = config.DevicesOfType<DiskDevice>()
            .Select(d => new VmConfigView.DiskView(d.Path, "硬盘", d.IsExternal)).ToList();
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
            // 两个流都要重定向：BeginOutputReadLine 在 stdout 未重定向时直接抛
            // InvalidOperationException，外层 catch 吞掉后 BeginErrorReadLine 也
            // 不会执行——stderr 管道没人读，QEMU 写满 ~4KB 后整机冻结
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new GrassCoreException("无法启动 QEMU。");
        var logLock = new object();
        var logPath = Path.Combine(cmd.PackageRoot ?? ".", GrassVmPackage.LogsDir, "qemu.log");
        FileStream? logStream = null;
        StreamWriter? logWriter = null;
        var processExited = false;
        var stdoutClosed = false;
        var stderrClosed = false;
        var logClosed = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var fi = new FileInfo(logPath);
            if (!fi.Exists || fi.Length > 2 * 1024 * 1024)
            {
                using var reset = new FileStream(logPath, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var resetWriter = new StreamWriter(reset, new UTF8Encoding(false));
                resetWriter.WriteLine($"--- session {DateTimeOffset.Now:O} ---");
            }
            // 整个 QEMU 会话共用一个允许并发读取的句柄。Windows 上即使每次
            // Append 都指定 FileShare.ReadWrite，多个异步回调仍可能让诊断页在
            // 回调交错时撞上共享冲突；持有句柄能把共享策略固定下来。
            logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            logWriter = new StreamWriter(logStream, new UTF8Encoding(false));
        }
        catch { }
        void AppendLogLine(string line)
        {
            try
            {
                // FileShare.ReadWrite|Delete 允许诊断页/测试在 QEMU 退出后立即读取，
                // 同时保留 stdout/stderr 两个回调之间的行级串行化。
                lock (logLock)
                {
                    if (logWriter is not null)
                    {
                        logWriter.WriteLine(LogRedactor.Redact(line));
                        logWriter.Flush();
                    }
                }
            }
            catch { }
        }

        void TryCloseLog()
        {
            if (logClosed || !processExited || !stdoutClosed || !stderrClosed) return;
            logClosed = true;
            try { logWriter?.Flush(); } catch { }
            try { logWriter?.Dispose(); } catch { }
            logWriter = null;
            logStream = null;
        }

        // Process.WaitForExit() 会等待 Begin*ReadLine 的异步回调全部排空，
        // 因而调用方在 WaitForExit 返回后可以可靠读取完整 qemu.log。
        proc.OutputDataReceived += (_, e) =>
        {
            lock (logLock)
            {
                if (e.Data is null) stdoutClosed = true;
                else AppendLogLine(e.Data);
                TryCloseLog();
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            lock (logLock)
            {
                if (e.Data is null) stderrClosed = true;
                else AppendLogLine(e.Data);
                TryCloseLog();
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.Exited += (_, _) =>
        {
            lock (logLock)
            {
                processExited = true;
                TryCloseLog();
            }
        };
        proc.EnableRaisingEvents = true;
        return proc;
    }
}
