using System.Net;
using System.Net.Sockets;
using System.Text;
using GrassCore.Qemu;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// QMP 客户端与协议测试：用本地 TCP 假 QMP 服务驱动（greeting → capabilities → 命令/事件）。
/// 验证握手、错误转换、事件分发、SPICE 端口查询与挂起序列。
/// </summary>
public class QmpClientTests
{
    private sealed class FakeQmp : IDisposable
    {
        private readonly TcpListener _listener;
        private TcpClient? _client;

        public FakeQmp()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task<StreamReader> AcceptAsync()
        {
            _client = await _listener.AcceptTcpClientAsync();
            var s = _client.GetStream();
            return new StreamReader(s, Encoding.UTF8, leaveOpen: true);
        }

        public void WriteLine(string line)
        {
            _client!.GetStream().Write(Encoding.UTF8.GetBytes(line + "\n"));
            _client.GetStream().Flush();
        }

        public void Dispose()
        {
            _client?.Dispose();
            _listener.Stop();
        }
    }

    private static async Task RunWithFakeQmp(Func<QmpClient, Task> body)
    {
        using var fake = new FakeQmp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var reader = await fake.AcceptAsync();
            fake.WriteLine("""{"QMP":{"version":{"qemu":{"major":11,"minor":1,"micro":1},"package":""},"capabilities":[]}}""");
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var doc = System.Text.Json.JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("execute", out var exec)) continue;
                var id = doc.RootElement.GetProperty("id").GetInt32();
                switch (exec.GetString())
                {
                    case "qmp_capabilities":
                        fake.WriteLine($$"""{"return":{},"id":{{id}}}""");
                        break;
                    case "query-spice":
                        fake.WriteLine($$"""{"return":{"port":59312,"family":"ipv4"},"id":{{id}}}""");
                        break;
                    case "query-migrate":
                        fake.WriteLine($$"""{"return":{"status":"completed"},"id":{{id}}}""");
                        break;
                    case "stop":
                        fake.WriteLine($$"""{"return":{},"id":{{id}}}""");
                        // stop 后异步事件
                        fake.WriteLine("""{"timestamp":{"seconds":1,"microseconds":0},"event":"STOP"}""");
                        break;
                    case "migrate":
                        fake.WriteLine($$"""{"return":{},"id":{{id}}}""");
                        break;
                    case "system_powerdown":
                        fake.WriteLine($$"""{"return":{},"id":{{id}}}""");
                        break;
                    default:
                        fake.WriteLine($$"""{"error":{"class":"CommandNotFound","desc":"unknown command"},"id":{{id}}}""");
                        break;
                }
            }
        });

        using var qmp = new QmpClient(new TcpTransport("127.0.0.1", fake.Port));
        await qmp.ConnectAsync(cts.Token);
        await body(qmp);
        await Task.WhenAny(serverTask, Task.Delay(2000));
    }

    [Fact]
    public async Task Handshake_AfterGreeting_CapabilitiesAccepted()
    {
        await RunWithFakeQmp(async qmp =>
        {
            // ConnectAsync 已完成 greeting + qmp_capabilities；能执行命令即成功
            var port = await qmp.QuerySpicePortAsync();
            Assert.Equal(59312, port);
        });
    }

    [Fact]
    public async Task Error_BecomesException_WithCommandAndDesc()
    {
        await RunWithFakeQmp(async qmp =>
        {
            var ex = await Assert.ThrowsAsync<QmpException>(() => qmp.ExecuteAsync("not-a-command"));
            Assert.Contains("not-a-command", ex.Message);
            Assert.Contains("unknown command", ex.Message);
        });
    }

    [Fact]
    public async Task Events_AreDispatchedWhileWaitingForResponse()
    {
        await RunWithFakeQmp(async qmp =>
        {
            var sawStop = new TaskCompletionSource<System.Text.Json.JsonElement>();
            qmp.On("STOP", e => sawStop.TrySetResult(e));
            var stopped = await qmp.StopAsync(); // 服务端在响应 stop 之后异步发送 STOP 事件
            Assert.Equal("{}", stopped.ToString());
            // 常驻读循环：事件即时派发，无需等待下一条命令
            var finished = await qmp.MigrationStatusAsync();
            Assert.Equal("completed", finished);
            var winner = await Task.WhenAny(sawStop.Task, Task.Delay(3000));
            Assert.Equal(sawStop.Task, winner); // STOP 事件已分派
        });
    }

    [Fact]
    public async Task Suspend_Sequence_StopThenMigrateToFile()
    {
        await RunWithFakeQmp(async qmp =>
        {
            await qmp.StopAsync();
            await qmp.MigrateToFileAsync(@"C:\VM\state.dat");
            Assert.Equal("completed", await qmp.MigrationStatusAsync());
        });
    }
}
