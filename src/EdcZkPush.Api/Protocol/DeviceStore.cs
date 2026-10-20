using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EdcZkPush.Protocol;

public sealed class DeviceSession
{
    public required string SerialNumber { get; init; }
    public string RegistryCode { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? PushVerFromInit { get; set; }
    public string? RemoteIp { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? RegisteredAt { get; set; }
    public DateTimeOffset? ConfigDownloadedAt { get; set; }
    public string? RegistryBody { get; set; }
    public DeviceCapabilities? Capabilities { get; set; }
    public string? LastRtState { get; set; }
    public long HeartbeatCount;
    public long RtLogCount;
    public long RtStateCount;
    public string? LastInfo { get; set; }

    /// <summary>Token per the document: MD5(RegistryCode + SN + SessionID). Real C3-400 Plus firmware uses MD5(RegistryCode + SN + cookie timestamp) instead.</summary>
    public string ExpectedToken
    {
        get
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(RegistryCode + SerialNumber + SessionId));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public bool IsRegistered => RegisteredAt.HasValue;
}

/// <summary>In-memory list of devices that have talked to us.</summary>
public sealed class DeviceStore
{
    private readonly ConcurrentDictionary<string, DeviceSession> _devices = new(StringComparer.OrdinalIgnoreCase);

    public DeviceSession Touch(string sn, string? remoteIp)
    {
        var s = _devices.GetOrAdd(sn.Trim(), key => new DeviceSession { SerialNumber = key });
        s.LastSeen = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(remoteIp))
        {
            s.RemoteIp = remoteIp;
        }

        return s;
    }

    public DeviceSession? Get(string sn) => _devices.TryGetValue(sn.Trim(), out var s) ? s : null;

    public IReadOnlyList<DeviceSession> All() => _devices.Values.OrderBy(d => d.SerialNumber).ToList();

    public static string NewCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }
}
