using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GrassCore.Qemu;

/// <summary>QMP 传输抽象：Windows Named Pipe；开发/测试用 TCP 或内存双工流。</summary>
public interface IQmpTransport : IDisposable
{
    Stream Stream { get; }
    string Describe();
}

public sealed class WindowsNamedPipeTransport : IQmpTransport
{
    private readonly NamedPipeClientStream _pipe;
    public WindowsNamedPipeTransport(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(5000);
    }
    public Stream Stream => _pipe;
    public string Describe() => "named-pipe:" + _pipe.SafePipeHandle;
    public void Dispose() => _pipe.Dispose();
}

public sealed class TcpTransport : IQmpTransport
{
    private readonly TcpClient _client;
    public TcpTransport(string host, int port)
    {
        _client = new TcpClient(host, port);
    }
    public Stream Stream => _client.GetStream();
    public string Describe() => $"tcp:{_client.Client.RemoteEndPoint}";
    public void Dispose() => _client.Dispose();
}

public sealed class StreamTransport : IQmpTransport
{
    public StreamTransport(Stream duplex) => Stream = duplex;
    public Stream Stream { get; }
    public string Describe() => "stream";
    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// QMP 客户端：JSON 行协议（greeting → qmp_capabilities → commands + asynchronous events）。
/// UI 或 GrassCore 崩溃不杀 QEMU；Core 重启后凭 session.json + 管道重连无损接管。
/// </summary>
public sealed class QmpClient : IDisposable
{
    private readonly IQmpTransport _transport;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, Action<JsonElement>> _eventHandlers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private Task? _readLoop;
    private volatile bool _disposed;
    private int _nextId = 1;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public QmpClient(IQmpTransport transport)
    {
        _transport = transport;
        _reader = new StreamReader(transport.Stream, Encoding.UTF8);
        _writer = new StreamWriter(transport.Stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public event Action<JsonElement, string>? UnhandledEvent;

    /// <summary>读 greeting 并完成能力协商（QMP 握手）。</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var greeting = await ReadMessageAsync(ct).ConfigureAwait(false);
        if (!greeting.RootElement.TryGetProperty("QMP", out _))
            throw new QmpException("对端不是 QMP 服务（缺少 greeting）。");
        // 读循环从这里接管接收侧（greeting 之后的全部消息）
        _readLoop = Task.Run(ReadLoopAsync);
        var resp = await ExecuteRawAsync(new { execute = "qmp_capabilities" }, ct).ConfigureAwait(false);
        if (resp.RootElement.TryGetProperty("error", out _))
            throw new QmpException("qmp_capabilities 被拒绝。");
    }

    /// <summary>执行 QMP 命令，返回 return 节点；error 转为异常。</summary>
    public async Task<JsonElement> ExecuteAsync(string command, object? args = null, CancellationToken ct = default)
    {
        var payload = args is null
            ? (object)new { execute = command }
            : new Dictionary<string, object?> { ["execute"] = command, ["arguments"] = args };
        var resp = await ExecuteRawAsync(payload, ct).ConfigureAwait(false);
        if (resp.RootElement.TryGetProperty("error", out var err))
            throw new QmpException($"QMP {command} 失败：{err.GetProperty("desc").GetString()}");
        return resp.RootElement.TryGetProperty("return", out var ret) ? ret : default;
    }

    /// <summary>注册事件处理（BLOCK_JOB_COMPLETED / SPICE_CONNECTED / DEVICE_DELETED …）。</summary>
    public void On(string eventName, Action<JsonElement> handler) => _eventHandlers[eventName] = handler;

    private async Task<JsonDocument> ExecuteRawAsync(object payload, CancellationToken ct)
    {
        int id;
        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            id = _nextId++;
            _pending[id] = tcs;
            var json = JsonSerializer.Serialize(payload);
            // 注入 id（QMP 需要；事件没有 id）
            json = json.Insert(json.Length - 1, $",\"id\":{id}");
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
        // 响应由常驻读循环匹配 id 后完成；ct 取消时清理 pending
        using var reg = ct.Register(() => _pending.TryRemove(id, out _));
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>常驻读循环：带 id 的响应完成对应命令；事件即时派发（无需命令在途）。</summary>
    private async Task ReadLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                var msg = await ReadMessageAsync(CancellationToken.None).ConfigureAwait(false);
                using (msg)
                {
                    if (msg.RootElement.TryGetProperty("id", out var msgId) &&
                        _pending.TryRemove(msgId.GetInt32(), out var tcs))
                    {
                        // 文档所有权转移给等待方（重解析一份，原始文档在本作用域释放）
                        tcs.TrySetResult(JsonDocument.Parse(msg.RootElement.GetRawText()));
                    }
                    else
                    {
                        DispatchEvent(msg);
                    }
                }
            }
            catch
            {
                // 连接关闭/损坏：让所有在途命令失败并停止循环
                foreach (var kv in _pending) kv.Value.TrySetException(new QmpException("QMP 连接已关闭。"));
                _pending.Clear();
                return;
            }
        }
    }

    private void DispatchEvent(JsonDocument msg)
    {
        if (msg.RootElement.TryGetProperty("event", out var ev))
        {
            var name = ev.GetString()!;
            if (_eventHandlers.TryGetValue(name, out var h)) h(msg.RootElement);
            else UnhandledEvent?.Invoke(msg.RootElement, name);
        }
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken ct)
    {
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null) throw new QmpException("QMP 连接已关闭。");
        return JsonDocument.Parse(line);
    }

    public void Dispose()
    {
        _disposed = true;
        _writer.Dispose();
        _reader.Dispose();
        _transport.Dispose();
        _sendLock.Dispose();
    }
}

public sealed class QmpException(string message) : Exception(message);

/// <summary>QMP 高层操作（产品语义 → QMP 命令）。</summary>
public static class QmpOps
{
    /// <summary>SPICE 实际监听端口（-spice port=0 自动分配后查询）。</summary>
    public static async Task<int> QuerySpicePortAsync(this QmpClient qmp, CancellationToken ct = default)
    {
        var ret = await qmp.ExecuteAsync("query-spice", null, ct);
        return ret.GetProperty("port").GetInt32();
    }

    /// <summary>正常关机 = ACPI 电源按钮请求（永远先于强制关机）。</summary>
    public static Task<JsonElement> AcpiShutdownAsync(this QmpClient qmp, CancellationToken ct = default) =>
        qmp.ExecuteAsync("system_powerdown", null, ct);

    /// <summary>强制关机 = 用户在电源菜单选择并二次确认后才允许调用。</summary>
    public static Task<JsonElement> ForceQuitAsync(this QmpClient qmp, CancellationToken ct = default) =>
        qmp.ExecuteAsync("quit", null, ct);

    /// <summary>挂起第一步：暂停虚拟机（保存状态前的稳定点）。</summary>
    public static Task<JsonElement> StopAsync(this QmpClient qmp, CancellationToken ct = default) =>
        qmp.ExecuteAsync("stop", null, ct);

    /// <summary>挂起第二步：完整运行状态（内存/CPU/设备）迁移到文件。Windows 构建使用 file: 目标。</summary>
    public static Task<JsonElement> MigrateToFileAsync(this QmpClient qmp, string stateFilePath, CancellationToken ct = default) =>
        qmp.ExecuteAsync("migrate", new { uri = $"file:{stateFilePath}" }, ct);

    /// <summary>热插拔换盘：CD/DVD 运行中更换镜像（blockdev-change-medium：filename + read-only-mode）。</summary>
    public static Task<JsonElement> InsertMediumAsync(this QmpClient qmp, string deviceId, string isoPath, CancellationToken ct = default) =>
        qmp.ExecuteAsync("blockdev-change-medium",
            new Dictionary<string, object?>
            {
                ["device"] = deviceId,
                ["filename"] = isoPath,
                ["format"] = "raw",
                ["read-only-mode"] = "read-only",
            }, ct);

    /// <summary>热插拔取出介质（eject 命令；等价于"空光驱"）。</summary>
    public static Task<JsonElement> EjectMediumAsync(this QmpClient qmp, string deviceId, CancellationToken ct = default) =>
        qmp.ExecuteAsync("eject", new Dictionary<string, object?> { ["device"] = deviceId }, ct);

    /// <summary>查询迁移状态（active/completed/failed/cancelled…）。</summary>
    public static async Task<string> MigrationStatusAsync(this QmpClient qmp, CancellationToken ct = default)
    {
        var ret = await qmp.ExecuteAsync("query-migrate", null, ct);
        return ret.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
    }

    /// <summary>查询虚拟机运行状态（running/paused/…；-incoming 恢复完成的判定）。</summary>
    public static async Task<string> VmStatusAsync(this QmpClient qmp, CancellationToken ct = default)
    {
        var ret = await qmp.ExecuteAsync("query-status", null, ct);
        return ret.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
    }
}
