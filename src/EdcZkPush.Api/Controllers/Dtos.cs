using System.ComponentModel;

namespace EdcZkPush.Controllers;

public sealed class UserRequest
{
    /// <summary>Controller serial number. Leave empty when only one controller is connected.</summary>
    public string? Sn { get; set; }

    /// <summary>User ID (PIN). Digits only unless the device reports StringPinFunOn=1. Max 14.</summary>
    /// <example>1001</example>
    public string Pin { get; set; } = "";

    /// <example>Ana Novak</example>
    public string? Name { get; set; }

    /// <summary>Keypad password (up to 6 digits). Empty = none.</summary>
    /// <example>1234</example>
    public string? Password { get; set; }

    /// <summary>Access group. 0 = no group (privileges come from userauthorize).</summary>
    /// <example>0</example>
    public int Group { get; set; }

    /// <summary>User valid from (local time). Null = no limit.</summary>
    /// <example>2026-10-20T00:00:00</example>
    public DateTime? ValidFrom { get; set; }

    /// <summary>User valid until (local time). Null = no limit. Must be before ~2133 (32-bit device field).</summary>
    /// <example>2030-12-31T23:59:59</example>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Optional card to add in the same call (decimal as printed on the card, or 0x-prefixed hex).</summary>
    /// <example>12345678</example>
    public string? CardNo { get; set; }

    /// <summary>Optional: send Privilege (0 user, 2 registrar, 6 admin, 14 super admin). The C3-200 Pro rejected this field (-621), so it is omitted when null.</summary>
    public int? Privilege { get; set; }

    /// <summary>Optional (2024/07+ docs): 1 = user disabled / blocklisted. Omitted when null.</summary>
    public int? Disable { get; set; }

    /// <summary>Auto = follow DateFmtFunOn from the registry.</summary>
    [DefaultValue(DateFormatMode.Auto)]
    public DateFormatMode DateFormat { get; set; } = DateFormatMode.Auto;

    /// <summary>Auto = mulcarduser if the device supports multiple cards per user, otherwise the user table.</summary>
    [DefaultValue(CardTarget.Auto)]
    public CardTarget CardTarget { get; set; } = CardTarget.Auto;
}

public sealed class UserBatchRequest
{
    public string? Sn { get; set; }

    public List<UserRequest> Users { get; set; } = new();
}

public sealed class CardRequest
{
    public string? Sn { get; set; }

    /// <summary>User the card belongs to (add the user first).</summary>
    /// <example>1001</example>
    public string Pin { get; set; } = "";

    /// <summary>Card number: decimal as printed / read (e.g. 12345678) or hex with 0x prefix (0xBC614E).</summary>
    /// <example>12345678</example>
    public string CardNo { get; set; } = "";

    /// <summary>0 = main card, 1 = secondary card.</summary>
    /// <example>0</example>
    public int CardType { get; set; }

    /// <summary>1 = card reported lost.</summary>
    /// <example>0</example>
    public int LossCardFlag { get; set; }

    /// <summary>Wiegand bit count — only for devices with WGRuleVer=2.0.</summary>
    public int? CardBit { get; set; }

    /// <summary>Site / facility code — only for devices with WGRuleVer=2.0.</summary>
    public string? SiteCode { get; set; }

    [DefaultValue(CardTarget.Auto)]
    public CardTarget Target { get; set; } = CardTarget.Auto;
}

public sealed class CardBatchRequest
{
    public string? Sn { get; set; }

    public List<CardRequest> Cards { get; set; } = new();
}

public sealed class AccessRequest
{
    public string? Sn { get; set; }

    /// <example>1001</example>
    public string Pin { get; set; } = "";

    /// <summary>Doors the user may open (1-4 on a C3-400 Plus). Empty = all doors.</summary>
    /// <example>[1,2,3,4]</example>
    public List<int>? Doors { get; set; }

    /// <summary>Alternative to Doors: the raw AuthorizeDoorId bit mask (1=door1, 2=door2, 4=door3, 8=door4, 15=all four).</summary>
    public int? DoorMask { get; set; }

    /// <summary>Time rule ("timezone") ID. 1 is the controller's built-in 24/7 rule.</summary>
    /// <example>1</example>
    public int TimezoneId { get; set; } = 1;

    /// <summary>Auto = include DevID=1 only when AccSupportFunList[26]=1.</summary>
    [DefaultValue(IncludeOption.Auto)]
    public IncludeOption IncludeDevId { get; set; } = IncludeOption.Auto;

    /// <summary>Access validity (only sent when AccSupportFunList[62]=1, or when forced with IncludeValidity=Yes).</summary>
    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidUntil { get; set; }

    [DefaultValue(IncludeOption.Auto)]
    public IncludeOption IncludeValidity { get; set; } = IncludeOption.Auto;
}

public sealed class AccessBatchRequest
{
    public string? Sn { get; set; }

    public List<AccessRequest> Items { get; set; } = new();
}

/// <summary>
/// A time rule ("timezone" in ZKTeco terms = when access is allowed). Each day takes up to three ranges "HH:mm-HH:mm".
/// EveryDay / Weekdays / Weekend are shortcuts that fill the individual days that are left empty.
/// </summary>
public sealed class TimeRuleRequest
{
    public string? Sn { get; set; }

    /// <summary>Time rule ID (2..n; 1 is usually the built-in 24h rule).</summary>
    /// <example>2</example>
    public int TimezoneId { get; set; }

    /// <example>[]</example>
    public List<string>? EveryDay { get; set; }

    /// <example>["07:00-12:00","12:30-17:00"]</example>
    public List<string>? Weekdays { get; set; }

    /// <example>["08:00-12:00"]</example>
    public List<string>? Weekend { get; set; }

    /// <example>[]</example>
    public List<string>? Monday { get; set; }
    /// <example>[]</example>
    public List<string>? Tuesday { get; set; }
    /// <example>[]</example>
    public List<string>? Wednesday { get; set; }
    /// <example>[]</example>
    public List<string>? Thursday { get; set; }
    /// <example>[]</example>
    public List<string>? Friday { get; set; }
    /// <example>[]</example>
    public List<string>? Saturday { get; set; }
    /// <example>[]</example>
    public List<string>? Sunday { get; set; }

    /// <summary>Ranges on days marked as holiday type 1/2/3 (see /api/holidays).</summary>
    /// <example>[]</example>
    public List<string>? Holiday1 { get; set; }
    /// <example>[]</example>
    public List<string>? Holiday2 { get; set; }
    /// <example>[]</example>
    public List<string>? Holiday3 { get; set; }

    /// <summary>Optional validity of the whole rule (only devices with AccSupportFunList[60]=1).</summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
}

public sealed class HolidayRequest
{
    public string? Sn { get; set; }

    /// <example>2026-12-25</example>
    public DateTime Date { get; set; }

    /// <summary>1, 2 or 3 — selects HolNTimeN ranges of the time rules.</summary>
    /// <example>1</example>
    public int HolidayType { get; set; } = 1;

    /// <summary>1 = every year on this month/day, 2 = only this exact date.</summary>
    /// <example>1</example>
    public int Loop { get; set; } = 1;
}

public sealed class OptionsSetRequest
{
    public string? Sn { get; set; }

    /// <summary>Parameter name → value. Example sets the lock-open time of door 1 to 5 s and the door sensor to normally-open.</summary>
    /// <example>{"Door1Drivertime":"5","Door1SensorType":"1"}</example>
    public Dictionary<string, string> Options { get; set; } = new();
}

public sealed class OptionsGetRequest
{
    public string? Sn { get; set; }

    /// <example>["IPAddress","NetMask","GATEIPAddress","DateTime","~SerialNumber","FirmVer","LockCount","Door1Drivertime","ComPwd"]</example>
    public List<string> Names { get; set; } = new();
}

public sealed class RawCommandRequest
{
    public string? Sn { get; set; }

    /// <summary>Command without "C:ID:". Use [TAB] and [LF] for tab / line feed.</summary>
    /// <example>DATA QUERY tablename=user,fielddesc=*,filter=*</example>
    public string Command { get; set; } = "";

    /// <example>Raw test</example>
    public string? Label { get; set; }
}

public sealed class LoggingSettings
{
    /// <summary>Show every ping / empty getrequest (otherwise one summary line per minute).</summary>
    public bool? ShowHeartbeat { get; set; }

    /// <summary>"all", "changes" or "none".</summary>
    /// <example>changes</example>
    public string? RtState { get; set; }

    public bool? ShowHeaders { get; set; }
    public bool? ShowBodies { get; set; }
    /// <example>6000</example>
    public int? MaxBodyChars { get; set; }
}
