using System.Globalization;

namespace EdcZkPush.Protocol;

/// <summary>
/// ZKTeco date/time helpers (Security PUSH protocol, Appendix 5 and 6).
/// </summary>
public static class ZkTime
{
    /// <summary>Largest value the controller can store (unsigned 32-bit) → 2133-06-?? .</summary>
    public const long MaxEncodedValue = uint.MaxValue;

    /// <summary>
    /// Appendix 5 "OldEncodeTime": ((year-2000)*12*31 + (mon-1)*31 + day-1) * 86400 + (hour*60+min)*60 + sec.
    /// The device stores the result as an unsigned 32-bit integer, so dates after ~2133 overflow.
    /// </summary>
    public static long Encode(DateTime value)
    {
        if (value.Year < 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "ZKTeco time encoding starts at the year 2000.");
        }

        long encoded =
            ((value.Year - 2000) * 12L * 31L + (value.Month - 1) * 31L + (value.Day - 1)) * 86400L
            + (value.Hour * 60L + value.Minute) * 60L
            + value.Second;

        if (encoded > MaxEncodedValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"{value:yyyy-MM-dd} does not fit in the controller's 32-bit time field (max ≈ year 2133). " +
                "Use a closer end date, or 0 for 'no limit'.");
        }

        return encoded;
    }

    /// <summary>Appendix 6 "OldDecodeTime".</summary>
    public static DateTime Decode(long tt)
    {
        var sec = (int)(tt % 60); tt /= 60;
        var min = (int)(tt % 60); tt /= 60;
        var hour = (int)(tt % 24); tt /= 24;
        var day = (int)(tt % 31) + 1; tt /= 31;
        var mon = (int)(tt % 12) + 1; tt /= 12;
        var year = (int)tt + 2000;

        // The 31-days-per-month encoding allows "impossible" dates (e.g. 31 Feb); clamp for display.
        day = Math.Min(day, DateTime.DaysInMonth(year, mon));
        return new DateTime(year, mon, day, hour, min, sec, DateTimeKind.Unspecified);
    }

    /// <summary>YYYYMMDD format used when the device reports DateFmtFunOn=0.</summary>
    public static string ToYyyyMmDd(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>"+0200" style offset used in the rtdata ServerTZ field.</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{sign}{offset.Hours:00}{offset.Minutes:00}";
    }

    /// <summary>
    /// Time-rule segment: HHMM start shifted left by 16 bits plus HHMM end (e.g. 08:30-12:00 → 830&lt;&lt;16 + 1200).
    /// </summary>
    public static int EncodeSegment(string range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return 0;
        }

        var parts = range.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Time range '{range}' must look like HH:mm-HH:mm.");
        }

        var start = ParseHhmm(parts[0], range);
        var end = ParseHhmm(parts[1], range);

        if (end < start)
        {
            throw new ArgumentException($"Time range '{range}' ends before it starts. Split ranges that cross midnight into two.");
        }

        return (start << 16) + end;
    }

    public static string DecodeSegment(long value)
    {
        if (value == 0)
        {
            return "-";
        }

        var start = (int)(value >> 16);
        var end = (int)(value & 0xFFFF);
        return $"{start / 100:00}:{start % 100:00}-{end / 100:00}:{end % 100:00}";
    }

    private static int ParseHhmm(string text, string range)
    {
        if (!TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var ts) ||
            ts.TotalMinutes >= 24 * 60)
        {
            throw new ArgumentException($"'{text}' in '{range}' is not a valid HH:mm time (00:00 to 23:59).");
        }

        return ts.Hours * 100 + ts.Minutes;
    }
}
