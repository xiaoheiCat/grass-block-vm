using GrassCore.GrassVm;
using Microsoft.Data.Sqlite;

namespace GrassCore.Library;

/// <summary>
/// 宿主级数据：%LOCALAPPDATA%\GrassBlockVM\grass.db（SQLite）。
/// 保存 Library Root、Host-only 网络（一级资源）、自动启动列表/顺序/间隔、更新偏好、VM 索引缓存。
/// Library Root 才是 VM 存在性的事实来源；索引缓存只为主界面秒开，可随时重建。
/// VM 自身的事实配置不复制进 SQLite。
/// </summary>
public sealed class HostDb : IDisposable
{
    private readonly SqliteConnection _conn;
    /// <summary>命令串行化门：Microsoft.Data.Sqlite 不支持单连接并发（读器重叠直接抛），
    /// 而 RPC 分发是并发的（scanLibrary 轮询 + StartVm 写偏好同时发生）。</summary>
    private readonly object _gate = new();

    public HostDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Migrate();
    }

    private void Migrate()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS preferences (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vm_index_cache (
                package_path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                artwork_summary TEXT,
                mtime TEXT,
                last_known_state TEXT
            );
            CREATE TABLE IF NOT EXISTS autostart (
                vm_path TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                position INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS virtual_networks (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                subnet TEXT NOT NULL,
                dhcp_enabled INTEGER NOT NULL DEFAULT 1,
                dhcp_range_start TEXT,
                dhcp_range_end TEXT,
                is_default INTEGER NOT NULL DEFAULT 0
            );
            """);
        // 默认值：Library Root、自动启动间隔 10 秒（可在全局设置调整 0–60）、默认 Host-only 网络
        SetDefaultPreference("libraryRoot", null);
        SetDefaultPreference("autostartIntervalSeconds", "10");
        if (CountNetworks() == 0)
        {
            var subnet = SuggestFreeSubnet(Array.Empty<string>());
            var prefix = subnet[..subnet.LastIndexOf('.')];
            CreateNetwork(new VirtualNetwork(
                Id: Guid.NewGuid().ToString(),
                Name: "Host-only",
                Subnet: subnet,
                DhcpEnabled: true,
                DhcpRangeStart: prefix + ".10",
                DhcpRangeEnd: prefix + ".254",
                IsDefault: true));
        }
    }

    // ---- preferences ----

    public string? GetPreference(string key)
    {
        string? v = null;
        Locked(cmd =>
        {
            cmd.CommandText = "SELECT value FROM preferences WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            v = cmd.ExecuteScalar() as string;
        });
        return v;
    }

    public void SetPreference(string key, string value)
    {
        Locked(cmd =>
        {
            cmd.CommandText = "INSERT INTO preferences(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        });
    }

    private void SetDefaultPreference(string key, string? value)
    {
        if (GetPreference(key) is null && value is not null) SetPreference(key, value);
    }

    /// <summary>宿主只有一个当前 Library Root。默认为用户 Documents 下 Grass Block VM 目录（UI 层给出）。</summary>
    public string? LibraryRoot => GetPreference("libraryRoot");

    /// <summary>
    /// 更改 Library Root 只影响之后创建/导入；旧目录 VM 不迁移，也不再出现在主界面。
    /// </summary>
    public void ChangeLibraryRoot(string newRoot)
    {
        SetPreference("libraryRoot", Path.GetFullPath(newRoot));
        // 旧缓存条目不再有效：整个索引缓存可随时重建
        Exec("DELETE FROM vm_index_cache;");
    }

    public int AutostartIntervalSeconds
    {
        get => int.TryParse(GetPreference("autostartIntervalSeconds"), out var v) ? v : 10;
        set => SetPreference("autostartIntervalSeconds", Math.Clamp(value, 0, 60).ToString());
    }

    // ---- autostart（宿主级属性：不写入 .grassvm；迁移 VM 不继承）----

    public sealed record AutostartEntry(string VmPath, bool Enabled, int Position);

    public IReadOnlyList<AutostartEntry> GetAutostartList() =>
        Query("SELECT vm_path, enabled, position FROM autostart ORDER BY position",
              r => new AutostartEntry(r.GetString(0), r.GetInt64(1) != 0, (int)r.GetInt64(2)));

    /// <summary>自动启动列表支持拖拽排序；GrassCore 严格按此顺序启动。</summary>
    public void SetAutostartOrder(IEnumerable<string> orderedVmPaths)
    {
        // 整个事务持锁（Monitor 同线程可重入，内部 Exec/Locked 不死锁）
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var reset = _conn.CreateCommand())
            {
                reset.Transaction = tx;
                reset.CommandText = "UPDATE autostart SET position = 1000000";
                reset.ExecuteNonQuery();
            }
            var pos = 0;
            foreach (var p in orderedVmPaths)
            {
                Locked(cmd =>
                {
                    cmd.CommandText = "UPDATE autostart SET position = $p WHERE vm_path = $v";
                    cmd.Transaction = tx;
                    cmd.Parameters.AddWithValue("$p", pos);
                    cmd.Parameters.AddWithValue("$v", p);
                    cmd.ExecuteNonQuery();
                });
                pos++;
            }
            tx.Commit();
        }
    }

    public void SetAutostart(string vmPath, bool enabled)
    {
        Locked(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO autostart(vm_path, enabled, position)
                VALUES($v,$e,(SELECT COALESCE(MAX(position),-1)+1 FROM autostart))
                ON CONFLICT(vm_path) DO UPDATE SET enabled=$e
                """;
            cmd.Parameters.AddWithValue("$v", vmPath);
            cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            cmd.ExecuteNonQuery();
        });
    }

    public void RemoveAutostart(string vmPath)
    {
        Locked(cmd =>
        {
            cmd.CommandText = "DELETE FROM autostart WHERE vm_path = $v";
            cmd.Parameters.AddWithValue("$v", vmPath);
            cmd.ExecuteNonQuery();
        });
    }

    // ---- virtual networks（一级资源；允许多个独立 Host-only，默认创建一个）----

    public sealed record VirtualNetwork(
        string Id, string Name, string Subnet, bool DhcpEnabled,
        string? DhcpRangeStart, string? DhcpRangeEnd, bool IsDefault);

    public IReadOnlyList<VirtualNetwork> ListNetworks() =>
        Query("SELECT id,name,subnet,dhcp_enabled,dhcp_range_start,dhcp_range_end,is_default FROM virtual_networks",
              r => new VirtualNetwork(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0,
                  r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) != 0));

    public void CreateNetwork(VirtualNetwork n)
    {
        Locked(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO virtual_networks(id,name,subnet,dhcp_enabled,dhcp_range_start,dhcp_range_end,is_default)
                VALUES($id,$name,$subnet,$dhcp,$rs,$re,$def)
                """;
            cmd.Parameters.AddWithValue("$id", n.Id);
            cmd.Parameters.AddWithValue("$name", n.Name);
            cmd.Parameters.AddWithValue("$subnet", n.Subnet);
            cmd.Parameters.AddWithValue("$dhcp", n.DhcpEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$rs", (object?)n.DhcpRangeStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$re", (object?)n.DhcpRangeEnd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$def", n.IsDefault ? 1 : 0);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>自动选择不冲突的 RFC1918 网段（检查已有 Host-only 网段；手动指定时只做合法性提示，不阻止）。</summary>
    public static string SuggestFreeSubnet(IReadOnlyCollection<string> existing)
    {
        for (var i = 16; i < 256; i += 8)
        {
            var candidate = $"192.168.{i}.0/24";
            if (!existing.Contains(candidate)) return candidate;
        }
        for (var i = 16; i < 256; i += 8)
        {
            var candidate = $"10.{i}.0.0/24";
            if (!existing.Contains(candidate)) return candidate;
        }
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 16; i++)
        for (var j = 1; j < 255; j++)
        {
            var candidate = $"172.{i + 16}.{j}.0/24";
            if (used.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("没有可用的 Host-only 网段。");
    }

    private int CountNetworks()
    {
        int v = 0;
        Locked(cmd =>
        {
            cmd.CommandText = "SELECT COUNT(*) FROM virtual_networks";
            v = Convert.ToInt32(cmd.ExecuteScalar()!);
        });
        return v;
    }

    // ---- vm index cache ----

    public sealed record VmIndexEntry(string PackagePath, string Name, string? ArtworkSummary, string? Mtime, string? LastKnownState);

    public IReadOnlyList<VmIndexEntry> GetIndexCache() =>
        Query("SELECT package_path,name,artwork_summary,mtime,last_known_state FROM vm_index_cache",
              r => new VmIndexEntry(r.GetString(0), r.GetString(1),
                  r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                  r.IsDBNull(4) ? null : r.GetString(4)));

    public void UpsertIndex(VmIndexEntry e)
    {
        Locked(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO vm_index_cache(package_path,name,artwork_summary,mtime,last_known_state)
                VALUES($p,$n,$a,$m,$s)
                ON CONFLICT(package_path) DO UPDATE SET name=$n,artwork_summary=$a,mtime=$m,last_known_state=$s
                """;
            cmd.Parameters.AddWithValue("$p", e.PackagePath);
            cmd.Parameters.AddWithValue("$n", e.Name);
            cmd.Parameters.AddWithValue("$a", (object?)e.ArtworkSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", (object?)e.Mtime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", (object?)e.LastKnownState ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public void RebuildIndexCache(string libraryRoot)
    {
        lock (_gate) Exec("DELETE FROM vm_index_cache");
        foreach (var pkg in GrassVmPackage.ScanLibraryRoot(libraryRoot))
        {
            UpsertIndex(new VmIndexEntry(pkg.Path, pkg.Name, null,
                Directory.GetLastWriteTimeUtc(pkg.Path).ToString("o"), null));
        }
    }

    // ---- helpers ----

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>串行化执行一条自建命令（单语句公共方法的统一入口）。</summary>
    private void Locked(Action<SqliteCommand> body)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            body(cmd);
        }
    }

    public void Dispose() => _conn.Dispose();
}
