namespace EdcZkPush.Protocol;

/// <summary>Turns the registry body into the things that matter for building commands.</summary>
public static class CapabilityReport
{
    public static IEnumerable<string> Summarize(DeviceCapabilities c, PushOptions opt)
    {
        yield return $"DEVICE: type={c.DeviceType} name={c.DeviceName} firmware={c.FirmwareVersion} push={c.PushVersion} " +
                     $"doors={c.LockCount} readers={c.ReaderCount} machineType={c.MachineType}";

        if (c.IsElevator)
        {
            yield return $"ELEVATOR CONTROLLER (MachineType={c.MachineType}): floor access via /api/elevator/access (table usereleauthorize), " +
                         "users/cards/time rules via the normal endpoints.";
        }

        if (!string.Equals(c.DeviceType, "acc", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"WARNING: DeviceType='{c.DeviceType}' (expected 'acc' for an access controller).";
        }

        foreach (var line in Decisions(c, opt))
        {
            yield return line;
        }
    }

    public static IEnumerable<string> Decisions(DeviceCapabilities c, PushOptions opt)
    {
        yield return c.DateFmtFunOn switch
        {
            1 => "Validity dates: DateFmtFunOn=1 → StartTime/EndTime sent as encoded seconds (Appendix 5).",
            0 => "Validity dates: DateFmtFunOn=0 → StartTime/EndTime sent as YYYYMMDD (the old server always sent seconds!).",
            _ => "Validity dates: DateFmtFunOn not reported → defaulting to encoded seconds (old behaviour)."
        };

        yield return c.CardsInMulCardUser switch
        {
            true => "Cards: MulCardUserFunOn=1 → cards go to table 'mulcarduser' as lowercase hex (as in the swipe event).",
            false => "Cards: multi-card OFF (MulCardUserFunOn=0 or not supported) → cards go into user.CardNo.",
            _ => "Cards: multi-card SUPPORTED (AccSupportFunList[7]=1) but whether it is SWITCHED ON is unknown → " +
                 "check with GET OPTIONS MulCardUserFunOn. Until then cards go to 'mulcarduser'; if swipes give EVENT 27, " +
                 "SET OPTIONS MulCardUserFunOn=1 or use cardTarget=UserTable."
        };

        var doors = c.LockCount ?? 4;
        yield return c.DoorIdIsSingleDoor == true
            ? $"Doors: {doors}, AccSupportFunList[15]=1 → userauthorize gets ONE ROW PER DOOR with AuthorizeDoorId=1..{doors} (like ZKBio CVSecurity)."
            : $"Doors: {doors} → userauthorize AuthorizeDoorId bit mask 1..{(1 << doors) - 1}.";

        yield return c.SupportsDevIdField == true
            ? (opt.AutoDetectDevId
                ? "userauthorize: AccSupportFunList[26]=1 → DevID will be added; its value is read from the controller's DevProperty table right after the handshake."
                : "userauthorize: AccSupportFunList[26]=1 → DevID will be added (value from PUT /api/devices/{sn}/devid or Push:DevIds).")
            : "userauthorize: DevID field not supported → omitted unless includeDevId=Yes.";

        if (c.SupportsAccessLevelValidity == true)
        {
            yield return "userauthorize: AccSupportFunList[62]=1 → access validity StartTime/EndTime can be sent.";
        }

        if (c.SupportsTimeRuleValidity == true)
        {
            yield return "timezone: AccSupportFunList[60]=1 → time rule StartTime/EndTime can be sent.";
        }

        yield return c.StringPinFunOn == 1
            ? "PIN: StringPinFunOn=1 → alphanumeric PINs allowed."
            : "PIN: digits only (StringPinFunOn != 1).";

        if (c.CmdFormat.HasValue)
        {
            var effective = opt.CmdFormat is "0" or "1" ? $"{opt.CmdFormat} (forced in appsettings)" : c.CmdFormat.ToString();
            yield return $"CmdFormat={c.CmdFormat} → commands sent with format {effective}" +
                         (c.CmdFormat == 1 ? " (DataType=1,SN=..,CmdID=..,CmdDesc=C:..: prefix, Appendix 14)." : " (plain C:ID:COMMAND).");
        }

        if (c.DevNSLevel.HasValue)
        {
            yield return $"DevNSLevel={c.DevNSLevel} → the server must answer the same SvrNSLevel (EchoNsLevel={opt.EchoNsLevel}).";
        }

        if (c.MultiStageControlFunOn == 1)
        {
            yield return $"Multi-level control: master={c.MasterControlOn} sub={c.SubControlOn} → results may come back as -5000 (pending).";
        }

        if (!string.IsNullOrEmpty(c.WgRuleVer))
        {
            yield return $"Wiegand rule version {c.WgRuleVer}" + (c.WgRuleVer.Contains('2') ? " → mulcarduser accepts CardBit/SiteCode." : ".");
        }

        if (c.RemoteOpenDoorExFunOn == 1)
        {
            yield return "REMOTE OPENDOOREX supported (open door + attach a PIN to the event, 2026 doc 9.4.2).";
        }

        if (c.Get("authKey") is { Length: > 0 } key)
        {
            yield return $"authKey/communication key reported: '{key}'.";
        }
    }

    /// <summary>Every AccSupportFunList bit with its meaning, for the device details endpoint.</summary>
    public static IEnumerable<object> Bits(DeviceCapabilities c)
    {
        var list = c.AccSupportFunList ?? "";
        for (var i = 0; i < list.Length; i++)
        {
            yield return new
            {
                position = i,
                supported = list[i] == '1',
                meaning = AccFunBits.Names.TryGetValue(i, out var n) ? n : "(not documented)"
            };
        }
    }
}
