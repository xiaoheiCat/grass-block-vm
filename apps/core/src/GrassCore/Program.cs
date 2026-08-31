using System.Text.Json;
using GrassCore.Library;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore;

/// <summary>
/// GrassCore 入口。按需启动：打开 UI 时由 Electron 主进程拉起；UI 退出但仍有 VM 运行时继续存在；
/// 最后一台 VM 结束且 UI 已退出时自动退出。GrassCore 是协调者，不是 VM 的所有者。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() is { } cmd && cmd == "--version")
        {
            Console.WriteLine("GrassCore 0.1.0");
            return 0;
        }

        // 路径解析：bundled QEMU Runtime 与固件随 Grass Block VM 整包安装（不可由用户配置，1.0 无自定义 Runtime）。
        // 开发/联调可用环境变量 GRASSCORE_QEMU_DIR / GRASSCORE_OVMF_DIR 覆盖（不影响产品形态）。
        var installDir = AppContext.BaseDirectory;
        var qemuDir = Environment.GetEnvironmentVariable("GRASSCORE_QEMU_DIR");
        var qemuSystem = FindFile(qemuDir ?? installDir, "qemu-system-x86_64.exe", "qemu-system-x86_64");
        var qemuImg = FindFile(qemuDir ?? installDir, "qemu-img.exe", "qemu-img");
        var ovmfEnv = Environment.GetEnvironmentVariable("GRASSCORE_OVMF_DIR");
        var ovmfDir = !string.IsNullOrEmpty(ovmfEnv) && Directory.Exists(ovmfEnv)
            ? ovmfEnv
            : FindDir(installDir, "firmware") ?? FindDir(installDir, "share") ?? installDir;
        var qemuMajor = Environment.GetEnvironmentVariable("GRASSCORE_QEMU_MAJOR") ?? "bundled";

        var dbPath = HostDbPath();
        using var db = new HostDb(dbPath);
        var service = new GrassCoreService(db, qemuSystem ?? "qemu-system-x86_64", qemuImg ?? "qemu-img",
            ovmfDir ?? installDir, qemuMajor);

        // 启动即清理残留 .grass-tmp（应用启动时自动删除，不尝试断点续传）
        if (db.LibraryRoot is { } root)
        {
            foreach (var pkg in GrassVm.GrassVmPackage.ScanLibraryRoot(root))
            {
                pkg.CleanupResidualTempFiles();
            }
        }

        // Core 崩溃恢复：接管仍在运行的 QEMU（不触碰 QEMU 本体；旧 Helper 无法安全复用时替换 Helper）
        var recovery = new CoreCrashRecovery();
        foreach (var r in recovery.ScanAdoptable(db.LibraryRoot ?? "."))
        {
            if (r.SessionValid)
                Console.Error.WriteLine($"[grasscore] 重新接管运行中的虚拟机：{r.Package.Name} (pid {r.Session.QemuPid})");
        }

        // Core 重启（或 UI 先于 Core 启动后 Core 崩溃重启）：先无损重接管运行中的 VM
        try { service.AdoptRunningVms(); }
        catch { /* 接管失败不阻塞服务启动；预检/手动解锁兜底 */ }

        // 空闲自动退出：无连接 + 无运行中的 VM 持续 2 分钟 → 进程结束。
        // （契约：最后一台 VM 结束且 UI 已退出时 Core 自动退出。UI 侧重开时按需重生 Core；
        // 重生会重新执行上面的重接管——收尾失败残留的锁因此有机会被重扫。）
        _ = Task.Run(async () =>
        {
            var idleSince = (DateTimeOffset?)null;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                if (Transport.ActiveConnections > 0 || service.HasRunningVms || service.HasInFlightDiskJobs)
                {
                    idleSince = null;
                    continue;
                }
                idleSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - idleSince > TimeSpan.FromMinutes(2))
                    Environment.Exit(0);
            }
        });

        await Transport.RunServerAsync(conn => HandleConnectionAsync(conn, service));
        return 0;
    }

    private static async Task HandleConnectionAsync(JsonRpcConnection conn, GrassCoreService service)
    {
        // 每个请求在线程池上执行、响应按完成顺序回写（JSON-RPC id 配对，客户端不依赖顺序）。
        // 不能串行等待：挂起大内存 VM 时 migrate 轮询可达分钟级，串行会把同一连接上的
        // scanLibrary（3 秒轮询）/ forceOff 全部堵死——用户既看不到状态也取消不了。
        var pending = new List<Task>();
        while (true)
        {
            JsonElement? request;
            try { request = await conn.ReceiveAsync(); }
            catch (Exception)
            {
                break; // 连接关闭
            }
            if (request is null) break;
            var currentRequest = request.Value;

            pending.Add(Task.Run(() =>
            {
                try
                {
                    var method = currentRequest.GetProperty("method").GetString()!;
                    var id = currentRequest.TryGetProperty("id", out var idEl) ? idEl : (JsonElement?)null;
                    JsonElement p = currentRequest.TryGetProperty("params", out var pEl) ? pEl : JsonDocument.Parse("{}").RootElement;
                    object result = Dispatch(service, method, p);
                    if (id is not null)
                        conn.SendAsync(JsonRpcConnection.Ok(result, id.Value)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (currentRequest.TryGetProperty("id", out var idEl2) && idEl2.ValueKind != JsonValueKind.Null)
                        conn.SendAsync(JsonRpcConnection.Error(-32000, ex.Message, idEl2)).GetAwaiter().GetResult();
                }
            }));
            pending.RemoveAll(t => t.IsCompleted);
        }
        // 连接关闭：等在途回写结束（新请求不再产生）
        await Task.WhenAll(pending);
    }

    private static object Dispatch(GrassCoreService s, string method, JsonElement p) => method switch
    {
        "ping" => new { pong = true, version = "0.1.0" },
        "scanLibrary" => s.ScanLibrary(),
        "getProfiles" => s.GetProfiles(),
        "adoptRunningVms" => s.AdoptRunningVms(),
        "getDisplayInfo" => s.GetDisplayInfo(p.GetProperty("packagePath").GetString()!),
        "getHostInfo" => s.GetHostInfo(),
        "createVm" => s.CreateVm(p),
        "startVm" => s.StartVm(p.GetProperty("packagePath").GetString()!),
        "resumeVm" => s.ResumeVm(p.GetProperty("packagePath").GetString()!),
        "powerAction" => s.PowerAction(p.GetProperty("packagePath").GetString()!, p.GetProperty("action").GetString()!),
        "changeMedium" => s.ChangeMedium(
            p.GetProperty("packagePath").GetString()!,
            p.GetProperty("deviceId").GetString()!,
            p.GetProperty("isoPath").ValueKind == JsonValueKind.Null ? null : p.GetProperty("isoPath").GetString()),
        "getConfig" => s.GetConfig(p.GetProperty("packagePath").GetString()!),
        "runAutostart" => s.RunAutostartAsync().GetAwaiter().GetResult(),
        "setAutostart" => s.SetAutostart(p.GetProperty("packagePath").GetString()!, p.GetProperty("enabled").GetBoolean()),
        "removeAutostart" => s.RemoveAutostart(p.GetProperty("packagePath").GetString()!),
        "setAutostartOrder" => s.SetAutostartOrder(p.GetProperty("orderedVmPaths").EnumerateArray().Select(e => e.GetString()!).ToArray()),
        "getAutostartInterval" => s.GetAutostartInterval(),
        "setAutostartInterval" => s.SetAutostartInterval(p.GetProperty("intervalSeconds").GetInt32()),
        "updateConfig" => s.UpdateConfig(p.GetProperty("packagePath").GetString()!, p.GetProperty("configJson").GetString()!),
        "resizeDisk" => s.ResizeDisk(
            p.GetProperty("packagePath").GetString()!,
            p.GetProperty("deviceId").GetString()!,
            // 自由文本数字输入：小数/NaN 会让 GetInt64 裸抛英文异常——
            // 按产品规则给可读错误，整数交给 ResizeDisk 钳制
            p.GetProperty("newGiB").ValueKind == JsonValueKind.Number
                && p.GetProperty("newGiB").TryGetInt64(out var newGiB)
                ? newGiB
                : throw new GrassCore.Rpc.GrassCoreException("磁盘大小必须是整数 GB。")),
        "unlockVm" => s.UnlockVm(p.GetProperty("packagePath").GetString()!),
        "discardSuspendState" => s.DiscardSuspendState(p.GetProperty("packagePath").GetString()!),
        "fullClone" => s.FullClone(p.GetProperty("packagePath").GetString()!, p.GetProperty("newName").GetString()!),
        "linkedClone" => s.LinkedClone(p.GetProperty("packagePath").GetString()!, p.GetProperty("snapshotUuid").GetString()!, p.GetProperty("newName").GetString()!),
        "exportZip" => s.ExportZip(p.GetProperty("packagePath").GetString()!, p.GetProperty("zipPath").GetString()!),
        "importZip" => s.ImportZip(p.GetProperty("zipPath").GetString()!),
        "planImportOvf" => s.PlanImportOvf(p.GetProperty("path").GetString()!),
        "executeImportOvf" => s.ExecuteImportOvf(p.GetProperty("path").GetString()!, p.GetProperty("vmName").GetString()!, p.GetProperty("allowUnsupported").GetBoolean()),
        "exportOvf" => s.ExportOvf(p.GetProperty("packagePath").GetString()!, p.GetProperty("destDir").GetString()!),
        "exportOva" => s.ExportOva(p.GetProperty("packagePath").GetString()!, p.GetProperty("ovaPath").GetString()!),
        "createSnapshot" => s.CreateSnapshot(
            p.GetProperty("packagePath").GetString()!,
            p.GetProperty("name").GetString()!,
            p.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.Null
                ? description.GetString()
                : null),
        "listSnapshots" => s.ListSnapshots(p.GetProperty("packagePath").GetString()!),
        "planRestoreSnapshot" => s.PlanRestoreSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "restoreSnapshot" => s.RestoreSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "planDeleteSnapshot" => s.PlanDeleteSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "deleteSnapshot" => s.DeleteSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "getPreferences" => new
        {
            libraryRoot = s.LibraryRoot,
        },
        "setLibraryRoot" => s.SetLibraryRoot(p.GetProperty("libraryRoot").GetString()!),
        _ => throw new GrassCoreException($"未知方法：{method}"),
    };

    private static string HostDbPath()
    {
        // %LOCALAPPDATA%\GrassBlockVM\grass.db（Windows）；开发机上落到仓库本地 .local/
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "GrassBlockVM", "grass.db");
        }
        return Path.Combine(AppContext.BaseDirectory, "grass.db");
    }

    private static string? FindFile(string dir, params string[] names)
    {
        foreach (var n in names)
        {
            var direct = Path.Combine(dir, n);
            if (File.Exists(direct)) return direct;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (names.Any(n => Path.GetFileNameWithoutExtension(n) == name)) return f;
        }
        return null;
    }

    private static string? FindDir(string dir, string name)
    {
        var direct = Path.Combine(dir, name);
        if (Directory.Exists(direct)) return direct;
        return Directory.EnumerateDirectories(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
