using System.Globalization;
using System.Text;
using EdcZkPush.Logging;
using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>
/// Endpoints the CONTROLLER calls (it is the HTTP client, we are the server).
/// Security PUSH sequence: GET cdata?options=all → POST registry → POST push → loop { getrequest / ping } +
/// POST cdata (rtlog, rtstate, tabledata) + POST devicecmd (command results) + POST querydata (query results).
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
[Route("iclock")]
public sealed class IClockController : ControllerBase
{
    private const string Plain = "text/plain;charset=UTF-8";
    private const string PushType = "application/push;charset=UTF-8";

    private readonly DeviceStore _devices;
    private readonly CommandQueue _queue;
    private readonly PushOptions _opt;

    public IClockController(DeviceStore devices, CommandQueue queue, IOptions<PushOptions> options)
    {
        _devices = devices;
        _queue = queue;
        _opt = options.Value;
    }

    // ───────────────────────────── handshake ─────────────────────────────

    /// <summary>
    /// Step 1: GET /iclock/cdata?SN=..&amp;options=all&amp;pushver=..
    /// Unregistered → "OK" (the device then registers). Known device (optional) → "registry=ok" + configuration.
    /// </summary>
    [HttpGet("cdata")]
    public IActionResult Init([FromQuery] string? SN, [FromQuery] string? options, [FromQuery] string? pushver)
    {
        HttpContext.SetKind(TrafficKind.Handshake);
        if (string.IsNullOrWhiteSpace(SN))
        {
            HttpContext.Note("Missing SN in query string.");
            return Text("OK");
        }

        var session = Touch(SN);
        session.PushVerFromInit = pushver;
        HttpContext.Note($"Device push protocol version (pushver) = {pushver ?? "(not sent → 3.0.1 or older)"}; server announces PushProtVer={_opt.PushProtVer}. Lower one wins → " +
                         (IsAtLeast(_opt.PushProtVer, "3.1.1") && IsAtLeast(pushver, "3.1.1")
                             ? "ENCRYPTION will be negotiated (/iclock/exchange) — not supported by this server!"
                             : "plain-text communication."));

        if (_opt.AnswerRegistryOkForKnownDevices && session.IsRegistered)
        {
            HttpContext.Note("Known device → answering 'registry=ok' with the full configuration (skips /registry).");
            return Text(BuildConfig(session, includeRegistry: true), PushType);
        }

        HttpContext.Note("Answering 'OK' = not registered → device should now POST /iclock/registry.");
        return Text("OK");
    }

    /// <summary>Step 1b (only when encryption was negotiated): key exchange. Not implemented on purpose.</summary>
    [HttpPost("exchange")]
    public IActionResult Exchange([FromQuery] string? SN, [FromQuery] string? type)
    {
        HttpContext.SetKind(TrafficKind.Handshake);
        if (!string.IsNullOrWhiteSpace(SN))
        {
            Touch(SN);
        }

        HttpContext.Note($"!!! ENCRYPTION KEY EXCHANGE requested (type={type}). The device thinks both sides speak PUSH ≥ 3.1.1. " +
                         $"This server does not implement ZKTeco's encryption library → answering HTTP {_opt.ExchangeResponseStatus}. " +
                         "Make sure Push:PushProtVer is 3.0.1 (see docs/PROTOCOL-COMPARISON.md).");
        return StatusCode(_opt.ExchangeResponseStatus);
    }

    /// <summary>Step 2: POST /iclock/registry — device describes itself, we answer RegistryCode=...</summary>
    [HttpPost("registry")]
    public async Task<IActionResult> Registry([FromQuery] string? SN)
    {
        HttpContext.SetKind(TrafficKind.Handshake);
        var body = await ReadBody();
        if (string.IsNullOrWhiteSpace(SN))
        {
            HttpContext.Note("Registry without SN → 406.");
            return StatusCode(406);
        }

        var session = Touch(SN);
        session.RegistryBody = body;
        session.Capabilities = DeviceCapabilities.Parse(body);
        session.CapabilitiesFromCache = false;
        session.RegisteredAt = DateTimeOffset.Now;
        _devices.SaveRegistry(session);
        if (string.IsNullOrEmpty(session.RegistryCode))
        {
            session.RegistryCode = DeviceStore.NewCode(16);
        }

        foreach (var line in CapabilityReport.Summarize(session.Capabilities, _opt))
        {
            HttpContext.Note(line);
        }

        return Text($"RegistryCode={session.RegistryCode}", "text/plain;charset=ISO-8859-1");
    }

    /// <summary>Step 3: POST /iclock/push — device downloads our configuration (RequestDelay, SessionID, ...).</summary>
    [HttpGet("push")]
    [HttpPost("push")]
    public IActionResult Push([FromQuery] string? SN)
    {
        HttpContext.SetKind(TrafficKind.Handshake);
        if (string.IsNullOrWhiteSpace(SN))
        {
            return Text("OK");
        }

        var session = Touch(SN);
        if (!session.IsRegistered)
        {
            HttpContext.Note("Device asked for configuration without registering first (server restarted?). Creating a session anyway.");
        }

        if (string.IsNullOrEmpty(session.RegistryCode))
        {
            session.RegistryCode = DeviceStore.NewCode(16);
        }

        session.ConfigDownloadedAt = DateTimeOffset.Now;
        CheckToken(session);
        var config = BuildConfig(session, includeRegistry: false);
        HttpContext.Note($"Handshake complete. Device will poll /iclock/getrequest every {_opt.RequestDelay}s.");
        return Text(config, PushType);
    }

    // ───────────────────────────── heartbeat & commands ─────────────────────────────

    [HttpGet("ping")]
    [HttpPost("ping")]
    public IActionResult Ping([FromQuery] string? SN)
    {
        HttpContext.SetKind(TrafficKind.Heartbeat);
        if (!string.IsNullOrWhiteSpace(SN))
        {
            Interlocked.Increment(ref Touch(SN).HeartbeatCount);
        }

        return Text("OK");
    }

    /// <summary>Heartbeat + command download: answer "OK" or one/more "C:ID:COMMAND" lines.</summary>
    [HttpGet("getrequest")]
    [HttpPost("getrequest")]
    public IActionResult GetRequest([FromQuery] string? SN)
    {
        HttpContext.SetKind(TrafficKind.Heartbeat);
        if (string.IsNullOrWhiteSpace(SN))
        {
            return Text("OK");
        }

        var session = Touch(SN);
        Interlocked.Increment(ref session.HeartbeatCount);
        CheckToken(session);


        var commands = _queue.TakeForDelivery(session.SerialNumber, _opt.MaxCommandsPerPoll, _opt.ResendUnansweredAfterSeconds);
        if (commands.Count == 0)
        {
            return Text("OK");
        }

        HttpContext.SetKind(TrafficKind.CommandDelivery);
        var useFormat1 = _opt.CmdFormat switch
        {
            "1" => true,
            "0" => false,
            _ => session.Capabilities?.CmdFormat == 1
        };

        var lines = new List<string>();
        foreach (var c in commands)
        {
            var wire = $"C:{c.Id}:{c.Text}";
            if (useFormat1)
            {
                wire = $"DataType=1,SN={session.SerialNumber},Priority=0,CmdID={c.Id},CmdDesc={wire}";
            }

            lines.Add(wire);
            HttpContext.Note($"→ sending #{c.Id} [{c.Label}]{(c.SendCount > 1 ? $" (attempt {c.SendCount})" : "")}{(useFormat1 ? " using CmdFormat=1 prefix" : "")}");
        }

        // No trailing CR/LF: the old server saw -619 when one was added.
        return Text(string.Join("\n", lines));
    }

    /// <summary>Command results: ID=..&amp;Return=..&amp;CMD=.. (one record per line).</summary>
    [HttpPost("devicecmd")]
    public async Task<IActionResult> DeviceCmd([FromQuery] string? SN)
    {
        HttpContext.SetKind(TrafficKind.CommandResult);
        var body = await ReadBody();
        if (!string.IsNullOrWhiteSpace(SN))
        {
            Touch(SN);
        }

        foreach (var record in SplitResultRecords(body))
        {
            var v = ParseAmpRecord(record);
            if (!v.TryGetValue("ID", out var idText) || !int.TryParse(idText, out var id))
            {
                HttpContext.Note($"Result without a valid ID: {record}");
                continue;
            }

            v.TryGetValue("Return", out var retText);
            if (!int.TryParse(retText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ret))
            {
                HttpContext.Note($"Result #{id} without a numeric Return: {record}");
                continue;
            }

            var cmd = _queue.Complete(id, ret, record);
            v.TryGetValue("CMD", out var cmdType);
            var label = cmd == null ? "(unknown ID — server restarted?)" : cmd.Label;
            var verdict = ret >= 0 ? "OK" : ret == -5000 ? "PENDING" : "FAILED";
            HttpContext.Note($"← result #{id} [{label}] CMD={cmdType} Return={ret} → {verdict}: {ReturnCodes.Describe(ret)}");
            if (cmd != null && ret < 0 && ret != -5000)
            {
                HttpContext.Note($"   failed command was: {cmd.WireVisible}");
            }
        }

        return Text("OK");
    }

    /// <summary>Data uploaded as the answer to DATA QUERY / DATA COUNT / GET OPTIONS.</summary>
    [HttpPost("querydata")]
    public async Task<IActionResult> QueryData([FromQuery] string? SN, [FromQuery] string? type, [FromQuery] int cmdid,
        [FromQuery] string? tablename, [FromQuery] int count = 0, [FromQuery] int packcnt = 1, [FromQuery] int packidx = 1)
    {
        HttpContext.SetKind(TrafficKind.CommandResult);
        var body = await ReadBody();
        if (!string.IsNullOrWhiteSpace(SN))
        {
            Touch(SN);
        }

        var cmd = _queue.Get(cmdid);
        cmd?.QueryData.AddRange(RealtimeParser.Records(body));
        HttpContext.Note($"Query data for #{cmdid} [{cmd?.Label ?? "?"}] type={type} table={tablename} count={count} package {packidx}/{packcnt}");

        if (string.IsNullOrWhiteSpace(tablename))
        {
            return Text("OK");
        }

        if (string.Equals(type, "count", StringComparison.OrdinalIgnoreCase))
        {
            // Body is e.g. "user=3" — echo the table count back.
            var first = RealtimeParser.Records(body).FirstOrDefault() ?? "";
            var idx = first.IndexOf('=');
            var value = idx >= 0 && int.TryParse(first[(idx + 1)..].Trim(), out var n) ? n : 0;
            HttpContext.Note($"COUNT {tablename} = {value}");
            return Text($"{tablename}={value}");
        }

        return Text($"{tablename}={Math.Max(count, 0)}");
    }

    // ───────────────────────────── uploads from the device ─────────────────────────────

    /// <summary>Uploads: rtlog (events), rtstate (door states), tabledata (user/extuser/… tables), options,
    /// and (2026 doc, chapter 11) ServerReq=transparent JSON requests.</summary>
    [HttpPost("cdata")]
    public async Task<IActionResult> Upload([FromQuery] string? SN, [FromQuery] string? table, [FromQuery] string? tablename,
        [FromQuery] int? count, [FromQuery] string? type, [FromQuery] string? ServerReq)
    {
        var body = await ReadBody();
        HttpContext.SetKind(TrafficKind.Other);
        var session = string.IsNullOrWhiteSpace(SN) ? null : Touch(SN);
        table ??= "";

        if (string.Equals(ServerReq, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Note("Universal transparent transmission request (JSON, 2026 doc chapter 11: SIP status / background face comparison / elevator). " +
                             "This server never announces support for these features, so the device should not send them.");
            return Text("OK");
        }

        switch (table.ToLowerInvariant())
        {
            case "rtlog":
                HttpContext.SetKind(TrafficKind.RtLog);
                if (session != null) Interlocked.Increment(ref session.RtLogCount);
                foreach (var line in RealtimeParser.DescribeRtLog(body))
                {
                    HttpContext.Note(line);
                }

                return Text("OK");

            case "rtstate":
                HttpContext.SetKind(TrafficKind.RtState);
                if (session != null)
                {
                    Interlocked.Increment(ref session.RtStateCount);
                    var withoutTime = string.Join("\t", body.Split('\t').Where(p => !p.StartsWith("time=", StringComparison.OrdinalIgnoreCase))).Trim();
                    HttpContext.Items[ZkItems.RtStateChanged] = withoutTime != session.LastRtState;
                    session.LastRtState = withoutTime;
                }

                HttpContext.Note(RealtimeParser.DescribeRtState(body));
                return Text("OK");

            case "tabledata":
            {
                var records = RealtimeParser.Records(body).ToList();
                var n = count ?? records.Count;
                var name = tablename ?? "tabledata";
                HttpContext.Note($"Device uploaded {n} row(s) of table '{name}'{(type != null ? $" (type={type})" : "")}. Answering '{name}={n}' as the protocol requires (the old server answered 'OK').");
                foreach (var r in records.Take(20))
                {
                    HttpContext.Note("   " + r.Replace("\t", "  "));
                }

                return Text($"{name}={n}");
            }

            case "options":
                if (session != null) session.LastInfo = body;
                HttpContext.Note("Device pushed its current parameters (table=options).");
                return Text("OK");

            default:
                HttpContext.Note($"Upload for table '{table}' (not specially handled).");
                return Text(string.IsNullOrEmpty(tablename) ? "OK" : $"{tablename}={count ?? RealtimeParser.Records(body).Count()}");
        }
    }

    /// <summary>Time request: GET /iclock/rtdata?SN=..&amp;type=time → DateTime=&lt;Appendix 5 seconds&gt;,ServerTZ=+0200.</summary>
    [HttpGet("rtdata")]
    [HttpPost("rtdata")]
    public IActionResult RtData([FromQuery] string? SN, [FromQuery] string? type)
    {
        HttpContext.SetKind(TrafficKind.Handshake);
        if (!string.IsNullOrWhiteSpace(SN))
        {
            Touch(SN);
        }

        if (!string.Equals(type, "time", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Note($"rtdata type={type} not handled.");
            return Text("OK");
        }

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _opt.ResolveTimeZone());
        var useLocal = string.Equals(_opt.RtDataTimeMode, "local", StringComparison.OrdinalIgnoreCase);
        var basis = useLocal ? local.DateTime : DateTime.UtcNow;
        var encoded = ZkTime.Encode(basis);
        HttpContext.Note($"Time sync: sending {(useLocal ? "local" : "GMT")} time {basis:yyyy-MM-dd HH:mm:ss} as {encoded}, ServerTZ={ZkTime.FormatOffset(local.Offset)} (old server sent a formatted date string here).");
        return Text($"DateTime={encoded},ServerTZ={ZkTime.FormatOffset(local.Offset)}");
    }

    /// <summary>Anything else under /iclock is logged so unknown firmware behaviour is visible.</summary>
    [Route("{**rest}")]
    public async Task<IActionResult> CatchAll(string rest)
    {
        HttpContext.SetKind(TrafficKind.Unknown);
        await ReadBody();
        var sn = Request.Query["SN"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sn))
        {
            Touch(sn);
        }

        HttpContext.Note($"UNHANDLED endpoint /iclock/{rest} — answered 'OK'. Please send this log line to the developer.");
        return Text("OK");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private string BuildConfig(DeviceSession session, bool includeRegistry)
    {
        if (string.IsNullOrEmpty(session.SessionId))
        {
            session.SessionId = DeviceStore.NewCode(32);
        }

        var lines = new List<string>();
        if (includeRegistry)
        {
            lines.Add("registry=ok");
            lines.Add($"RegistryCode={session.RegistryCode}");
        }

        lines.Add($"ServerVersion={_opt.ServerVersion}");
        lines.Add($"ServerName={_opt.ServerName}");
        lines.Add($"PushProtVer={_opt.PushProtVer}");
        if (!string.IsNullOrWhiteSpace(_opt.LegacyPushVersionLine))
        {
            lines.Add($"PushVersion={_opt.LegacyPushVersionLine}");
        }

        lines.Add($"ErrorDelay={_opt.ErrorDelay}");
        lines.Add($"RequestDelay={_opt.RequestDelay}");
        lines.Add($"TransTimes={_opt.TransTimes}");
        lines.Add($"TransInterval={_opt.TransInterval}");
        lines.Add($"TransTables={_opt.TransTables}");
        lines.Add($"Realtime={_opt.Realtime}");
        lines.Add($"SessionID={session.SessionId}");
        lines.Add($"TimeoutSec={_opt.TimeoutSec}");

        var ns = session.Capabilities?.DevNSLevel;
        if (_opt.EchoNsLevel && ns.HasValue)
        {
            lines.Add($"SvrNSLevel={ns.Value}");
            HttpContext.Note($"Device reported DevNSLevel={ns} → replying SvrNSLevel={ns} (they must match or the device refuses the server).");
        }

        // Reply the device's new verification styles so it keeps using them (4.5: NewVFStyles).
        var vf = session.Capabilities?.Get("NewVFStyles");
        if (!string.IsNullOrEmpty(vf))
        {
            lines.Add($"NewVFStyles={vf}");
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    private void CheckToken(DeviceSession session)
    {
        var cookie = Request.Headers.Cookie.ToString();
        if (string.IsNullOrEmpty(cookie))
        {
            return;
        }

        // Seen on C3-400 Plus (AC Ver 19.0.18, push 2.0.34.ACCS-20250410):
        //   Cookie: token=<md5>,timestamp=2026-10-20-15-52-24
        //   token = MD5(RegistryCode + SN + timestamp)  — the doc says RegistryCode + SN + SessionID.
        var token = System.Text.RegularExpressions.Regex.Match(cookie, @"token=([0-9a-fA-F]{32})").Groups[1].Value;
        var stamp = System.Text.RegularExpressions.Regex.Match(cookie, @"timestamp=([^,;\s]+)").Groups[1].Value;
        if (token.Length == 0)
        {
            return;
        }

        static string Md5(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        var withStamp = stamp.Length > 0 ? Md5(session.RegistryCode + session.SerialNumber + stamp) : "";
        if (token.Equals(withStamp, StringComparison.OrdinalIgnoreCase) ||
            token.Equals(session.ExpectedToken, StringComparison.OrdinalIgnoreCase))
        {
            if (stamp.Length > 0)
            {
                CheckDeviceClock(stamp);
            }

            return;
        }

        if (session.TokenNoteShown)
        {
            return;
        }

        session.TokenNoteShown = true;
        HttpContext.Note($"Cookie token {token} matches neither MD5(RegistryCode+SN+timestamp) nor MD5(RegistryCode+SN+SessionID). " +
                         "Not enforced — informational only, shown once (server restarted with a new RegistryCode?).");
    }

    /// <summary>The cookie timestamp is the controller's own clock: warn when it is far from server time.</summary>
    private void CheckDeviceClock(string stamp)
    {
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var deviceTime))
        {
            return;
        }

        var serverLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _opt.ResolveTimeZone()).DateTime;
        var diff = deviceTime - serverLocal;
        if (Math.Abs(diff.TotalMinutes) >= 2)
        {
            HttpContext.Note($"CONTROLLER CLOCK is {diff.TotalHours:+0.0;-0.0} h off ({deviceTime:yyyy-MM-dd HH:mm:ss} vs server {serverLocal:yyyy-MM-dd HH:mm:ss} {_opt.TimeZone}). " +
                             "Time rules use the controller clock → run POST /api/device/sync-time and check the time zone in the web UI.");
        }
    }

    private DeviceSession Touch(string sn) =>
        _devices.Touch(sn, HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString());

    private async Task<string> ReadBody()
    {
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;
        return body;
    }

    private ContentResult Text(string text, string contentType = Plain)
    {
        if (_opt.ConnectionClose)
        {
            Response.Headers.Connection = "close";
        }

        return new ContentResult { Content = text, ContentType = contentType, StatusCode = 200 };
    }

    private static IEnumerable<string> SplitResultRecords(string body)
    {
        var lines = RealtimeParser.Records(body).ToList();
        if (lines.Any(l => l.Contains('&')))
        {
            return lines;
        }

        // Some results come as one key per line → merge into one record.
        return lines.Count == 0 ? lines : new[] { string.Join("&", lines) };
    }

    private static Dictionary<string, string> ParseAmpRecord(string record)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
            {
                dict[part[..idx].Trim()] = part[(idx + 1)..].Trim();
            }
        }

        return dict;
    }

    internal static bool IsAtLeast(string? version, string minimum)
    {
        static Version? P(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var digits = new string(v.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
            return Version.TryParse(digits, out var parsed) ? parsed : null;
        }

        var a = P(version);
        var b = P(minimum);
        return a != null && b != null && a >= b;
    }
}
