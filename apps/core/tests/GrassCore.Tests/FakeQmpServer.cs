using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GrassCore.Tests;

/// <summary>本地 TCP 假 QMP 服务（与 QmpClientTests 共用）。可在子类扩展命令行为。</summary>
public sealed class FakeQmpServer : IDisposable
{
    private readonly TcpListener _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;

    public FakeQmpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>收到的命令记录（断言 -incoming、migrate 目标等用）。</summary>
    public List<string> ExecutedCommands { get; } = new();

    public Func<string, JsonDocument, Task<string>> OnCommand { get; set; } = (_, _) =>
        Task.FromResult("""{"return":{}}""");

    /// <summary>接受连接并处理命令；QMP quit 后继续等待下一次连接（挂起→恢复会重连）。</summary>
    public async Task AcceptAsync()
    {
        while (true)
        {
            await AcceptOnceAsync();
        }
    }

    private async Task AcceptOnceAsync()
    {
        _client = await _listener.AcceptTcpClientAsync();
        var stream = _client.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine("""{"QMP":{"version":{"qemu":{"major":11,"minor":1,"micro":1},"package":""},"capabilities":[]}}""");
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("execute", out var exec)) continue;
                var cmd = exec.GetString()!;
                lock (ExecutedCommands) ExecutedCommands.Add(cmd);
                var resp = await OnCommand(cmd, doc);
                // 保留 id
                var id = doc.RootElement.GetProperty("id").GetInt32();
                var withId = resp.Insert(resp.Length - 1, $",\"id\":{id}");
                _writer.WriteLine(withId);
                if (cmd == "quit") break;
            }
        });
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _client?.Dispose();
        _listener.Stop();
    }
}
