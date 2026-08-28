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

        await Transport.RunServerAsync(conn => HandleConnectionAsync(conn, service));
        return 0;
    }

    private static async Task HandleConnectionAsync(JsonRpcConnection conn, GrassCoreService service)
    {
        while (true)
        {
            JsonElement? request;
            try { request = await conn.ReceiveAsync(); }
            catch (Exception) { break; } // 连接关闭
            if (request is null) break;

            try
            {
                var method = request.Value.GetProperty("method").GetString()!;
                var id = request.Value.TryGetProperty("id", out var idEl) ? idEl : (JsonElement?)null;
                JsonElement p = request.Value.TryGetProperty("params", out var pEl) ? pEl : JsonDocument.Parse("{}").RootElement;
                object result = Dispatch(service, method, p);
                if (id is not null)
                    await conn.SendAsync(JsonRpcConnection.Ok(result, id.Value));
            }
            catch (Exception ex)
            {
                if (request.Value.TryGetProperty("id", out var idEl2) && idEl2.ValueKind != JsonValueKind.Null)
                    await conn.SendAsync(JsonRpcConnection.Error(-32000, ex.Message, idEl2));
            }
        }
    }

    private static object Dispatch(GrassCoreService s, string method, JsonElement p) => method switch
    {
        "ping" => new { pong = true, version = "0.1.0" },
        "scanLibrary" => s.ScanLibrary(),
        "getProfiles" => s.GetProfiles(),
        "createVm" => s.CreateVm(p),
        "startVm" => s.StartVm(p.GetProperty("packagePath").GetString()!),
        "powerAction" => s.PowerAction(p.GetProperty("packagePath").GetString()!, p.GetProperty("action").GetString()!),
        "unlockVm" => s.UnlockVm(p.GetProperty("packagePath").GetString()!),
        "createSnapshot" => s.CreateSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("name").GetString()!, null),
        "listSnapshots" => s.ListSnapshots(p.GetProperty("packagePath").GetString()!),
        "restoreSnapshot" => s.RestoreSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "planDeleteSnapshot" => s.PlanDeleteSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "deleteSnapshot" => s.DeleteSnapshot(p.GetProperty("packagePath").GetString()!, p.GetProperty("uuid").GetString()!),
        "runAutostart" => s.RunAutostartAsync().GetAwaiter().GetResult(),
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
