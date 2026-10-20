using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>Users (table "user"). A user alone opens nothing: give them a card (/api/cards) and access (/api/access).</summary>
[Route("api/users")]
[Tags("2 · Users")]
public sealed class UsersController : ApiBase
{
    public UsersController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Add or update a user (optionally with a card in the same call).</summary>
    /// <remarks>
    /// Wire example (DateFmtFunOn=1, cards in mulcarduser):
    ///
    ///     C:101:DATA UPDATE user Pin=1001[TAB]Password=1234[TAB]Group=0[TAB]StartTime=861408000[TAB]EndTime=996364799[TAB]Name=Ana Novak
    ///     C:102:DATA UPDATE mulcarduser Pin=1001[TAB]CardNo=12345678[TAB]LossCardFlag=0[TAB]CardType=0
    ///
    /// Expected result: ID=101&amp;Return=0&amp;CMD=DATA UPDATE
    /// </remarks>
    /// <param name="request">User data. Leave sn empty when only one controller is connected.</param>
    /// <param name="preview">true = only show the command, do not queue it.</param>
    [HttpPost]
    public IActionResult AddOrUpdate([FromBody] UserRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;

        var notes = new List<string>();
        var commands = BuildUser(request, caps, notes).ToList();
        return Done(sn, preview, commands, notes);
    }

    /// <summary>Add or update many users in ONE command (records separated by LF).</summary>
    /// <remarks>
    ///     C:103:DATA UPDATE user Pin=1001[TAB]...[LF]Pin=1002[TAB]...
    /// Cards given in the batch are sent as a second, separate batch command.
    /// </remarks>
    [HttpPost("batch")]
    public IActionResult AddBatch([FromBody] UserBatchRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        if (request.Users.Count == 0) return BadRequest(new { error = "At least one user is required." });

        var notes = new List<string>();
        var userRecords = new List<string>();
        var cardRecords = new List<string>();
        foreach (var u in request.Users)
        {
            foreach (var (_, text) in BuildUser(u, caps, notes))
            {
                // Split the single-record commands back into table + record so they can be merged.
                const string userPrefix = "DATA UPDATE user ";
                const string cardPrefix = "DATA UPDATE mulcarduser ";
                if (text.StartsWith(userPrefix, StringComparison.Ordinal)) userRecords.Add(text[userPrefix.Length..]);
                else if (text.StartsWith(cardPrefix, StringComparison.Ordinal)) cardRecords.Add(text[cardPrefix.Length..]);
            }
        }

        var commands = new List<(string, string)> { ($"Add {userRecords.Count} users", SecurityCommands.Update("user", userRecords)) };
        if (cardRecords.Count > 0)
        {
            commands.Add(($"Add {cardRecords.Count} cards", SecurityCommands.Update("mulcarduser", cardRecords)));
        }

        return Done(sn, preview, commands, notes.Distinct());
    }

    /// <summary>Delete ONLY the user row (cards and access stay — usually you want /complete).</summary>
    /// <remarks>    C:104:DATA DELETE user Pin=1001</remarks>
    [HttpDelete("{pin}")]
    public IActionResult Delete(string pin, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ($"Delete user {pin}", $"DATA DELETE user Pin={pin}") });
    }

    /// <summary>Delete a user completely: access → cards → user (the order the protocol document uses).</summary>
    /// <remarks>
    ///     C:105:DATA DELETE userauthorize Pin=1001
    ///     C:106:DATA DELETE mulcarduser Pin=1001
    ///     C:107:DATA DELETE user Pin=1001
    /// Commands are delivered in order, one per poll by default. The old "delete workflow" service is not needed.
    /// </remarks>
    [HttpDelete("{pin}/complete")]
    public IActionResult DeleteComplete(string pin, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, caps, error) = Target(sn, preview);
        if (error != null) return error;

        var commands = new List<(string, string)> { ($"Delete access of {pin}", $"DATA DELETE userauthorize Pin={pin}") };
        if (caps.CardsInMulCardUser != false)
        {
            commands.Add(($"Delete cards of {pin}", $"DATA DELETE mulcarduser Pin={pin}"));
        }

        commands.Add(($"Delete user {pin}", $"DATA DELETE user Pin={pin}"));
        return Done(s, preview, commands);
    }

    /// <summary>Delete ALL users (table wipe). Requires confirm=true.</summary>
    /// <remarks>    C:108:DATA DELETE user *</remarks>
    [HttpDelete("all")]
    public IActionResult DeleteAll([FromQuery] bool confirm, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        if (!confirm && !preview) return BadRequest(new { error = "Add confirm=true — this removes every user from the controller." });
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[]
        {
            ("Delete all access", "DATA DELETE userauthorize *"),
            ("Delete all cards", "DATA DELETE mulcarduser *"),
            ("Delete all users", "DATA DELETE user *")
        });
    }

    /// <summary>Read all users back from the controller. Result rows appear in GET /api/commands/{id} → queryData.</summary>
    /// <remarks>    C:109:DATA QUERY tablename=user,fielddesc=*,filter=*</remarks>
    [HttpPost("query")]
    public IActionResult Query([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query users", SecurityCommands.Query("user")) });
    }

    /// <summary>Count users on the controller.</summary>
    /// <remarks>    C:110:DATA COUNT user</remarks>
    [HttpPost("count")]
    public IActionResult Count([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Count users", SecurityCommands.Count("user")) });
    }

    private IEnumerable<(string Label, string Text)> BuildUser(UserRequest r, DeviceCapabilities caps, List<string> notes)
    {
        ValidatePin(r.Pin, caps);
        if (r.ValidFrom.HasValue && r.ValidUntil.HasValue && r.ValidUntil < r.ValidFrom)
        {
            throw new ArgumentException("ValidUntil is before ValidFrom.");
        }

        var start = ValidityValue(r.ValidFrom, caps, r.DateFormat, notes, "StartTime");
        var end = ValidityValue(r.ValidUntil, caps, r.DateFormat, notes, "EndTime");

        string? cardInUser = null;
        string? cardRecord = null;
        if (!string.IsNullOrWhiteSpace(r.CardNo))
        {
            if (CardsInMulCardUser(caps, r.CardTarget))
            {
                cardRecord = SecurityCommands.CardRecord(r.Pin, MulCardNumber(r.CardNo, notes), 0, 0, null, null);
            }
            else
            {
                cardInUser = SecurityCommands.CardToDecimal(r.CardNo);
                notes.Add($"Card {r.CardNo} → user.CardNo={cardInUser} (decimal).");
            }
        }

        var record = SecurityCommands.UserRecord(r.Pin, r.Name, r.Password, r.Group, start, end, cardInUser, r.Privilege);
        if (r.Disable.HasValue)
        {
            record += $"\tDisable={r.Disable.Value}";
        }

        yield return ($"Add/update user {r.Pin}", SecurityCommands.Update("user", new[] { record }));
        if (cardRecord != null)
        {
            yield return ($"Add card for {r.Pin}", SecurityCommands.Update("mulcarduser", new[] { cardRecord }));
        }
    }
}
