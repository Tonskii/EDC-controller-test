namespace EdcZkPush.Protocol;

/// <summary>
/// Parsed body of POST /iclock/registry (Security PUSH 4.4 Registration).
/// Everything the device tells us about itself is kept, and the values that change
/// how commands must be built are exposed as typed properties.
/// </summary>
public sealed class DeviceCapabilities
{
    public DeviceCapabilities(IReadOnlyDictionary<string, string> raw)
    {
        Raw = raw;
    }

    /// <summary>All key/value pairs exactly as sent (keys without the leading "~").</summary>
    public IReadOnlyDictionary<string, string> Raw { get; }

    public string? Get(string key) => Raw.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string key) => int.TryParse(Get(key), out var v) ? v : null;

    public string? DeviceType => Get("DeviceType");
    public string? DeviceName => Get("DeviceName");
    public string? FirmwareVersion => Get("FirmVer");
    public string? PushVersion => Get("PushVersion");
    public int? LockCount => GetInt("LockCount");
    public int? ReaderCount => GetInt("ReaderCount");
    public int? MachineType => GetInt("MachineType");

    /// <summary>1 = StartTime/EndTime are encoded seconds (Appendix 5); 0 = YYYYMMDD.</summary>
    public int? DateFmtFunOn => GetInt("DateFmtFunOn");

    /// <summary>1 = commands need the "DataType=1,SN=..,CmdID=..,CmdDesc=" prefix (Appendix 14).</summary>
    public int? CmdFormat => GetInt("CmdFormat");

    /// <summary>"National standard level" — device and server must report the same value (added in protocol doc v4.2).</summary>
    public int? DevNSLevel => GetInt("DevNSLevel");

    public int? StringPinFunOn => GetInt("StringPinFunOn");
    public int? MulCardUserFunOn => GetInt("MulCardUserFunOn");
    public string? WgRuleVer => Get("WGRuleVer");
    public int? MultiStageControlFunOn => GetInt("MultiStageControlFunOn");
    public int? MasterControlOn => GetInt("MasterControlOn");
    public int? SubControlOn => GetInt("SubControlOn");
    public string? AccSupportFunList => Get("AccSupportFunList");

    /// <summary>1 = device understands "REMOTE OPENDOOREX id=..\topentime=..\tpin=.." (2026 doc 9.4.2).</summary>
    public int? RemoteOpenDoorExFunOn => GetInt("RemoteOpenDoorExFunOn");

    /// <summary>Returns true/false when the bit is present in AccSupportFunList, null when the list is missing or too short.</summary>
    public bool? Supports(int position)
    {
        var list = AccSupportFunList;
        if (string.IsNullOrEmpty(list) || position >= list.Length)
        {
            return null;
        }

        return list[position] == '1';
    }

    /// <summary>
    /// Cards live in the separate mulcarduser table only when the option MulCardUserFunOn=1 is ACTIVE on the controller.
    /// AccSupportFunList[7] only says the feature is SUPPORTED — per the protocol the server has to switch it on with
    /// SET OPTIONS MulCardUserFunOn=1. true/false = known, null = supported but current state unknown.
    /// </summary>
    public bool? CardsInMulCardUser => MulCardUserFunOn switch
    {
        1 => true,
        0 => false,
        _ => Supports(AccFunBits.MultipleCardsPerUser) == false ? false : null
    };

    public bool? MultiCardSupported => Supports(AccFunBits.MultipleCardsPerUser);

    /// <summary>Copy with values learned later (GET OPTIONS answers) layered over the registry values.</summary>
    public DeviceCapabilities With(IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(Raw, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides)
        {
            merged[kv.Key] = kv.Value;
        }

        return new DeviceCapabilities(merged);
    }

    public bool? SupportsDevIdField => Supports(AccFunBits.TableDevIdField);

    /// <summary>AccSupportFunList[15]: AuthorizeDoorId is a door NUMBER (one row per door), not a bit mask.</summary>
    public bool? DoorIdIsSingleDoor => Supports(AccFunBits.DoorIdSingleDoor);
    public bool? SupportsTimeRuleValidity => Supports(AccFunBits.TimeRuleValidPeriod);
    public bool? SupportsAccessLevelValidity => Supports(AccFunBits.PermissionGroupValidity);

    public static DeviceCapabilities Parse(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in body.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = token.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = token[..idx].Trim().TrimStart('~');
            var value = token[(idx + 1)..].Trim();
            dict[key] = value;
        }

        return new DeviceCapabilities(dict);
    }
}

/// <summary>Bit positions of AccSupportFunList (Security PUSH 4.4, read left to right starting at 0).</summary>
public static class AccFunBits
{
    public const int MultipleCardsPerUser = 7;
    public const int AccountCommand = 11;
    public const int DoorIdSingleDoor = 15;
    public const int TableDevIdField = 26;
    public const int Wiegand2 = 33;
    public const int SendQrCode = 35;
    public const int RemoteRegistration = 40;
    public const int ExpansionEx0808 = 47;
    public const int OsdpBoard = 49;
    public const int TimeRuleValidPeriod = 60;
    public const int NtpConfig = 61;
    public const int PermissionGroupValidity = 62;
    public const int VideoIntercom = 63;

    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0] = "Forbid reader",
        [1] = "RS485 reader encryption",
        [2] = "Lock door",
        [3] = "Emergency double open/close",
        [4] = "Wiegand reader LED follows door/relay",
        [5] = "Long name",
        [6] = "Wiegand format with site code",
        [7] = "Multiple cards per user (mulcarduser table)",
        [8] = "Exit button controlled by time period",
        [9] = "Auto-delete temporary users daily",
        [10] = "Wiegand test",
        [11] = "Account command",
        [12] = "Per-user verification mode by time period",
        [13] = "Control RS485 reader LED",
        [14] = "Multiple Wiegand formats",
        [15] = "DoorID means one door in authority table",
        [16] = "Independent super-user authority table",
        [17] = "Door attribute table",
        [18] = "Reader attribute table",
        [19] = "Aux input attribute table",
        [20] = "Aux output attribute table",
        [21] = "Door parameter table",
        [22] = "Anti-passback table",
        [23] = "Interlock table",
        [24] = "Wireless serial",
        [25] = "4G USB",
        [26] = "DevID field in tables (userauthorize DevID=)",
        [27] = "Secondary machine",
        [28] = "Dual network card",
        [29] = "Authorization table",
        [30] = "Identity card registration",
        [31] = "Primary/secondary switch",
        [32] = "Mixed reader + Wiegand/RS485",
        [33] = "Wiegand data management (byte swap/bit reversal)",
        [34] = "Desfire card control",
        [35] = "Send QR code command",
        [36] = "Advertisement command",
        [37] = "Indoor station",
        [38] = "Upload comparison photo",
        [39] = "Upload visible-light face template",
        [40] = "Remote registration",
        [41] = "Verification result style",
        [42] = "Authentication server",
        [43] = "Resource file delivery",
        [44] = "AI device connection",
        [45] = "Expansion board DM10",
        [46] = "Expansion board AUX485",
        [47] = "Expansion board EX0808",
        [48] = "Super user passes locked door",
        [49] = "OSDP expansion board",
        [50] = "User focusing",
        [51] = "Display complete information",
        [52] = "Elevator expansion board",
        [53] = "Elevator direct access",
        [54] = "Elevator emergency recovery",
        [55] = "Elevator direct access privilege group",
        [56] = "Elevator optimisation",
        [57] = "Voice module",
        [58] = "Negative floors",
        [59] = "Card number on identity card",
        [60] = "Time rule validity period (timezone StartTime/EndTime)",
        [61] = "NTP configuration",
        [62] = "Permission validity period (userauthorize StartTime/EndTime)",
        [63] = "Video intercom contact list",
        [64] = "Reader parameters/properties delivery (2026 doc)",
        [65] = "IPC linkage (2026 doc)",
        [66] = "New anti-passback & interlock rules (2026 doc)",
    };
}
