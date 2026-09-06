using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// GrassSpiceHelper 是独立于 QEMU/GrassCore 的本机能力进程。当前托管实现
// 先把协议边界、令牌握手和能力协商做成可验证的最小闭环；音频/USB 等需要
// 原生 spice-gtk 的能力只有在真正随安装包提供对应后端时才会声明。
var options = ParseArgs(args);
if (options.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(options.Token))
{
    Console.Error.WriteLine("usage: GrassSpiceHelper --port <1..65535> --token <random> --session <id>");
    return 2;
}

using var listener = new TcpListener(IPAddress.Loopback, options.Port);
try
{
    listener.Start();
}
catch (SocketException e)
{
    Console.Error.WriteLine($"无法监听 Helper 端口：{e.Message}");
    return 3;
}

Console.WriteLine($"READY {options.Port}");
Console.Out.Flush();
const int maxConnections = 32;
using var connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
while (true)
{
    TcpClient client;
    try { client = await listener.AcceptTcpClientAsync(); }
    catch (SocketException) { break; }
    catch (ObjectDisposedException) { break; }
    if (!connectionSlots.Wait(0))
    {
        client.Dispose();
        continue;
    }
    _ = Task.Run(async () =>
    {
        try { await HandleClientAsync(client, options); }
        finally { connectionSlots.Release(); }
    });
}
return 0;

static async Task HandleClientAsync(TcpClient client, HelperOptions options)
{
    using (client)
    {
        var stream = client.GetStream();
        WebSocket socket;
        try
        {
            // 未完成握手的本机连接不能无限占住 Helper 的连接槽位。
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = await ReadHttpHeadersAsync(stream, handshakeTimeout.Token);
            if (!request.TryGetValue("upgrade", out var upgrade)
                || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                || !request.TryGetValue("sec-websocket-key", out var key))
                return;
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes);
            socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        }
        catch
        {
            return;
        }
        await RunWebSocketClientAsync(socket, options);
    }
}

static async Task<Dictionary<string, string>> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
{
    var bytes = new List<byte>(4096);
    var one = new byte[1];
    while (bytes.Count < 16 * 1024)
    {
        var n = await stream.ReadAsync(one, ct);
        if (n == 0) throw new IOException("连接已关闭。");
        bytes.Add(one[0]);
        if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
    }
    if (bytes.Count >= 16 * 1024) throw new IOException("WebSocket 请求头过大。");
    var text = Encoding.ASCII.GetString(bytes.ToArray());
    var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in lines.Skip(1))
    {
        var colon = line.IndexOf(':');
        if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
    }
    return headers;
}

static async Task RunWebSocketClientAsync(WebSocket socket, HelperOptions options)
{
    using (socket)
    {
        var buffer = new byte[64 * 1024];
        var authenticated = false;
        const int maxMessageBytes = 1 * 1024 * 1024;
        // 握手完成后仍未认证的连接不能无限占用 Helper 槽位：本机任意进程
        // 都能完成普通 WebSocket 101，但只有持有会话 token 的渲染器才应长期留存。
        using var authenticationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult received;
            using var message = new MemoryStream();
            try
            {
                // WebSocket 允许把一条文本消息拆成任意数量的帧；必须累积到
                // EndOfMessage 才能交给 JSON 解析器，且设置总量上限避免内存滥用。
                var receiveToken = authenticated ? CancellationToken.None : authenticationTimeout.Token;
                do
                {
                    received = await socket.ReceiveAsync(buffer, receiveToken);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (received.Count > 0) message.Write(buffer, 0, received.Count);
                    if (message.Length > maxMessageBytes)
                    {
                        try { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", CancellationToken.None); } catch { }
                        return;
                    }
                }
                while (!received.EndOfMessage);
            }
            catch { break; }
            if (received.MessageType == WebSocketMessageType.Close) break;
            if (received.MessageType != WebSocketMessageType.Text || message.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
                var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
                object result;
                if (method == "hello")
                {
                    authenticated = Authenticate(root, options);
                    result = authenticated
                        ? new { sessionId = options.Session, capabilities = Array.Empty<string>() }
                        : new { error = "unauthorized" };
                }
                else if (!authenticated)
                    result = new { error = "unauthorized" };
                else
                    result = method switch
                    {
                        "getCapabilities" => new { capabilities = Array.Empty<string>() },
                        "ping" => new { pong = true },
                        _ => new { error = "unknown-method" },
                    };
                await SendAsync(socket, new { jsonrpc = "2.0", id, result });
                // 错误 token 连接立即释放槽位；不允许攻击者通过反复发送无效
                // hello 把未认证连接保持成长期占位。
                if (method == "hello" && !authenticated) return;
            }
            catch
            {
                await SendAsync(socket, new { jsonrpc = "2.0", id = (object?)null, error = "invalid-request" });
            }
        }
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }
}

static bool Authenticate(JsonElement root, HelperOptions options)
{
    var token = root.TryGetProperty("params", out var p)
        && p.TryGetProperty("token", out var t) ? t.GetString() : null;
    return CryptographicEquals(token, options.Token);
}

static bool CryptographicEquals(string? a, string b)
{
    if (a is null || a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}

static async Task SendAsync(WebSocket socket, object value)
{
    var json = JsonSerializer.Serialize(value);
    var bytes = Encoding.UTF8.GetBytes(json);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

static HelperOptions ParseArgs(string[] args)
{
    var port = 0;
    var token = "";
    var session = "";
    for (var i = 0; i + 1 < args.Length; i++)
    {
        switch (args[i])
        {
            case "--port" when int.TryParse(args[++i], out var parsed): port = parsed; break;
            case "--token": token = args[++i]; break;
            case "--session": session = args[++i]; break;
        }
    }
    return new HelperOptions(port, token, session);
}

sealed record HelperOptions(int Port, string Token, string Session);
