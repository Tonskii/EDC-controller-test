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
    /// Sends what ZKBio CVSecurity sends (verified on the C3-400 Plus):
    ///
    ///     C:101:DATA UPDATE user CardNo=[TAB]Pin=1001[TAB]Password=1234[TAB]Group=0[TAB]StartTime=0[TAB]EndTime=0[TAB]Name=[TAB]SuperAuthorize=0[TAB]Disable=0
    ///     C:102:DATA UPDATE extuser Pin=1001[TAB]FunSwitch=0[TAB]FirstName=Ana Novak[TAB]LastName=
    ///     C:103:DATA DELETE SuperAuthorize Pin=1001
    ///     C:104:DATA UPDATE mulcarduser Pin=1001[TAB]CardNo=1d0456[TAB]LossCardFlag=0[TAB]CardType=0   (only with cardNo)
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
    ///     C:103:DATA UPDATE user CardNo=[TAB]Pin=1001[TAB]...[LF]CardNo=[TAB]Pin=1002[TAB]...
    ///     C:104:DATA UPDATE extuser Pin=1001[TAB]...[LF]Pin=1002[TAB]...
    ///     C:105:DATA DELETE SuperAuthorize Pin=1001[LF]Pin=1002
    ///     C:106:DATA UPDATE mulcarduser ...   (only if cards are given)
    /// One multi-record command per table, like ZKBio CVSecurity.
    /// </remarks>
    [HttpPost("batch")]
    public IActionResult AddBatch([FromBody] UserBatchRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        if (request.Users.Count == 0) return BadRequest(new { error = "At least one user is required." });

        var notes = new List<string>();
        var single = request.Users.SelectMany(u => BuildUser(u, caps, notes).ToList()).ToList();
        var commands = SecurityCommands.Merge(single);
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
    ///     C:107:DATA DELETE extuser Pin=1001
    ///     C:108:DATA DELETE user Pin=1001
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

        commands.Add(($"Delete ext. info of {pin}", $"DATA DELETE extuser Pin={pin}"));
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
            ("Delete all ext. info", "DATA DELETE extuser *"),
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

        // ZKBio CVSecurity (verified working on the C3-400 Plus 2026-10-21) sends, per user:
        //   DATA UPDATE user CardNo=<empty>\tPin\tPassword\tGroup\tStartTime\tEndTime\tName=<empty>\tSuperAuthorize=0\tDisable=0
        //   DATA UPDATE extuser Pin\tFunSwitch=0\tFirstName=<name>\tLastName=
        //   DATA DELETE SuperAuthorize Pin=<pin>
        //   DATA UPDATE mulcarduser Pin\tCardNo=<lowercase hex>\tLossCardFlag=0\tCardType=0
        var useExt = Options.UseExtUser;
        var record = SecurityCommands.UserRecord(r.Pin, useExt ? "" : r.Name, r.Password, r.Group, start, end, cardInUser,
            r.Privilege, r.SuperAuthorize,
            // CVSecurity sends Disable=0 to the C3-400 Plus (reports DisableUserFunOn=1) but not to the elevator controller.
            r.Disable ?? (caps.GetInt("DisableUserFunOn") == 0 || (caps.Raw.Count > 0 && caps.GetInt("DisableUserFunOn") == null) ? null : 0));
        yield return ($"Add/update user {r.Pin}", SecurityCommands.Update("user", new[] { record }));

        if (useExt)
        {
            notes.Add("Name sent in extuser.FirstName (user.Name left empty) like ZKBio CVSecurity.");
            yield return ($"Ext. info for {r.Pin}", SecurityCommands.Update("extuser", new[] { SecurityCommands.ExtUserRecord(r.Pin, r.Name, r.LastName) }));
        }

        if (Options.ClearSuperAuthorize && r.SuperAuthorize == 0)
        {
            yield return ($"Clear super-user rights of {r.Pin}", SecurityCommands.Delete("SuperAuthorize", new[] { $"Pin={r.Pin}" }));
        }

        if (cardRecord != null)
        {
            yield return ($"Add card for {r.Pin}", SecurityCommands.Update("mulcarduser", new[] { cardRecord }));
        }
    }
}
