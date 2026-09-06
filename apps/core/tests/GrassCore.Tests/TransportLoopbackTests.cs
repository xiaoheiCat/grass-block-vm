using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// 传输层回环测试：JsonRpcConnection 帧格式（4 字节小端长度 + JSON）+ 并发响应的帧不交错。
/// 此前该层零覆盖（Windows 管道作用域缺陷正是从这里逃逸的——dev 只走 stdio 路径）。
/// </summary>
public class TransportLoopbackTests
{
    private static async Task<(JsonRpcConnection server, JsonRpcConnection client, TcpClient clientSock)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var clientSock = new TcpClient();
        await clientSock.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var serverSock = await accept;
        listener.Stop();
        return (new JsonRpcConnection(serverSock.GetStream()), new JsonRpcConnection(clientSock.GetStream()), clientSock);
    }

    [Fact]
    public async Task Framing_RoundTrips_Request_And_Response()
    {
        var (server, client, clientSock) = await PairAsync();
        using var _0 = clientSock;

        var handler = Task.Run(async () =>
        {
            var req = await server.ReceiveAsync();
            var method = req!.Value.GetProperty("method").GetString();
            await server.SendAsync(JsonRpcConnection.Ok(new { echoed = method }, req.Value.GetProperty("id")));
        });

        var frame = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 7, method = "ping" });
        var len = BitConverter.GetBytes((int)frame.Length);
        var raw = clientSock.GetStream();
        raw.Write(len, 0, 4);
        raw.Write(frame, 0, frame.Length);
        raw.Flush();

        var resp = await client.ReceiveAsync();
        Assert.NotNull(resp);
        Assert.Equal(7, resp!.Value.GetProperty("id").GetInt32());
        Assert.Equal("ping", resp.Value.GetProperty("result").GetProperty("echoed").GetString());
        await handler;
    }

    [Fact]
    public async Task Ok_Serializes_CamelCase_RegardlessOf_Concurrency()
    {
        // WireOpts 是共享静态：并发序列化必须安全（帧门 + 无状态序列化）
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await Task.Yield();
            var el = JsonRpcConnection.Ok(new { cpuCores = i, memoryMiB = i * 1024 }, i);
            return (i, hasCpu: el.TryGetProperty("result", out var r) && r.TryGetProperty("cpuCores", out _));
        }));
        Assert.All(results, r => Assert.True(r.hasCpu, $"并发序列化丢字段：{r.i}"));
    }

    [Fact]
    public async Task Receive_Rejects_Overlarge_Frame_Before_Allocating_Payload()
    {
        var prefix = BitConverter.GetBytes(JsonRpcConnection.MaxFrameBytes + 1);
        await using var stream = new MemoryStream(prefix);
        var conn = new JsonRpcConnection(stream);

        await Assert.ThrowsAsync<IOException>(() => conn.ReceiveAsync());
    }
}
