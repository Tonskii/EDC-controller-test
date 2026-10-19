using System.Globalization;

namespace EdcZkPush.Protocol;

/// <summary>
/// Builds command bodies for the access-control "Security PUSH" protocol (the one the C3 family uses).
/// Every method returns the text that goes after "C:{id}:" in the getrequest response.
/// </summary>
public static class SecurityCommands
{
    public const string Tab = "\t";
    public const string Lf = "\n";

    public static string Fields(params (string Key, object? Value)[] fields) =>
        string.Join(Tab, fields.Select(f => $"{f.Key}={Clean(f.Value)}"));

    public static string Update(string table, IEnumerable<string> records) =>
        $"DATA UPDATE {table} {string.Join(Lf, records)}";

    public static string Delete(string table, IEnumerable<string> conditions) =>
        $"DATA DELETE {table} {string.Join(Lf, conditions)}";

    /// <summary>DATA UPDATE user — field order as in the protocol document.</summary>
    public static string UserRecord(string pin, string? name, string? password, int group, string startTime, string endTime,
        string? cardNo, int? privilege)
    {
        var fields = new List<(string, object?)>();
        if (cardNo != null)
        {
            fields.Add(("CardNo", cardNo));
        }

        fields.Add(("Pin", pin));
        fields.Add(("Password", password ?? ""));
        fields.Add(("Group", group));
        fields.Add(("StartTime", startTime));
        fields.Add(("EndTime", endTime));
        fields.Add(("Name", name ?? ""));
        if (privilege.HasValue)
        {
            fields.Add(("Privilege", privilege.Value));
        }

        return Fields(fields.ToArray());
    }

    /// <summary>DATA UPDATE mulcarduser — one row per card.</summary>
    public static string CardRecord(string pin, string cardNo, int lossCardFlag, int cardType, int? cardBit, string? siteCode)
    {
        var fields = new List<(string, object?)>
        {
            ("Pin", pin), ("CardNo", cardNo), ("LossCardFlag", lossCardFlag), ("CardType", cardType)
        };

        // CardBit / SiteCode / IMIE only exist with WGRuleVer=2.0.
        if (cardBit.HasValue)
        {
            fields.Add(("CardBit", cardBit.Value));
        }

        if (!string.IsNullOrEmpty(siteCode))
        {
            fields.Add(("SiteCode", siteCode));
        }

        return Fields(fields.ToArray());
    }

    /// <summary>DATA UPDATE userauthorize — door access + which time rule applies.</summary>
    public static string AccessRecord(string pin, int timezoneId, int doorMask, int? devId, long? start, long? end)
    {
        var fields = new List<(string, object?)>
        {
            ("Pin", pin), ("AuthorizeTimezoneId", timezoneId), ("AuthorizeDoorId", doorMask)
        };
        if (devId.HasValue)
        {
            fields.Add(("DevID", devId.Value));
        }

        if (start.HasValue || end.HasValue)
        {
            fields.Add(("StartTime", start ?? 0));
            fields.Add(("EndTime", end ?? 0));
        }

        return Fields(fields.ToArray());
    }

    public static readonly string[] TimezoneSlots =
    {
        "SunTime1", "SunTime2", "SunTime3", "MonTime1", "MonTime2", "MonTime3", "TueTime1", "TueTime2", "TueTime3",
        "WedTime1", "WedTime2", "WedTime3", "ThuTime1", "ThuTime2", "ThuTime3", "FriTime1", "FriTime2", "FriTime3",
        "SatTime1", "SatTime2", "SatTime3", "Hol1Time1", "Hol1Time2", "Hol1Time3", "Hol2Time1", "Hol2Time2", "Hol2Time3",
        "Hol3Time1", "Hol3Time2", "Hol3Time3"
    };

    /// <summary>DATA UPDATE timezone — 30 encoded slots (7 days + 3 holiday types, 3 ranges each).</summary>
    public static string TimezoneRecord(int timezoneId, IReadOnlyList<int> slots, long? start, long? end)
    {
        if (slots.Count != TimezoneSlots.Length)
        {
            throw new ArgumentException("A time rule needs exactly 30 slots.");
        }

        var fields = new List<(string, object?)> { ("TimezoneId", timezoneId) };
        fields.AddRange(TimezoneSlots.Select((name, i) => (name, (object?)slots[i])));
        if (start.HasValue || end.HasValue)
        {
            fields.Add(("StartTime", start ?? 0));
            fields.Add(("EndTime", end ?? 0));
        }

        return Fields(fields.ToArray());
    }

    public static string HolidayRecord(DateOnly date, int type, int loop) =>
        Fields(("Holiday", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), ("HolidayType", type), ("Loop", loop));

    public static string Query(string table, string fieldDesc = "*", string filter = "*") =>
        $"DATA QUERY tablename={table},fielddesc={fieldDesc},filter={filter}";

    public static string Count(string table, string condition = "*") => $"DATA COUNT {table} {condition}".TrimEnd();

    /// <summary>CONTROL DEVICE AABBCCDD — each group is a byte printed with %02X (hex!).</summary>
    public static string Control(int aa, int bb, int cc, int dd) =>
        $"CONTROL DEVICE {aa:X2}{bb:X2}{cc:X2}{dd:X2}";

    public static string SetOptions(IEnumerable<KeyValuePair<string, string>> options) =>
        "SET OPTIONS " + string.Join(",", options.Select(kv => $"{kv.Key}={Clean(kv.Value)}"));

    public static string GetOptions(IEnumerable<string> names) => "GET OPTIONS " + string.Join(",", names);

    /// <summary>Card number as the hex string mulcarduser expects (decimal "12345678" or "0xBC614E" in, "bc614e" out).</summary>
    public static string CardToHex(string cardNo)
    {
        var t = cardNo.Trim();
        ulong value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException($"'{cardNo}' is not a valid hex card number.");
            }
        }
        else if (!ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            throw new ArgumentException($"'{cardNo}' is not a valid decimal card number (prefix hex with 0x).");
        }

        return value.ToString("x", CultureInfo.InvariantCulture);
    }

    /// <summary>Card number as decimal (what the user table and the reader display use).</summary>
    public static string CardToDecimal(string cardNo)
    {
        var t = cardNo.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        if (!ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"'{cardNo}' is not a valid decimal card number (prefix hex with 0x).");
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Clean(object? value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (s.IndexOfAny(new[] { '\t', '\n', '\r' }) >= 0)
        {
            throw new ArgumentException($"Value '{s.Replace("\t", "[TAB]").Replace("\n", "[LF]")}' contains a protocol separator (TAB/LF/CR).");
        }

        return s;
    }
}
