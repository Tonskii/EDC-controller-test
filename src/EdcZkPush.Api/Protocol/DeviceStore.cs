using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

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

    /// <summary>Parameter values reported by the controller in GET OPTIONS answers (override registry values).</summary>
    public Dictionary<string, string> LearnedOptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DevID set at runtime (PUT /api/devices/{sn}/devid), saved in the devices folder.</summary>
    public int? DevId { get; set; }

    /// <summary>The "unknown cookie token" note is printed once per session, not on every poll.</summary>
    public bool TokenNoteShown { get; set; }

    /// <summary>True when the capabilities came from the saved registry file, not from a registration since server start.</summary>
    public bool CapabilitiesFromCache { get; set; }

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
    private readonly string _cacheFolder;

    public DeviceStore(IOptions<PushOptions> options)
    {
        _cacheFolder = options.Value.DeviceCacheFolder;
    }

    public DeviceSession Touch(string sn, string? remoteIp)
    {
        var s = _devices.GetOrAdd(sn.Trim(), CreateFromCache);
        s.LastSeen = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(remoteIp))
        {
            s.RemoteIp = remoteIp;
        }

        return s;
    }

    public DeviceSession? Get(string sn)
    {
        sn = sn.Trim();
        if (_devices.TryGetValue(sn, out var s))
        {
            return s;
        }

        return File.Exists(CachePath(sn)) ? _devices.GetOrAdd(sn, CreateFromCache) : null;
    }

    /// <summary>Remember the registry body so capability-based decisions (DevID, card table, dates) survive a server restart.</summary>
    public void SaveRegistry(DeviceSession session)
    {
        if (string.IsNullOrWhiteSpace(_cacheFolder) || string.IsNullOrEmpty(session.RegistryBody))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_cacheFolder);
            File.WriteAllText(CachePath(session.SerialNumber), session.RegistryBody);
        }
        catch (IOException)
        {
        }
    }

    public void SaveDevId(DeviceSession session)
    {
        if (string.IsNullOrWhiteSpace(_cacheFolder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_cacheFolder);
            var path = DevIdPath(session.SerialNumber);
            if (session.DevId.HasValue)
            {
                File.WriteAllText(path, session.DevId.Value.ToString());
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private DeviceSession CreateFromCache(string sn)
    {
        var session = new DeviceSession { SerialNumber = sn };
        try
        {
            var devIdPath = DevIdPath(sn);
            if (!string.IsNullOrWhiteSpace(_cacheFolder) && File.Exists(devIdPath) &&
                int.TryParse(File.ReadAllText(devIdPath).Trim(), out var devId))
            {
                session.DevId = devId;
            }

            var path = CachePath(sn);
            if (!string.IsNullOrWhiteSpace(_cacheFolder) && File.Exists(path))
            {
                session.RegistryBody = File.ReadAllText(path);
                session.Capabilities = DeviceCapabilities.Parse(session.RegistryBody);
                session.CapabilitiesFromCache = true;
            }
        }
        catch (IOException)
        {
        }

        return session;
    }

    private string DevIdPath(string sn) => Path.Combine(_cacheFolder ?? "", $"{string.Concat(sn.Where(char.IsLetterOrDigit))}.devid.txt");

    private string CachePath(string sn)
    {
        var safe = string.Concat(sn.Where(char.IsLetterOrDigit));
        return Path.Combine(_cacheFolder ?? "", $"{safe}.registry.txt");
    }

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
