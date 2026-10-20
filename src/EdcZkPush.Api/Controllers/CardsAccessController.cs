using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>Cards. On C3 controllers cards normally live in table "mulcarduser" (several cards per user).</summary>
[Route("api/cards")]
[Tags("3 · Cards")]
public sealed class CardsController : ApiBase
{
    public CardsController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Add a card to an existing user.</summary>
    /// <remarks>
    /// mulcarduser (default on C3):
    ///
    ///     C:111:DATA UPDATE mulcarduser Pin=1001[TAB]CardNo=12345678[TAB]LossCardFlag=0[TAB]CardType=0
    ///
    /// user table (devices without multi-card support):
    ///
    ///     C:111:DATA UPDATE user CardNo=12345678[TAB]Pin=1001
    ///
    /// Enter the number in decimal, or hex with 0x (the swipe event shows hex). Written to mulcarduser in DECIMAL by default
    /// (Push:MulCardNumberFormat) — that is what ZKBio CVSecurity writes and what the C3-400 Plus matches.
    /// </remarks>
    [HttpPost]
    public IActionResult Add([FromBody] CardRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        var notes = new List<string>();
        return Done(sn, preview, new[] { Build(request, caps, notes) }, notes);
    }

    /// <summary>Add many cards in one command (records separated by LF).</summary>
    [HttpPost("batch")]
    public IActionResult AddBatch([FromBody] CardBatchRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        if (request.Cards.Count == 0) return BadRequest(new { error = "At least one card is required." });

        var notes = new List<string>();
        var built = request.Cards.Select(c => Build(c, caps, notes)).ToList();
        var groups = built.GroupBy(b => b.Text.StartsWith("DATA UPDATE mulcarduser ") ? "mulcarduser" : "user");
        var commands = groups.Select(g =>
        {
            var prefix = $"DATA UPDATE {g.Key} ";
            return ($"Add {g.Count()} cards ({g.Key})", SecurityCommands.Update(g.Key, g.Select(x => x.Text[prefix.Length..])));
        });
        return Done(sn, preview, commands, notes.Distinct());
    }

    /// <summary>Delete one card (cardNo given) or all cards of a user.</summary>
    /// <remarks>
    ///     C:112:DATA DELETE mulcarduser Pin=1001                 (all cards of the user)
    ///     C:112:DATA DELETE mulcarduser Pin=1001[TAB]CardNo=12345678  (one card)
    /// user-table mode clears the field instead:  DATA UPDATE user CardNo=[TAB]Pin=1001
    /// </remarks>
    [HttpDelete("{pin}")]
    public IActionResult Delete(string pin, [FromQuery] string? cardNo = null, [FromQuery] CardTarget target = CardTarget.Auto,
        [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, caps, error) = Target(sn, preview);
        if (error != null) return error;

        if (!CardsInMulCardUser(caps, target))
        {
            return Done(s, preview, new[] { ($"Clear card of {pin}", SecurityCommands.Update("user", new[] { $"CardNo=\tPin={pin}" })) });
        }

        var cond = string.IsNullOrWhiteSpace(cardNo) ? $"Pin={pin}" : $"Pin={pin}\tCardNo={MulCardNumber(cardNo)}";
        return Done(s, preview, new[] { ($"Delete card(s) of {pin}", SecurityCommands.Delete("mulcarduser", new[] { cond })) });
    }

    /// <summary>Read all cards back (rows appear in GET /api/commands/{id}).</summary>
    /// <remarks>    C:113:DATA QUERY tablename=mulcarduser,fielddesc=*,filter=*</remarks>
    [HttpPost("query")]
    public IActionResult Query([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query cards", SecurityCommands.Query("mulcarduser")) });
    }

    private (string Label, string Text) Build(CardRequest r, DeviceCapabilities caps, List<string> notes)
    {
        ValidatePin(r.Pin, caps);
        if (string.IsNullOrWhiteSpace(r.CardNo)) throw new ArgumentException("CardNo is required.");

        if (CardsInMulCardUser(caps, r.Target))
        {
            var hex = MulCardNumber(r.CardNo, notes);
            if (r.Target == CardTarget.Auto && caps.CardsInMulCardUser == null)
            {
                notes.Add("WARNING: the controller only looks in mulcarduser when MulCardUserFunOn=1. It is not known whether that is on — " +
                          "check with POST /api/device/options/get {\"names\":[\"MulCardUserFunOn\"]}.");
            }
            if ((r.CardBit.HasValue || !string.IsNullOrEmpty(r.SiteCode)) && caps.WgRuleVer?.Contains('2') != true)
            {
                notes.Add("CardBit/SiteCode are only understood with WGRuleVer=2.0 — the device may answer -621.");
            }

            return ($"Add card {r.CardNo} to {r.Pin}",
                SecurityCommands.Update("mulcarduser", new[] { SecurityCommands.CardRecord(r.Pin, hex, r.LossCardFlag, r.CardType, r.CardBit, r.SiteCode) }));
        }

        var dec = SecurityCommands.CardToDecimal(r.CardNo);
        notes.Add($"Card {r.CardNo} → user.CardNo={dec} (only Pin+CardNo are sent; other user fields are kept by the device as an update).");
        return ($"Set card {r.CardNo} on user {r.Pin}", SecurityCommands.Update("user", new[] { $"CardNo={dec}\tPin={r.Pin}" }));
    }
}

/// <summary>Door access rights (table "userauthorize"): which doors, during which time rule.</summary>
[Route("api/access")]
[Tags("4 · Access (doors + time rule)")]
public sealed class AccessController : ApiBase
{
    public AccessController(DeviceStore d, CommandQueue q, IOptions<PushOptions> o) : base(d, q, o) { }

    /// <summary>Give a user access to doors during a time rule.</summary>
    /// <remarks>
    /// All 4 doors of a C3-400 Plus, time rule 2, DevID (C3-400 Plus reports AccSupportFunList[26]=1):
    ///
    ///     {"pin":"1001","doors":[1,2,3,4],"timezoneId":2,"includeDevId":"Yes"}
    ///     C:114:DATA UPDATE userauthorize Pin=1001[TAB]AuthorizeTimezoneId=2[TAB]AuthorizeDoorId=15[TAB]DevID=190
    ///
    /// includeDevId: Auto = add DevID when the controller reported support at registration (remembered across
    /// server restarts in ./devices), Yes = always, No = never.
    /// The VALUE is per controller: devId in the request → PUT /api/devices/{sn}/devid → appsettings Push:DevIds[sn].
    ///
    /// AuthorizeDoorId is a bit mask: door1=1, door2=2, door3=4, door4=8 (15 = all). The old API rejected values above 3
    /// and the old bulk push always sent 3 (doors 1+2 only).
    /// </remarks>
    [HttpPut]
    public IActionResult Set([FromBody] AccessRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        var notes = new List<string>();
        return Done(sn, preview, new[] { Build(sn, request, caps, notes) }, notes);
    }

    /// <summary>Set access for many users in one command.</summary>
    [HttpPut("batch")]
    public IActionResult SetBatch([FromBody] AccessBatchRequest request, [FromQuery] bool preview = false)
    {
        var (sn, caps, error) = Target(request.Sn, preview);
        if (error != null) return error;
        if (request.Items.Count == 0) return BadRequest(new { error = "At least one item is required." });

        var notes = new List<string>();
        const string prefix = "DATA UPDATE userauthorize ";
        var records = request.Items.Select(i => Build(sn, i, caps, notes).Text[prefix.Length..]).ToList();
        return Done(sn, preview, new[] { ($"Set access for {records.Count} users", SecurityCommands.Update("userauthorize", records)) }, notes.Distinct());
    }

    /// <summary>Remove all door access of a user.</summary>
    /// <remarks>    C:115:DATA DELETE userauthorize Pin=1001</remarks>
    [HttpDelete("{pin}")]
    public IActionResult Delete(string pin, [FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ($"Delete access of {pin}", $"DATA DELETE userauthorize Pin={pin}") });
    }

    /// <summary>Read all access rights back.</summary>
    /// <remarks>    C:116:DATA QUERY tablename=userauthorize,fielddesc=*,filter=*</remarks>
    [HttpPost("query")]
    public IActionResult Query([FromQuery] string? sn = null, [FromQuery] bool preview = false)
    {
        var (s, _, error) = Target(sn, preview);
        if (error != null) return error;
        return Done(s, preview, new[] { ("Query access", SecurityCommands.Query("userauthorize")) });
    }

    private (string Label, string Text) Build(string sn, AccessRequest r, DeviceCapabilities caps, List<string> notes)
    {
        ValidatePin(r.Pin, caps);
        if (r.TimezoneId < 1) throw new ArgumentException("TimezoneId must be 1 or higher.");

        var mask = DoorMask(r.Doors, r.DoorMask, caps);
        notes.Add($"AuthorizeDoorId={mask} → doors {string.Join(",", Enumerable.Range(1, 8).Where(d => (mask & (1 << (d - 1))) != 0))}.");

        bool include;
        string why;
        if (r.IncludeDevId == IncludeOption.No)
        {
            include = false;
            why = "includeDevId=No";
        }
        else if (r.IncludeDevId == IncludeOption.Yes || r.DevId.HasValue)
        {
            include = true;
            why = r.DevId.HasValue ? "devId was given" : "includeDevId=Yes";
        }
        else if (caps.SupportsDevIdField == true)
        {
            include = true;
            why = "controller reported AccSupportFunList[26]=1";
        }
        else if (caps.SupportsDevIdField == null)
        {
            include = false;
            why = "controller capabilities unknown (not registered since server start, no saved registry) — set includeDevId=Yes";
        }
        else
        {
            include = false;
            why = "controller reported AccSupportFunList[26]=0";
        }

        int? devId = null;
        if (include)
        {
            var (value, source) = ResolveDevId(sn, r.DevId);
            if (value == null)
            {
                throw new ArgumentException(
                    $"DevID is needed ({why}) but unknown for controller {sn}. Give \"devId\" in the request, " +
                    $"or set it once with PUT /api/devices/{sn}/devid?value=..., or add it to appsettings Push:DevIds.");
            }

            devId = value;
            notes.Add($"DevID={devId} (from {source}); sent because {why}.");
        }
        else
        {
            notes.Add($"DevID: not sent ({why}).");
        }

        long? start = null, end = null;
        var sendValidity = r.IncludeValidity switch
        {
            IncludeOption.Yes => true,
            IncludeOption.No => false,
            _ => caps.SupportsAccessLevelValidity == true && (r.ValidFrom.HasValue || r.ValidUntil.HasValue)
        };
        if (sendValidity)
        {
            start = r.ValidFrom.HasValue ? ZkTime.Encode(r.ValidFrom.Value) : 0;
            end = r.ValidUntil.HasValue ? ZkTime.Encode(r.ValidUntil.Value) : 0;
        }
        else if (r.ValidFrom.HasValue || r.ValidUntil.HasValue)
        {
            notes.Add("Access validity ignored: device did not report AccSupportFunList[62]=1 (use IncludeValidity=Yes to force).");
        }

        return ($"Access {r.Pin} → mask {mask}, rule {r.TimezoneId}",
            SecurityCommands.Update("userauthorize", new[] { SecurityCommands.AccessRecord(r.Pin, r.TimezoneId, mask, devId, start, end) }));
    }
}
