using System.Globalization;
using System.Numerics;
using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

public sealed class ElevatorAccessRequest
{
    /// <example>WSQ2260800005</example>
    public string? Sn { get; set; }

    /// <example>1001</example>
    public string Pin { get; set; } = "";

    /// <summary>Floors the user may select (1-based). Ignored when allFloors is true.</summary>
    /// <example>[1,2,3]</example>
    public List<int>? Floors { get; set; }

    /// <summary>All floors 1..floorCount (ZKBio CVSecurity sends AuthorizeFloors=FFFF for 16 floors).</summary>
    /// <example>false</example>
    public bool AllFloors { get; set; }

    /// <summary>Number of floors of the controller, used with allFloors. Default 16.</summary>
    /// <example>16</example>
    public int FloorCount { get; set; } = 16;

    /// <summary>Time rule ("timezone") ID — same table as for doors (/api/timerules).</summary>
    /// <example>1</example>
    public int TimezoneId { get; set; } = 1;

    /// <summary>AuthorizeFloorId = "direct floor" (released automatically on swipe). 0 = none (CVSecurity sends 0).</summary>
    /// <example>0</example>
    public int DirectFloor { get; set; }

    /// <summary>DevID override. Empty = DevProperty ID read from the controller (WSQ2260800005 → 1).</summary>
    public int? DevId { get; set; }
}

public sealed class ElevatorAccessBatchRequest
{
    /// <example>WSQ2260800005</example>
    public string? Sn { get; set; }

    public List<ElevatorAccessRequest> Items { get; set; } = new();
}

public sealed class FloorReleaseRequest
{
    /// <example>WSQ2260800005</example>
    public string? Sn { get; set; }

    /// <summary>Floors to release (1-based).</summary>
    /// <example>[1]</example>
    public List<int> Floors { get; set; } = new();

    /// <summary>Seconds the floor buttons stay enabled (1-254). CVSecurity sends 5.</summary>
    /// <example>5</example>
    public int Seconds { get; set; } = 5;

    /// <summary>
    /// Bit order of the 32-byte floor field. "cvsecurity" = floor 1 is the RIGHT-most bit (what ZKBio CVSecurity sent:
    /// …0001). "document" = floor 1 is the LEFT-most bit (protocol document example 8000…). Verify on the device.
    /// </summary>
    /// <example>cvsecurity</example>
    public string BitOrder { get; set; } = "cvsecurity";
}

public sealed class ElevatorSetupRequest
{
    /// <example>WSQ2260800005</example>
    public string? Sn { get; set; }

    /// <summary>Number of floor relays (the controller in the log has 16).</summary>
    /// <example>16</example>
    public int Floors { get; set; } = 16;

    /// <summary>How long a floor relay stays on after a valid swipe (DoorDrivertime). CVSecurity: 5.</summary>
    /// <example>5</example>
    public int RelaySeconds { get; set; } = 5;

    /// <summary>Time rule in which the floor relays are active at all (DoorValidTZ). CVSecurity: 1.</summary>
    /// <example>1</example>
    public int ValidTimezoneId { get; set; } = 1;

    /// <summary>Time rule in which floors are permanently free (DoorKeepOpenTimeZone). 0 = never.</summary>
    /// <example>0</example>
    public int KeepOpenTimezoneId { get; set; }

    /// <summary>Number of underground floors (SET OPTIONS EleNegativeFloorCount). CVSecurity: 0.</summary>
    /// <example>0</example>
    public int NegativeFloors { get; set; }

    /// <summary>Delete DoorProperty / DoorParameters first, like CVSecurity's "synchronize all data".</summary>
    /// <example>true</example>
    public bool ReplaceExisting { get; set; } = true;

    public int? DevId { get; set; }
}

/// <summary>
/// Elevator controller (ZKTeco elevator panel, MachineType 420, e.g. SN WSQ2260800005). Users, cards, extuser and
/// time rules use the SAME endpoints as the doors (/api/users, /api/cards, /api/timerules). Only floor access
/// (table usereleauthorize instead of userauthorize), floor release and the floor-relay setup are different.
/// </summary>
[Route("api/elevator")]
[Tags("8 · Elevator (floors)")]
public sealed class ElevatorController : ApiBase
{
    public ElevatorController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Give a user access to floors during a time rule.</summary>
    /// <remarks>
    /// What ZKBio CVSecurity sends for "all 16 floors, rule 1":
    ///
    ///     C:87:DATA UPDATE usereleauthorize Pin=1[TAB]AuthorizeTimezoneId=1[TAB]AuthorizeFloorId=0[TAB]AuthorizeFloors=FFFF[TAB]DevID=1[TAB]StartTime=0[TAB]EndTime=0
    ///
    /// AuthorizeFloors = hex bit mask, floor 1 = lowest bit (floors 8+9 → 180, floors 1-16 → FFFF).
    /// ONE row per user (unlike doors on the C3-400 Plus, which need one row per door).
    /// </remarks>
    [HttpPut("access")]
    public IActionResult SetAccess([FromBody] ElevatorAccessRequest r, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(r.Sn, preview);
        if (error != null) return error;
        var notes = new List<string>();
        return Done(sn, preview, new[] { ($"Floors for {r.Pin}", SecurityCommands.Update("usereleauthorize", new[] { Record(sn, r, caps, notes) })) }, notes);
    }

    /// <summary>Floor access for many users in one command.</summary>
    [HttpPut("access/batch")]
    public IActionResult SetAccessBatch([FromBody] ElevatorAccessBatchRequest r, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.Items.Count == 0) return BadRequest(new { error = "At least one item is required." });
        var notes = new List<string>();
        var records = r.Items.Select(i => Record(sn, i, caps, notes)).ToList();
        return Done(sn, preview, new[] { ($"Floors for {records.Count} users", SecurityCommands.Update("usereleauthorize", records)) }, notes.Distinct());
    }

    /// <summary>Remove all floor access of a user (all = true removes everybody's, like CVSecurity's full sync).</summary>
    /// <remarks>
    ///     C:88:DATA DELETE usereleauthorize Pin=1001
    ///     C:88:DATA DELETE usereleauthorize *
    /// </remarks>
    [HttpDelete("access/{pin}")]
    public IActionResult DeleteAccess(string pin, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        var cond = pin == "*" ? "*" : $"Pin={pin}";
        return Done(s, preview, new[] { ($"Delete floor access of {pin}", $"DATA DELETE usereleauthorize {cond}") });
    }

    /// <summary>Read the floor-access table back (rows in GET /api/commands/{id}).</summary>
    /// <remarks>    DATA QUERY tablename=usereleauthorize,fielddesc=*,filter=*</remarks>
    [HttpPost("access/query")]
    public IActionResult QueryAccess([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query floor access", SecurityCommands.Query("usereleauthorize")) });
    }

    /// <summary>Remotely release floors (enable the floor buttons for N seconds) — CONTROL DEVICE AA=0x11.</summary>
    /// <remarks>
    /// What ZKBio CVSecurity sent (returned 1 = success):
    ///
    ///     CONTROL DEVICE 11 0000…0001 01 05      (written without spaces: 11 + 64 hex floor digits + 01 + 05)
    ///
    /// AA=11 (17 = elevator output), 32 bytes floor bits, 01, seconds (hex). Which end is floor 1 is not certain:
    /// bitOrder=cvsecurity puts floor 1 at the right (as in the log), bitOrder=document at the left (as in the PDF).
    /// </remarks>
    [HttpPost("floors/release")]
    public IActionResult Release([FromBody] FloorReleaseRequest r, [FromQuery] bool preview = false)
    {
        var (sn, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.Floors.Count == 0) return BadRequest(new { error = "Give at least one floor." });
        if (r.Seconds is < 1 or > 254) return BadRequest(new { error = "seconds must be 1..254." });
        var bits = FloorBits256(r.Floors, r.BitOrder);
        return Done(sn, preview,
            new[] { ($"Release floors {string.Join(",", r.Floors)} for {r.Seconds}s", $"CONTROL DEVICE 11{bits}01{r.Seconds:X2}") },
            new[] { $"Floor field ({r.BitOrder}): {bits}" });
    }

    /// <summary>Set up the floor relays like CVSecurity does when a controller is added (DoorProperty, DoorParameters, negative floors).</summary>
    /// <remarks>
    ///     DATA DELETE DoorProperty *
    ///     DATA UPDATE DoorProperty ID=1[TAB]DevID=1[TAB]Address=1[TAB]Disable=0[LF]… ID=16 …
    ///     DATA DELETE DoorParameters *
    ///     DATA UPDATE DoorParameters ID=1[TAB]Name=DoorDrivertime[TAB]Value=5[TAB]DevID=1[LF]ID=1[TAB]Name=DoorKeepOpenTimeZone[TAB]Value=0[TAB]DevID=1[LF]ID=1[TAB]Name=DoorValidTZ[TAB]Value=1[TAB]DevID=1
    ///     … one DoorParameters command per floor …
    ///     SET OPTIONS EleNegativeFloorCount=0
    /// Only needed on a new / reset controller — a controller already set up by CVSecurity keeps these.
    /// </remarks>
    [HttpPost("setup")]
    public IActionResult Setup([FromBody] ElevatorSetupRequest r, [FromQuery] bool preview = false)
    {
        var (sn, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.Floors is < 1 or > 256) return BadRequest(new { error = "floors must be 1..256." });
        var devId = RequireDevId(sn, r.DevId, out var source);
        var notes = new List<string> { $"DevID={devId} (from {source})." };

        var commands = new List<(string, string)>();
        if (r.ReplaceExisting) commands.Add(("Delete DoorProperty", "DATA DELETE DoorProperty *"));
        commands.Add(($"DoorProperty for {r.Floors} floor relays", SecurityCommands.Update("DoorProperty",
            Enumerable.Range(1, r.Floors).Select(f => SecurityCommands.Fields(("ID", f), ("DevID", devId), ("Address", f), ("Disable", 0))))));
        if (r.ReplaceExisting) commands.Add(("Delete DoorParameters", "DATA DELETE DoorParameters *"));
        foreach (var f in Enumerable.Range(1, r.Floors))
        {
            commands.Add(($"DoorParameters floor {f}", SecurityCommands.Update("DoorParameters", new[]
            {
                SecurityCommands.Fields(("ID", f), ("Name", "DoorDrivertime"), ("Value", r.RelaySeconds), ("DevID", devId)),
                SecurityCommands.Fields(("ID", f), ("Name", "DoorKeepOpenTimeZone"), ("Value", r.KeepOpenTimezoneId), ("DevID", devId)),
                SecurityCommands.Fields(("ID", f), ("Name", "DoorValidTZ"), ("Value", r.ValidTimezoneId), ("DevID", devId)),
            })));
        }

        commands.Add(("Negative floors", SecurityCommands.SetOptions(new[] { new KeyValuePair<string, string>("EleNegativeFloorCount", r.NegativeFloors.ToString(CultureInfo.InvariantCulture)) })));
        return Done(sn, preview, commands, notes);
    }

    private string Record(string sn, ElevatorAccessRequest r, DeviceCapabilities caps, List<string> notes)
    {
        ValidatePin(r.Pin, caps);
        if (r.TimezoneId < 1) throw new ArgumentException("TimezoneId must be 1 or higher.");
        var floors = r.AllFloors ? Enumerable.Range(1, r.FloorCount).ToList() : (r.Floors ?? new List<int>());
        if (floors.Count == 0) throw new ArgumentException("Give floors, or allFloors=true.");
        if (floors.Any(f => f is < 1 or > 256)) throw new ArgumentException("Floors must be 1..256.");

        var mask = floors.Aggregate(BigInteger.Zero, (m, f) => m | (BigInteger.One << (f - 1)));
        var hex = mask.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
        var devId = RequireDevId(sn, r.DevId, out var source);
        notes.Add($"Floors {string.Join(",", floors.OrderBy(f => f))} → AuthorizeFloors={hex}; DevID={devId} (from {source}).");

        return SecurityCommands.Fields(("Pin", r.Pin), ("AuthorizeTimezoneId", r.TimezoneId), ("AuthorizeFloorId", r.DirectFloor),
            ("AuthorizeFloors", hex), ("DevID", devId), ("StartTime", 0), ("EndTime", 0));
    }

    private int RequireDevId(string sn, int? requested, out string source)
    {
        var (value, src) = ResolveDevId(sn, requested);
        source = src;
        return value ?? throw new ArgumentException(
            $"DevID unknown for {sn}. Wait until the server has read DevProperty after the handshake (GET /api/devices), " +
            "give \"devId\" in the request, or PUT /api/devices/{sn}/devid?value=1.");
    }

    /// <summary>32 bytes (64 hex digits) with one bit per floor.</summary>
    internal static string FloorBits256(IEnumerable<int> floors, string bitOrder)
    {
        var list = floors.ToList();
        if (list.Any(f => f is < 1 or > 256)) throw new ArgumentException("Floors must be 1..256.");
        var bits = new char[256];
        Array.Fill(bits, '0');
        var document = string.Equals(bitOrder, "document", StringComparison.OrdinalIgnoreCase);
        foreach (var f in list)
        {
            bits[document ? f - 1 : 256 - f] = '1';
        }

        var s = new string(bits);
        return string.Concat(Enumerable.Range(0, 64).Select(i => Convert.ToInt32(s.Substring(i * 4, 4), 2).ToString("X")));
    }
}
