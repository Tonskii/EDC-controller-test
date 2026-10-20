using System.Globalization;

namespace EdcZkPush.Protocol;

/// <summary>Parses rtlog / rtstate / tabledata bodies into human-readable lines for the console.</summary>
public static class RealtimeParser
{
    public static Dictionary<string, string> ParseTabRecord(string record, char separator = '\t')
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            dict[part[..idx].Trim()] = part[(idx + 1)..].Trim();
        }

        return dict;
    }

    public static IEnumerable<string> Records(string body) =>
        body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim('\r'))
            .Where(r => r.Length > 0);

    public static IEnumerable<string> DescribeRtLog(string body)
    {
        foreach (var record in Records(body))
        {
            var f = ParseTabRecord(record);
            f.TryGetValue("event", out var ev);
            var code = int.TryParse(ev, out var c) ? c : -1;
            f.TryGetValue("eventaddr", out var door);
            f.TryGetValue("pin", out var pin);
            f.TryGetValue("cardno", out var card);
            f.TryGetValue("time", out var time);
            f.TryGetValue("inoutstatus", out var io);
            f.TryGetValue("verifytype", out var vt);
            f.TryGetValue("time_offset", out var offset);

            var who = string.IsNullOrEmpty(pin) || pin == "0" ? "" : $" pin={pin}";
            var cardText = string.IsNullOrEmpty(card) || card == "0" ? "" : $" card={card}";
            var inout = io == "0" ? "IN" : io == "1" ? "OUT" : io;
            yield return $"EVENT {code} \"{EventCodes.Describe(code)}\" door={door} {inout}{who}{cardText} verify={DescribeVerify(vt)} at {time}{(string.IsNullOrEmpty(offset) ? "" : $" (UTC offset {offset}s)")}";
        }
    }

    public static string DescribeRtState(string body)
    {
        var f = ParseTabRecord(Records(body).FirstOrDefault() ?? "");
        f.TryGetValue("sensor", out var sensor);
        f.TryGetValue("relay", out var relay);
        f.TryGetValue("alarm", out var alarm);
        f.TryGetValue("time", out var time);

        var doors = new List<string>();
        for (var door = 1; door <= 4; door++)
        {
            var s = SensorState(sensor, door);
            var r = RelayState(relay, door);
            var a = AlarmState(alarm, door);
            doors.Add($"D{door}: {s}, relay {r}{(a.Length > 0 ? ", ALARM " + a : "")}");
        }

        // C3-400 Plus firmware 19.x sends a longer format (sensor=10 chars, relay=6, door=..) than the document;
        // the decode above follows the document, so the raw values are shown too.
        var raw = string.Join(" ", f.Where(kv => !kv.Key.Equals("time", StringComparison.OrdinalIgnoreCase) &&
                                                  !kv.Key.Equals("sn", StringComparison.OrdinalIgnoreCase))
                                    .Select(kv => $"{kv.Key}={kv.Value}"));
        return $"STATE at {time} | {string.Join(" | ", doors)} | raw: {raw}";
    }

    private static string SensorState(string? sensorHex, int door)
    {
        if (string.IsNullOrEmpty(sensorHex) || sensorHex.Length < 2 ||
            !byte.TryParse(sensorHex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return "sensor ?";
        }

        var bits = (b >> ((door - 1) * 2)) & 0b11;
        return bits switch
        {
            0 => "no sensor",
            1 => "closed",
            2 => "OPEN",
            _ => "sensor 3?"
        };
    }

    private static string RelayState(string? relayHex, int door)
    {
        if (string.IsNullOrEmpty(relayHex) ||
            !int.TryParse(relayHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return "?";
        }

        return ((v >> (door - 1)) & 1) == 1 ? "energised" : "idle";
    }

    private static string AlarmState(string? alarmHex, int door)
    {
        if (string.IsNullOrEmpty(alarmHex) || alarmHex.Length < door * 2 ||
            !byte.TryParse(alarmHex.AsSpan((door - 1) * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ||
            b == 0)
        {
            return "";
        }

        string[] names = { "forced open", "tamper", "duress password", "duress fingerprint", "door open too long", "mains failure", "battery failure", "reader removed" };
        return string.Join("+", names.Where((_, i) => ((b >> i) & 1) == 1));
    }

    public static string DescribeVerify(string? vt)
    {
        if (string.IsNullOrEmpty(vt))
        {
            return "-";
        }

        if (vt.Length >= 12 && vt.All(ch => ch is '0' or '1'))
        {
            // New-style bit string (VF_TYPE_*), right-most char = bit 0.
            string[] names = { "AND", "face", "palmprint", "palm vein", "fingerprint", "finger vein", "voice", "iris", "retina", "password", "user ID", "card", "QR" };
            var set = new List<string>();
            for (var bit = 1; bit < names.Length && bit < vt.Length; bit++)
            {
                if (vt[vt.Length - 1 - bit] == '1')
                {
                    set.Add(names[bit]);
                }
            }

            return $"{vt}({string.Join(vt[^1] == '1' ? "&" : "|", set)})";
        }

        return int.TryParse(vt, out var code) && VerifyModes.TryGetValue(code, out var name) ? $"{code}({name})" : vt;
    }

    private static readonly Dictionary<int, string> VerifyModes = new()
    {
        [0] = "auto", [1] = "fingerprint", [2] = "user ID", [3] = "password", [4] = "card",
        [5] = "fp|pwd", [6] = "fp|card", [7] = "card|pwd", [8] = "ID&fp", [9] = "fp&pwd",
        [10] = "card&fp", [11] = "card&pwd", [12] = "fp&pwd&card", [13] = "ID&fp&pwd",
        [15] = "face", [25] = "palm", [200] = "other/door event"
    };
}

/// <summary>Real-time event codes, Appendix 2 and 19 of the Security PUSH protocol.</summary>
public static class EventCodes
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "Normal open after verification",
        [1] = "Verified during normal-open period",
        [2] = "First-card open",
        [3] = "Multi-card open",
        [4] = "Emergency password open",
        [5] = "Opened in normal-open period",
        [6] = "Linkage triggered",
        [7] = "Alarm cancelled",
        [8] = "Remote open",
        [9] = "Remote close",
        [10] = "Normal-open period disabled for the day",
        [11] = "Normal-open period enabled for the day",
        [12] = "Aux output on (remote)",
        [13] = "Aux output off (remote)",
        [14] = "Normal open after verification",
        [15] = "Multi-card open",
        [16] = "Verified during normal-open period",
        [17] = "Normal open after verification",
        [18] = "First-card open",
        [19] = "First-card open",
        [20] = "Operation interval too short",
        [21] = "Door inactive time zone (denied)",
        [22] = "Illegal time zone (denied)",
        [23] = "Access denied (no privilege for this door)",
        [24] = "Anti-passback",
        [25] = "Interlock",
        [26] = "Multi-card verification waiting",
        [27] = "Card/user not registered",
        [28] = "Door open timeout",
        [29] = "Card/user validity expired",
        [30] = "Wrong password",
        [33] = "Fingerprint validity expired",
        [34] = "Fingerprint not registered",
        [36] = "Exit button during inactive period",
        [38] = "Card reported lost",
        [39] = "Blacklisted",
        [41] = "Wrong verification mode",
        [42] = "Wrong Wiegand format",
        [53] = "Password validity expired",
        [54] = "Battery voltage too low",
        [58] = "Normal-open alarm",
        [61] = "Repeat verification",
        [62] = "Prohibited user",
        [63] = "Door locked",
        [73] = "Invalid QR code",
        [74] = "QR code expired",
        [100] = "Tamper alarm",
        [101] = "Duress password open",
        [102] = "Door opened accidentally (forced)",
        [103] = "Duress fingerprint open",
        [104] = "Invalid card punch alarm",
        [105] = "Network disconnected",
        [106] = "Battery power failure",
        [107] = "Mains power failure",
        [109] = "Reader disassembly alarm",
        [110] = "Reader offline",
        [112] = "Extension board offline",
        [200] = "Door opened",
        [201] = "Door closed",
        [202] = "Exit button open",
        [203] = "Multi-card open",
        [204] = "Normal-open period ended",
        [205] = "Remote normal open enabled",
        [206] = "Device started",
        [207] = "Password open",
        [208] = "Super user open",
        [209] = "Exit button (locked)",
        [214] = "Connected to server",
        [220] = "Aux input disconnected",
        [221] = "Aux input shorted",
        [225] = "Aux input normal",
        [226] = "Aux input triggered",
        [232] = "Verification passed",
        [233] = "Remote lock",
        [234] = "Remote unlock",
        [237] = "Reader online",
        [247] = "Expansion board online",
        [255] = "Door status update",
        [4008] = "Mains power recovered",
        [5021] = "QR code invalid",
        [6005] = "Record storage 90% full",
        [6006] = "Reader short circuit (RS485)",
        [6007] = "Reader short circuit (Wiegand)",
    };

    public static string Describe(int code)
    {
        if (Names.TryGetValue(code, out var name))
        {
            return name;
        }

        return code switch
        {
            >= 4000 and < 5000 => "Extended normal event",
            >= 5000 and < 6000 => "Extended abnormal event",
            >= 6000 and < 7000 => "Extended warning event",
            >= 20 and < 100 => "Error event",
            >= 100 and < 200 => "Alarm event",
            _ => "Event"
        };
    }
}
