using GrassCore.Library;
using GrassCore.Rpc;
using Xunit;

namespace GrassCore.Tests;

/// <summary>
/// 线上契约测试：C# DTO → JSON-RPC 线格式的字段名必须与 apps/desktop 的 TS 契约（camelCase）
/// 完全一致。UI 测试只覆盖纯函数，这里补上序列化层，防止"测试全绿但 UI 读不到字段"。
/// </summary>
public class WireContractTests
{
    private static System.Text.Json.JsonElement Ok(object result) =>
        JsonRpcConnection.Ok(result, 1).GetProperty("result");

    [Fact]
    public void ScanLibraryResult_UsesCamelCase_OnTheWire()
    {
        var payload = Ok(new GrassCoreService.ScanLibraryResult("/vm", new()
        {
            new("C:\\VM\\A.grassvm", "A", "windows-11", "stopped", 4, 8192, false, false),
        }));
        // TS 契约：libraryRoot / vms[].path|name|osProfileId|state|cpuCores|memoryMiB|locked|hasAutostart
        Assert.True(payload.TryGetProperty("libraryRoot", out _), "缺少 libraryRoot（camelCase）");
        var vm = payload.GetProperty("vms")[0];
        foreach (var field in new[] { "path", "name", "osProfileId", "state", "cpuCores", "memoryMiB", "locked", "hasAutostart" })
            Assert.True(vm.TryGetProperty(field, out _), $"vms[].{field} 缺失或非 camelCase");
    }

    [Fact]
    public void CreateVmResult_And_HostInfo_UseCamelCase()
    {
        var created = Ok(new GrassCoreService.CreateVmResult("C:\\VM\\A.grassvm", "A") { Started = true });
        Assert.True(created.TryGetProperty("path", out _));
        Assert.True(created.TryGetProperty("name", out _));
        Assert.True(created.TryGetProperty("started", out _));

        var host = Ok(new { cpuCores = 8, memoryMiB = 16384 });
        Assert.True(host.TryGetProperty("cpuCores", out _));
        Assert.True(host.TryGetProperty("memoryMiB", out _));
    }

    [Fact]
    public void ConfigJson_DeviceEnums_AreCamelCase()
    {
        // getConfig 返回 config.json 的同一序列化：设备枚举值必须与 TS 端匹配（nat/disconnected/uefi）
        var config = new GrassCore.Config.VmConfiguration { Name = "E", OsProfileId = "ubuntu" };
        config.Devices.Add(new GrassCore.Config.NetworkDevice { Mode = GrassCore.Config.NetworkMode.HostOnly });
        var json = GrassCore.Config.ConfigJson.Serialize(config);
        Assert.Contains("\"hostOnly\"", json);
        Assert.DoesNotContain("HostOnly", json);
    }
}
