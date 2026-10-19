namespace EdcZkPush.Protocol;

/// <summary>Turns the registry body into the things that matter for building commands.</summary>
public static class CapabilityReport
{
    public static IEnumerable<string> Summarize(DeviceCapabilities c, PushOptions opt)
    {
        yield return $"DEVICE: type={c.DeviceType} name={c.DeviceName} firmware={c.FirmwareVersion} push={c.PushVersion} " +
                     $"doors={c.LockCount} readers={c.ReaderCount} machineType={c.MachineType}";

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
            true => "Cards: multi-card per user supported → cards go to table 'mulcarduser' (hex card number).",
            false => "Cards: no multi-card support reported → cards go into the 'user' table CardNo field.",
            _ => "Cards: AccSupportFunList missing → defaulting to 'mulcarduser' (old behaviour)."
        };

        var doors = c.LockCount ?? 4;
        yield return $"Doors: {doors} → userauthorize AuthorizeDoorId bit mask 1..{(1 << doors) - 1} (old server only allowed 1..3).";

        yield return c.SupportsDevIdField == true
            ? "userauthorize: AccSupportFunList[26]=1 → DevID=1 will be added."
            : "userauthorize: DevID field not supported → omitted.";

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
