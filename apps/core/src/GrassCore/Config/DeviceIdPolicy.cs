namespace GrassCore.Config;

/// <summary>
/// 设备标识是内部持久化 UUID，同时也会出现在快照磁盘文件名和元数据键中。
/// 所有外部输入必须经过这里校验，避免把任意字符串带入文件系统路径。
/// </summary>
public static class DeviceIdPolicy
{
    /// <summary>接受常见的连字符/无连字符 UUID 文本形式，拒绝路径和其他任意标识。</summary>
    public static bool IsValid(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && (Guid.TryParseExact(deviceId, "D", out _)
            || Guid.TryParseExact(deviceId, "N", out _));

    /// <summary>网卡地址必须是六组十六进制字节，且不能是组播/广播地址。</summary>
    public static bool IsValidMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return false;
        var parts = mac.Split(':', '-');
        if (parts.Length != 6 || parts.Any(p => p.Length != 2)) return false;
        Span<byte> bytes = stackalloc byte[6];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                return false;
            bytes[i] = parsed;
        }
        if ((bytes[0] & 1) != 0) return false;
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] != 0xFF) return true;
        return false;
    }
}
