using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrassCore.Config;

/// <summary>
/// 设备显示名："磁盘 #1"、"网络 #2"。编号按添加时间生成，一旦分配在该 VM 生命周期内保持稳定；
/// 删除"磁盘 #1"不会把"磁盘 #2"改名，后续新增的磁盘成为"磁盘 #3"。
/// </summary>
public static class DeviceNamer
{
    private static readonly Dictionary<Type, string> FamilyNames = new()
    {
        [typeof(DiskDevice)] = "硬盘",
        [typeof(CdromDevice)] = "CD/DVD",
        [typeof(NetworkDevice)] = "网络",
        [typeof(DisplayDevice)] = "显示器",
        [typeof(AudioDevice)] = "声音",
        [typeof(UsbControllerDevice)] = "USB",
        [typeof(SharedFolderDevice)] = "共享文件夹",
        [typeof(CameraDevice)] = "摄像头",
        [typeof(MicrophoneDevice)] = "麦克风",
        [typeof(TpmDevice)] = "安全芯片",
        [typeof(RawDevice)] = "兼容设备",
    };

    /// <summary>
    /// 显示名 = 家庭名 + #家庭内添加序号（按 createdOrder 排名）。
    /// 序号不随列表删除而漂移：删除"硬盘 #1"后"硬盘 #2"仍叫"硬盘 #2"，新增硬盘成为"硬盘 #3"。
    /// </summary>
    public static string DisplayName(VmConfiguration config, VmDevice device)
    {
        var family = FamilyNames[device.GetType()];
        var index = config.Devices
            .Where(d => FamilyNames[d.GetType()] == family && d.CreatedOrder <= device.CreatedOrder)
            .Count();
        return $"{family} #{index}";
    }

    /// <summary>新增设备时分配 createdOrder（全局递增，不回收）。</summary>
    public static int NextCreatedOrder(VmConfiguration config) =>
        config.Devices.Select(d => d.CreatedOrder).DefaultIfEmpty(0).Max() + 1;
}

/// <summary>配置 JSON 序列化（多态设备 + camelCase）。前端与 Core 共用同一 Schema 契约。</summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new VmDeviceJsonConverter() },
    };

    public static string Serialize(VmConfiguration config) => JsonSerializer.Serialize(config, Options);

    public static VmConfiguration Deserialize(string json) =>
        JsonSerializer.Deserialize<VmConfiguration>(json, Options)
        ?? throw new JsonException("config.json 内容为空。");
}

/// <summary>设备多态序列化：type 字段鉴别具体设备类型。</summary>
public sealed class VmDeviceJsonConverter : JsonConverter<VmDevice>
{
    private static readonly Dictionary<string, Type> Types = new()
    {
        ["disk"] = typeof(DiskDevice),
        ["cdrom"] = typeof(CdromDevice),
        ["network"] = typeof(NetworkDevice),
        ["display"] = typeof(DisplayDevice),
        ["audio"] = typeof(AudioDevice),
        ["usb"] = typeof(UsbControllerDevice),
        ["sharedFolder"] = typeof(SharedFolderDevice),
        ["camera"] = typeof(CameraDevice),
        ["microphone"] = typeof(MicrophoneDevice),
        ["tpm"] = typeof(TpmDevice),
        ["raw"] = typeof(RawDevice),
    };

    public override VmDevice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var typeToken = doc.RootElement.TryGetProperty("deviceType", out var t) ? t.GetString() : null;
        if (typeToken is null || !Types.TryGetValue(typeToken, out var type))
            throw new JsonException($"未知设备类型：{typeToken}");
        return (VmDevice?)doc.RootElement.Deserialize(type, WithoutDeviceConverter(options));
    }

    public override void Write(Utf8JsonWriter writer, VmDevice value, JsonSerializerOptions options)
    {
        var type = value.GetType();
        writer.WriteStartObject();
        writer.WriteString("deviceType", Types.First(kv => kv.Value == type).Key);
        foreach (var prop in type.GetProperties().Where(p => p.Name != nameof(VmDevice.DeviceType)))
        {
            var name = options.PropertyNamingPolicy?.ConvertName(prop.Name) ?? prop.Name;
            var val = prop.GetValue(value);
            if (val is null && options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull) continue;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, val, val?.GetType() ?? typeof(object), WithoutDeviceConverter(options));
        }
        writer.WriteEndObject();
    }

    private static JsonSerializerOptions WithoutDeviceConverter(JsonSerializerOptions options)
    {
        if (_cache.TryGetValue(options, out var cached)) return cached;
        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
        {
            if (clone.Converters[i] is VmDeviceJsonConverter) clone.Converters.RemoveAt(i);
        }
        _cache[options] = clone;
        return clone;
    }

    private static readonly Dictionary<JsonSerializerOptions, JsonSerializerOptions> _cache = new();
}
