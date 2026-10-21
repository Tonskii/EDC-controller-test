using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>
/// Time rules — ZKTeco calls them "timezone" (table "timezone"): the hours in which access is allowed.
/// A user gets a rule through /api/access (AuthorizeTimezoneId).
/// </summary>
[Route("api/timerules")]
[Tags("5 · Time rules (timezones) & holidays")]
public sealed class TimeRulesController : ApiBase
{
    public TimeRulesController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Create or update a time rule.</summary>
    /// <remarks>
    /// Each range is encoded as (HHMM start &lt;&lt; 16) + HHMM end, e.g. 07:00-12:00 → 700&lt;&lt;16+1200 = 45876400.
    /// Example (weekdays 07:00-12:00 and 12:30-17:00, weekend 08:00-12:00), shortened:
    ///
    ///     C:117:DATA UPDATE timezone TimezoneId=2[TAB]SunTime1=52430000[TAB]SunTime2=0[TAB]SunTime3=0[TAB]MonTime1=45876400[TAB]MonTime2=80610980[TAB]...[TAB]Hol3Time3=0
    ///
    /// All 30 slots are always sent (7 days + 3 holiday types × 3 ranges).
    /// </remarks>
    [HttpPut]
    public IActionResult Set([FromBody] TimeRuleRequest r, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.TimezoneId < 1) return BadRequest(new { error = "TimezoneId must be 1 or higher." });

        var notes = new List<string>();
        if (r.TimezoneId == 1)
        {
            notes.Add("Rule 1 is the 24h rule ZKBio CVSecurity creates (00:00-23:59 every day incl. holidays) — overwriting it changes access for everyone using it.");
        }

        // Day order in the protocol: Sun, Mon, Tue, Wed, Thu, Fri, Sat, Hol1, Hol2, Hol3.
        var days = new[]
        {
            Pick(r.Sunday, r.Weekend, r.EveryDay),
            Pick(r.Monday, r.Weekdays, r.EveryDay),
            Pick(r.Tuesday, r.Weekdays, r.EveryDay),
            Pick(r.Wednesday, r.Weekdays, r.EveryDay),
            Pick(r.Thursday, r.Weekdays, r.EveryDay),
            Pick(r.Friday, r.Weekdays, r.EveryDay),
            Pick(r.Saturday, r.Weekend, r.EveryDay),
            // "everyDay" also covers the three holiday types, exactly like ZKBio CVSecurity's 24h rule
            // (SunTime1..Hol3Time1 = 2359, i.e. 00:00-23:59).
            Pick(r.Holiday1, null, r.EveryDay),
            Pick(r.Holiday2, null, r.EveryDay),
            Pick(r.Holiday3, null, r.EveryDay)
        };

        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Hol1", "Hol2", "Hol3" };
        var slots = new List<int>();
        for (var i = 0; i < days.Length; i++)
        {
            var ranges = days[i].Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (ranges.Count > 3) return BadRequest(new { error = $"{names[i]}: at most 3 ranges per day." });
            for (var k = 0; k < 3; k++)
            {
                slots.Add(k < ranges.Count ? ZkTime.EncodeSegment(ranges[k]) : 0);
            }

            notes.Add($"{names[i]}: {(ranges.Count == 0 ? "no access" : string.Join(", ", ranges))}");
        }

        long? start = null, end = null;
        if (r.ValidFrom.HasValue || r.ValidUntil.HasValue)
        {
            if (caps.SupportsTimeRuleValidity == true || preview)
            {
                start = r.ValidFrom.HasValue ? ZkTime.Encode(r.ValidFrom.Value) : 0;
                end = r.ValidUntil.HasValue ? ZkTime.Encode(r.ValidUntil.Value) : 0;
            }
            else
            {
                notes.Add("Rule validity ignored: device did not report AccSupportFunList[60]=1.");
            }
        }

        return Done(sn, preview,
            new[] { ($"Time rule {r.TimezoneId}", SecurityCommands.Update("timezone", new[] { SecurityCommands.TimezoneRecord(r.TimezoneId, slots, start, end) })) },
            notes);
    }

    /// <summary>Delete a time rule.</summary>
    /// <remarks>    C:118:DATA DELETE timezone TimezoneId=2</remarks>
    [HttpDelete("{timezoneId:int}")]
    public IActionResult Delete(int timezoneId, [FromQuery] bool allowRule1 = false, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        if (timezoneId == 1 && !allowRule1) return BadRequest(new { error = "Rule 1 is the 24h rule most users reference. Add allowRule1=true if you really mean it." });
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ($"Delete time rule {timezoneId}", $"DATA DELETE timezone TimezoneId={timezoneId}") });
    }

    /// <summary>Read all time rules back (rows in GET /api/commands/{id}).</summary>
    /// <remarks>    C:119:DATA QUERY tablename=timezone,fielddesc=*,filter=*</remarks>
    [HttpPost("query")]
    public IActionResult Query([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query time rules", SecurityCommands.Query("timezone")) });
    }

    /// <summary>Decode a slot value, e.g. 45876400 → 07:00-12:00 (handy when reading query results).</summary>
    [HttpGet("decode/{value:long}")]
    public IActionResult Decode(long value) => Ok(new { value, range = ZkTime.DecodeSegment(value) });

    /// <summary>Add or update a holiday.</summary>
    /// <remarks>    C:120:DATA UPDATE holiday Holiday=20261225[TAB]HolidayType=1[TAB]Loop=1</remarks>
    [HttpPut("/api/holidays")]
    public IActionResult SetHoliday([FromBody] HolidayRequest r, [FromQuery] bool preview = false)
    {
        var (sn, _, error) = Target(r.Sn, preview);
        if (error != null) return error;
        if (r.HolidayType is < 1 or > 3) return BadRequest(new { error = "HolidayType must be 1, 2 or 3." });
        return Done(sn, preview, new[]
        {
            ($"Holiday {r.Date:yyyy-MM-dd}", SecurityCommands.Update("holiday", new[] { SecurityCommands.HolidayRecord(DateOnly.FromDateTime(r.Date), r.HolidayType, r.Loop) }))
        });
    }

    /// <summary>Delete one holiday (date given) or all holidays.</summary>
    /// <remarks>
    ///     C:121:DATA DELETE holiday Holiday=20261225
    ///     C:121:DATA DELETE holiday *
    /// </remarks>
    [HttpDelete("/api/holidays")]
    public IActionResult DeleteHoliday([FromQuery] DateTime? date = null, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        var cond = date.HasValue ? $"Holiday={date:yyyyMMdd}" : "*";
        return Done(s, preview, new[] { ("Delete holiday(s)", $"DATA DELETE holiday {cond}") });
    }

    private static List<string> Pick(List<string>? day, List<string>? group, List<string>? all)
    {
        if (day is { Count: > 0 }) return day;
        if (group is { Count: > 0 }) return group;
        return all ?? new List<string>();
    }
}
