using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GrassCore.Rpc;

/// <summary>
/// JSON-RPC 2.0。UI ↔ GrassCore 走 Windows Named Pipe（不占 TCP 端口、可用 Windows ACL）。
/// 非 Windows 开发机上以 stdio 承载同一协议，便于本地联调与测试。
/// </summary>
public sealed class JsonRpcConnection
{
    private readonly Stream _stream;

    public JsonRpcConnection(Stream stream) => _stream = stream;

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>并发请求的响应可能同时回写：整帧（长度+JSON+flush）持锁串行，防止帧交错。</summary>
    public async Task SendAsync(JsonElement response, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        // 帧格式：4 字节小端长度 + JSON（简单可靠，避免粘包）
        var len = BitConverter.GetBytes((int)bytes.Length);
        await _sendGate.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(len, ct);
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<JsonElement?> ReceiveAsync(CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactAsync(lenBuf, ct)) return null;
        var len = BitConverter.ToInt32(lenBuf);
        if (len <= 0 || len > 64 * 1024 * 1024) throw new IOException("非法帧长度。");
        var buf = new byte[len];
        if (!await ReadExactAsync(buf, ct)) return null;
        return JsonDocument.Parse(buf).RootElement;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var off = 0;
        while (off < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public static JsonElement Ok(object? result, object id) => ToElement(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    });

    public static JsonElement Error(int code, string message, object id) => ToElement(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
    });

    /// <summary>RPC 线上格式：camelCase 属性 + camelCase 枚举（与 apps/desktop 的 TS 契约一致）。</summary>
    internal static readonly JsonSerializerOptions WireOpts = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    private static JsonElement ToElement(object obj)
    {
        var json = JsonSerializer.Serialize(obj, WireOpts);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}

/// <summary>IPC 传输端点选择：Windows 用 Named Pipe；其他平台（开发）用 stdio。</summary>
public static class Transport
{
    public const string DefaultPipeName = "grassvm-core";

    /// <summary>当前活跃连接数（空闲退出判定用）。</summary>
    public static int ActiveConnections => _activeConnections;
    private static int _activeConnections;

    public static async Task RunServerAsync(Func<JsonRpcConnection, Task> handler, string? pipeName = null, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = pipeName ?? DefaultPipeName;
            while (!ct.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                var conn = new JsonRpcConnection(server);
                Interlocked.Increment(ref _activeConnections);
                _ = Task.Run(async () =>
                {
                    try { await handler(conn); }
                    finally { Interlocked.Decrement(ref _activeConnections); }
                }, ct);
            }
        }
        else
        {
            var conn = new JsonRpcConnection(Console.OpenStandardInput() is { } i && Console.IsInputRedirected ? new DualStream(i, Console.OpenStandardOutput()) : new DualStream(Console.OpenStandardInput(), Console.OpenStandardOutput()));
            Interlocked.Increment(ref _activeConnections);
            try { await handler(conn); }
            finally { Interlocked.Decrement(ref _activeConnections); }
        }
    }

    /// <summary>合并输入/输出两个半双工流的简单 Stream。</summary>
    private sealed class DualStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => write.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
