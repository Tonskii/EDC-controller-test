using EdcZkPush.Logging;
using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>Connected controllers, their capabilities, the command queue and the log switches.</summary>
[Route("api")]
[Tags("1 · Devices, commands & logging")]
public sealed class DevicesController : ApiBase
{
    private readonly TrafficLog _log;

    public DevicesController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o, TrafficLog log) : base(d, q, o)
    {
        _log = log;
    }

    /// <summary>All controllers that have contacted this server.</summary>
    [HttpGet("devices")]
    public IActionResult List() => Ok(Devices.All().Select(Summary));

    /// <summary>One controller: handshake state, raw registry values and what they mean for the commands.</summary>
    [HttpGet("devices/{sn}")]
    public IActionResult Get(string sn)
    {
        var d = Devices.Get(sn);
        if (d == null) return NotFound(new { error = $"{sn} has not connected." });

        var caps = d.Capabilities;
        return Ok(new
        {
            summary = Summary(d),
            decisions = caps == null ? new List<string> { "Device has not registered yet (no /iclock/registry)." } : CapabilityReport.Decisions(caps, Options).ToList(),
            registry = caps?.Raw,
            accSupportFunList = caps == null ? null : CapabilityReport.Bits(caps).ToList(),
            lastRtState = d.LastRtState,
            lastOptionsUpload = d.LastInfo,
            expectedToken = d.ExpectedToken
        });
    }

    /// <summary>Recent raw traffic of a controller (newest first), same content as the console/file log.</summary>
    [HttpGet("devices/{sn}/traffic")]
    public IActionResult Traffic(string sn, [FromQuery] int take = 50, [FromQuery] bool includeHeartbeat = false) =>
        Ok(_log.Recent(sn, 1000)
            .Where(r => includeHeartbeat || r.Kind is not (TrafficKind.Heartbeat or TrafficKind.RtState))
            .Take(take));

    /// <summary>Cancel all queued (not yet sent) commands of a controller.</summary>
    [HttpDelete("devices/{sn}/queue")]
    public IActionResult ClearQueue(string sn) => Ok(new { cancelled = Queue.CancelOpen(sn) });

    /// <summary>Command history (filter by sn, or only open = queued/sent/pending).</summary>
    [HttpGet("commands")]
    public IActionResult Commands([FromQuery] string? sn = null, [FromQuery] bool openOnly = false) =>
        Ok(Queue.ForDevice(sn, openOnly).Select(View));

    /// <summary>
    /// One command with status, return code, meaning and any rows the device uploaded for it (queries).
    /// waitSeconds &gt; 0 blocks until the command finished or the time is up.
    /// </summary>
    [HttpGet("commands/{id:int}")]
    public async Task<IActionResult> Command(int id, [FromQuery] int waitSeconds = 0, CancellationToken ct = default)
    {
        var cmd = Queue.Get(id);
        if (cmd == null) return NotFound(new { error = $"Command {id} not found (server restarted?)." });

        var until = DateTimeOffset.Now.AddSeconds(Math.Clamp(waitSeconds, 0, 120));
        while (cmd.Status is CommandStatus.Queued or CommandStatus.Sent && DateTimeOffset.Now < until)
        {
            await Task.Delay(250, ct);
        }

        return Ok(View(cmd));
    }

    /// <summary>Current console/file log switches.</summary>
    [HttpGet("logging")]
    public IActionResult GetLogging() => Ok(_log.Options);

    /// <summary>Change log switches at runtime (same as the console hotkeys H / S / B / D).</summary>
    /// <remarks>Example: hide heartbeats and only print rtstate when a door changes: {"showHeartbeat": false, "rtState": "changes"}</remarks>
    [HttpPut("logging")]
    public IActionResult SetLogging([FromBody] LoggingSettings s)
    {
        var o = _log.Options;
        if (s.ShowHeartbeat.HasValue) o.ShowHeartbeat = s.ShowHeartbeat.Value;
        if (!string.IsNullOrWhiteSpace(s.RtState))
        {
            if (s.RtState is not ("all" or "changes" or "none")) return BadRequest(new { error = "rtState must be all, changes or none." });
            o.RtState = s.RtState;
        }

        if (s.ShowHeaders.HasValue) o.ShowHeaders = s.ShowHeaders.Value;
        if (s.ShowBodies.HasValue) o.ShowBodies = s.ShowBodies.Value;
        if (s.MaxBodyChars.HasValue) o.MaxBodyChars = Math.Max(100, s.MaxBodyChars.Value);
        _log.Info($"[api] logging changed: heartbeat={o.ShowHeartbeat} rtstate={o.RtState} headers={o.ShowHeaders} bodies={o.ShowBodies}", ConsoleColor.White);
        return Ok(o);
    }

    private static object Summary(DeviceSession d) => new
    {
        serialNumber = d.SerialNumber,
        remoteIp = d.RemoteIp,
        deviceName = d.Capabilities?.DeviceName,
        deviceType = d.Capabilities?.DeviceType,
        firmware = d.Capabilities?.FirmwareVersion,
        pushVersion = d.Capabilities?.PushVersion ?? d.PushVerFromInit,
        doors = d.Capabilities?.LockCount,
        registered = d.RegisteredAt,
        capabilitiesSource = d.Capabilities == null ? "unknown (not registered yet)" : d.CapabilitiesFromCache ? "saved registry file" : "registration",
        handshakeComplete = d.ConfigDownloadedAt,
        lastSeen = d.LastSeen,
        heartbeats = d.HeartbeatCount,
        events = d.RtLogCount,
        stateUpdates = d.RtStateCount
    };

    private static object View(DeviceCommand c) => new
    {
        id = c.Id,
        serialNumber = c.SerialNumber,
        label = c.Label,
        status = c.Status.ToString(),
        wire = c.WireVisible,
        queuedAt = c.QueuedAt,
        sentAt = c.SentAt,
        sendCount = c.SendCount,
        completedAt = c.CompletedAt,
        returnCode = c.ReturnCode,
        meaning = c.ReturnMeaning,
        resultRecord = c.ResultRecord,
        queryData = c.QueryData
    };
}
