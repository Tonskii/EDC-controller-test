using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>Door / relay control, reboot, time sync and device parameters.</summary>
[Route("api/device")]
[Tags("6 · Doors, device control & options")]
public sealed class DeviceControlController : ApiBase
{
    public DeviceControlController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Open a door for N seconds (0 = all doors).</summary>
    /// <remarks>
    /// CONTROL DEVICE AABBCCDD, every byte written as %02X (hex): AA=01 output, BB=door, CC=01 lock, DD=seconds.
    ///
    ///     door 1, 5 s:   C:122:CONTROL DEVICE 01010105
    ///     door 3, 15 s:  C:122:CONTROL DEVICE 0103010F   (the old code wrote "15" = 21 seconds)
    /// </remarks>
    [HttpPost("doors/{door:int}/open")]
    public IActionResult Open(int door, [FromQuery] int seconds = 5, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        if (seconds is < 1 or > 254) return BadRequest(new { error = "seconds must be 1..254 (255 = normal open, use /normal-open)." });
        return Control(sn, preview, $"Open door {door} for {seconds}s", 1, door, 1, seconds);
    }

    /// <summary>Open a door and attach a PIN to the resulting event (2026 doc 9.4.2, needs RemoteOpenDoorExFunOn=1).</summary>
    /// <remarks>    C:123:REMOTE OPENDOOREX id=1[TAB]opentime=5[TAB]pin=1001</remarks>
    [HttpPost("doors/{door:int}/open-ex")]
    public IActionResult OpenEx(int door, [FromQuery] int seconds = 5, [FromQuery] string? pin = null, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, caps, error) = Target(sn, preview);
        if (error != null) return error;
        var notes = new List<string>();
        if (caps.RemoteOpenDoorExFunOn != 1)
        {
            notes.Add("Device did not report RemoteOpenDoorExFunOn=1 — expect -13/-708 if unsupported; use /open instead.");
        }

        return Done(s, preview, new[] { ($"Open door {door} (ex)", $"REMOTE OPENDOOREX id={door}\topentime={seconds}\tpin={pin ?? ""}") }, notes);
    }

    /// <summary>Close (re-lock) a door immediately.</summary>
    /// <remarks>    C:124:CONTROL DEVICE 01010100</remarks>
    [HttpPost("doors/{door:int}/close")]
    public IActionResult Close(int door, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, $"Close door {door}", 1, door, 1, 0);

    /// <summary>Keep a door permanently open until closed again.</summary>
    /// <remarks>    C:125:CONTROL DEVICE 010101FF</remarks>
    [HttpPost("doors/{door:int}/normal-open")]
    public IActionResult NormalOpen(int door, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, $"Normal open door {door}", 1, door, 1, 0xFF);

    /// <summary>Enable/disable today's scheduled normal-open period of a door.</summary>
    /// <remarks>    C:126:CONTROL DEVICE 04010100   (AA=04, BB=door, CC=01 enable / 00 disable)</remarks>
    [HttpPost("doors/{door:int}/normal-open-schedule")]
    public IActionResult NormalOpenSchedule(int door, [FromQuery] bool enable, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, $"{(enable ? "Enable" : "Disable")} normal-open period of door {door}", 4, door, enable ? 1 : 0, 0);

    /// <summary>Lock or unlock a door (AA=06, only if AccSupportFunList[2]=1).</summary>
    /// <remarks>    C:127:CONTROL DEVICE 06010100  (CC=01 lock, 00 unlock)</remarks>
    [HttpPost("doors/{door:int}/lock")]
    public IActionResult Lock(int door, [FromQuery] bool locked = true, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, $"{(locked ? "Lock" : "Unlock")} door {door}", 6, door, locked ? 1 : 0, 0);

    /// <summary>Switch an auxiliary output on for N seconds (255 = stay on, 0 = off).</summary>
    /// <remarks>    C:128:CONTROL DEVICE 01010205</remarks>
    [HttpPost("aux/{output:int}")]
    public IActionResult Aux(int output, [FromQuery] int seconds = 5, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, $"Aux output {output} {seconds}s", 1, output, 2, Math.Clamp(seconds, 0, 255));

    /// <summary>Cancel alarms (0 = all doors).</summary>
    /// <remarks>    C:129:CONTROL DEVICE 02000000</remarks>
    [HttpPost("alarm/cancel")]
    public IActionResult CancelAlarm([FromQuery] int door = 0, [FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, "Cancel alarm", 2, door, 0, 0);

    /// <summary>Reboot the controller.</summary>
    /// <remarks>    C:130:CONTROL DEVICE 03000000</remarks>
    [HttpPost("reboot")]
    public IActionResult Reboot([FromQuery] string? sn = null, [FromQuery] bool preview = false) =>
        Control(sn, preview, "Reboot", 3, 0, 0, 0);

    /// <summary>Set the controller clock to the server's local time.</summary>
    /// <remarks>
    /// The protocol wants seconds (Appendix 5) and the plural SET OPTIONS:
    ///
    ///     C:131:SET OPTIONS DateTime=832896000
    ///
    /// The old code sent "SET OPTION DateTime=2026-10-19 17:33:00" (string, singular).
    /// </remarks>
    [HttpPost("sync-time")]
    public IActionResult SyncTime([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Options.ResolveTimeZone());
        var encoded = ZkTime.Encode(now.DateTime);
        return Done(s, preview,
            new[] { ($"Sync time {now:yyyy-MM-dd HH:mm:ss}", SecurityCommands.SetOptions(new[] { new KeyValuePair<string, string>("DateTime", encoded.ToString()) })) },
            new[] { $"{now:yyyy-MM-dd HH:mm:ss} ({Options.TimeZone}) → {encoded}" });
    }

    /// <summary>Read device parameters. Values arrive on /iclock/querydata and are shown in GET /api/commands/{id}.</summary>
    /// <remarks>    C:132:GET OPTIONS IPAddress,NetMask,GATEIPAddress,DateTime</remarks>
    [HttpPost("options/get")]
    public IActionResult GetOptions([FromBody] OptionsGetRequest r, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.Names.Count == 0) return BadRequest(new { error = "Give at least one parameter name." });
        return Done(s, preview, new[] { ("Get options", SecurityCommands.GetOptions(r.Names)) });
    }

    /// <summary>Change device parameters (door lock time, sensor type, server address, ...).</summary>
    /// <remarks>
    ///     C:133:SET OPTIONS Door1Drivertime=5,Door1SensorType=1
    /// Common names: DoorNDrivertime (lock seconds), DoorNSensorType (0 none,1 NO,2 NC), DoorNDetectortime (open alarm s),
    /// DoorNVerifyType, DoorNValidTZ, IPAddress/NetMask/GATEIPAddress, WebServerIP/WebServerPort, ComPwd.
    /// </remarks>
    [HttpPost("options/set")]
    public IActionResult SetOptions([FromBody] OptionsSetRequest r, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.Options.Count == 0) return BadRequest(new { error = "Give at least one option." });
        if (r.Options.Values.Any(v => v.Contains(','))) return BadRequest(new { error = "Values must not contain commas (comma separates parameters)." });
        return Done(s, preview, new[] { ("Set options", SecurityCommands.SetOptions(r.Options)) });
    }

    private IActionResult Control(string? sn, bool preview, string label, int aa, int bb, int cc, int dd)
    {
        if (bb is < 0 or > 16) return BadRequest(new { error = "Door / output number must be 0..16." });
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { (label, SecurityCommands.Control(aa, bb, cc, dd)) });
    }
}

/// <summary>Access events stored on the controller (table "transaction") and free-form commands.</summary>
[Route("api")]
[Tags("7 · Records, queries & raw commands")]
public sealed class RecordsController : ApiBase
{
    public RecordsController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Download stored access records (newOnly = only records not uploaded before).</summary>
    /// <remarks>
    ///     C:134:DATA QUERY tablename=transaction,fielddesc=*,filter=NewRecord
    ///     C:134:DATA QUERY tablename=transaction,fielddesc=*,filter=*
    /// </remarks>
    [HttpPost("transactions/query")]
    public IActionResult QueryTransactions([FromQuery] bool newOnly = true, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query transactions", SecurityCommands.Query("transaction", "*", newOnly ? "NewRecord" : "*")) });
    }

    /// <summary>Count rows of any table (user, mulcarduser, userauthorize, timezone, holiday, transaction, templatev10, ...).</summary>
    /// <remarks>    C:135:DATA COUNT transaction</remarks>
    [HttpPost("tables/{table}/count")]
    public IActionResult CountTable(string table, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ($"Count {table}", SecurityCommands.Count(table)) });
    }

    /// <summary>Query any table.</summary>
    /// <remarks>    C:136:DATA QUERY tablename=holiday,fielddesc=*,filter=*</remarks>
    [HttpPost("tables/{table}/query")]
    public IActionResult QueryTable(string table, [FromQuery] string fieldDesc = "*", [FromQuery] string filter = "*",
        [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ($"Query {table}", SecurityCommands.Query(table, fieldDesc, filter)) });
    }

    /// <summary>Delete all stored access records. Requires confirm=true.</summary>
    /// <remarks>    C:137:DATA DELETE transaction *</remarks>
    [HttpDelete("transactions")]
    public IActionResult DeleteTransactions([FromQuery] bool confirm, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        if (!confirm && !preview) return BadRequest(new { error = "Add confirm=true — this erases the controller's event log." });
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Delete all transactions", "DATA DELETE transaction *") });
    }

    /// <summary>Send any command text. Use [TAB] and [LF] placeholders.</summary>
    /// <remarks>
    /// Examples:
    ///
    ///     DATA UPDATE user Pin=1002[TAB]Password=[TAB]Group=0[TAB]StartTime=0[TAB]EndTime=0[TAB]Name=Test
    ///     DATA UPDATE extuser Pin=1002[TAB]FunSwitch=0[TAB]FirstName=[TAB]LastName=[TAB]PersonalVS=0
    ///     DATA DELETE mulcarduser *
    ///     GET OPTIONS ~SerialNumber,FirmVer,LockCount
    /// </remarks>
    [HttpPost("raw")]
    public IActionResult Raw([FromBody] RawCommandRequest? r, [FromQuery] bool preview = false)
    {
        if (r == null)
        {
            return BadRequest(new { error = "Body missing or not valid JSON. Do not paste real tab characters into the JSON — write [TAB]." });
        }

        var (s, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (string.IsNullOrWhiteSpace(r.Command)) return BadRequest(new { error = "Command is required." });
        var text = System.Text.RegularExpressions.Regex.Replace(r.Command, @"\[tab\]", "\t", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[lf\]", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim('\r', '\n');
        var notes = FieldNameWarnings(text).ToList();
        if (text.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Leave out the 'C:ID:' prefix — the server adds it." });
        }

        return Done(s, preview, new[] { (r.Label ?? "Raw command", text) }, notes);
    }

    private static readonly Dictionary<string, string[]> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = new[] { "CardNo", "Pin", "Password", "Group", "StartTime", "EndTime", "Name", "Privilege", "Disable" },
        ["mulcarduser"] = new[] { "Pin", "CardNo", "LossCardFlag", "CardType", "CardBit", "SiteCode", "IMIE" },
        ["userauthorize"] = new[] { "Pin", "AuthorizeTimezoneId", "AuthorizeDoorId", "DevID", "StartTime", "EndTime" },
        ["timezone"] = SecurityCommands.TimezoneSlots.Prepend("TimezoneId").Concat(new[] { "StartTime", "EndTime" }).ToArray(),
        ["holiday"] = new[] { "Holiday", "HolidayType", "Loop" },
    };

    /// <summary>The firmware silently ignores misspelled field names (e.g. "Cardno" → stored empty) — point them out.</summary>
    private static IEnumerable<string> FieldNameWarnings(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^DATA (UPDATE|DELETE) (\S+) (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success || !KnownFields.TryGetValue(m.Groups[2].Value, out var known)) yield break;

        foreach (var record in m.Groups[3].Value.Split('\n'))
        {
            foreach (var part in record.Split('\t'))
            {
                var i = part.IndexOf('=');
                if (i <= 0) continue;
                var key = part[..i];
                if (key.Contains(' '))
                {
                    yield return $"WARNING: '{part}' contains a space — fields must be separated by [TAB], otherwise everything ends up in one field.";
                    continue;
                }

                var exact = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (exact == null)
                {
                    yield return $"WARNING: field '{key}' is not a documented {m.Groups[2].Value} field.";
                }
                else if (exact != key)
                {
                    yield return $"WARNING: field '{key}' should be written '{exact}' — the controller is case-sensitive and ignores '{key}'.";
                }
            }
        }
    }
}
