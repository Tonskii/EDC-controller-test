using System.Collections.Concurrent;
using System.Text;
using EdcZkPush.Protocol;

namespace EdcZkPush.Logging;

/// <summary>How a request is classified for log filtering.</summary>
public enum TrafficKind
{
    Other,
    /// <summary>ping, or getrequest that returned no command.</summary>
    Heartbeat,
    /// <summary>cdata table=rtstate (door/relay/alarm status, sent every few seconds).</summary>
    RtState,
    /// <summary>cdata table=rtlog (access events).</summary>
    RtLog,
    Handshake,
    CommandDelivery,
    CommandResult,
    Unknown
}

/// <summary>Runtime-switchable log filters (appsettings "TrafficLog", the /api/logging endpoint, or console hotkeys).</summary>
public sealed class TrafficLogOptions
{
    /// <summary>Print every ping / empty getrequest. When false a one-line summary is printed every <see cref="HeartbeatSummarySeconds"/>.</summary>
    public bool ShowHeartbeat { get; set; } = false;

    /// <summary>"all" = every rtstate post, "changes" = only when door/relay/alarm state changed, "none".</summary>
    public string RtState { get; set; } = "changes";

    public bool ShowHeaders { get; set; } = true;
    public bool ShowBodies { get; set; } = true;
    public int MaxBodyChars { get; set; } = 6000;
    public int HeartbeatSummarySeconds { get; set; } = 60;

    /// <summary>Folder for the full, unfiltered traffic log. Empty = no file.</summary>
    public string FileFolder { get; set; } = "logs";
}

public sealed class TrafficRecord
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public string? RemoteIp { get; init; }
    public string Method { get; init; } = "";
    public string PathAndQuery { get; init; } = "";
    public string? Sn { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public string RequestBody { get; init; } = "";
    public int RequestBytes { get; init; }
    public int Status { get; set; }
    public string ResponseBody { get; set; } = "";
    public double ElapsedMs { get; set; }
    public TrafficKind Kind { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>Pretty console output + full file log + in-memory ring buffer of device traffic.</summary>
public sealed class TrafficLog
{
    private readonly object _consoleLock = new();
    private readonly ConcurrentQueue<TrafficRecord> _recent = new();
    private readonly DeviceStore _devices;
    private DateTimeOffset _lastSummary = DateTimeOffset.Now;
    private long _suppressedHeartbeats;
    private long _suppressedRtStates;

    public TrafficLog(TrafficLogOptions options, DeviceStore devices)
    {
        Options = options;
        _devices = devices;
    }

    public TrafficLogOptions Options { get; }

    public IReadOnlyList<TrafficRecord> Recent(string? sn, int take) =>
        _recent.Reverse()
            .Where(r => sn == null || string.Equals(r.Sn, sn, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .ToList();

    public void Write(TrafficRecord rec, bool rtStateChanged)
    {
        _recent.Enqueue(rec);
        while (_recent.Count > 1000 && _recent.TryDequeue(out _))
        {
        }

        var text = Format(rec);
        WriteFile(text);

        var show = rec.Kind switch
        {
            TrafficKind.Heartbeat => Options.ShowHeartbeat,
            TrafficKind.RtState => Options.RtState.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                   (Options.RtState.Equals("changes", StringComparison.OrdinalIgnoreCase) && rtStateChanged),
            _ => true
        };

        if (!show)
        {
            if (rec.Kind == TrafficKind.Heartbeat) Interlocked.Increment(ref _suppressedHeartbeats);
            if (rec.Kind == TrafficKind.RtState) Interlocked.Increment(ref _suppressedRtStates);
            MaybeSummary();
            return;
        }

        lock (_consoleLock)
        {
            WriteColored(text, rec);
        }
    }

    /// <summary>Free-form line (startup banners, hotkey feedback, warnings).</summary>
    public void Info(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}";
        WriteFile(line + Environment.NewLine);
        lock (_consoleLock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = old;
        }
    }

    private void MaybeSummary()
    {
        if (Options.HeartbeatSummarySeconds <= 0 ||
            (DateTimeOffset.Now - _lastSummary).TotalSeconds < Options.HeartbeatSummarySeconds)
        {
            return;
        }

        _lastSummary = DateTimeOffset.Now;
        var hb = Interlocked.Exchange(ref _suppressedHeartbeats, 0);
        var rs = Interlocked.Exchange(ref _suppressedRtStates, 0);
        var devices = string.Join(", ", _devices.All().Select(d => $"{d.SerialNumber}@{d.RemoteIp} (last {d.LastSeen:HH:mm:ss})"));
        Info($"♥ alive: {hb} heartbeats and {rs} unchanged rtstate posts hidden in the last {Options.HeartbeatSummarySeconds}s — devices: {devices}  [press H / S to show them]",
            ConsoleColor.DarkGray);
    }

    private string Format(TrafficRecord rec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"──── {rec.At:HH:mm:ss.fff}  {rec.Kind}  from {rec.RemoteIp}  {rec.Method} {rec.PathAndQuery}");

        if (Options.ShowHeaders && rec.Headers.Count > 0)
        {
            sb.AppendLine("  headers : " + string.Join(" | ", rec.Headers.Select(h => $"{h.Key}: {h.Value}")));
        }

        if (Options.ShowBodies && rec.RequestBytes > 0)
        {
            sb.AppendLine($"  request : {rec.RequestBytes} bytes");
            foreach (var line in Visible(rec.RequestBody, Options.MaxBodyChars, splitRegistry: rec.PathAndQuery.Contains("/registry", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("    > " + line);
            }
        }

        sb.AppendLine($"  response: HTTP {rec.Status} in {rec.ElapsedMs:0} ms");
        if (Options.ShowBodies)
        {
            foreach (var line in Visible(rec.ResponseBody, Options.MaxBodyChars, splitRegistry: false))
            {
                sb.AppendLine("    < " + line);
            }
        }

        foreach (var note in rec.Notes)
        {
            sb.AppendLine("  * " + note);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Visible(string body, int max, bool splitRegistry)
    {
        if (string.IsNullOrEmpty(body))
        {
            yield return "(empty)";
            yield break;
        }

        var text = body.Length > max ? body[..max] + $" …[{body.Length - max} more chars]" : body;
        if (splitRegistry)
        {
            // Registry body is one long comma separated line: show one key per line.
            text = text.Replace(",", "\n");
        }

        foreach (var line in text.Split('\n'))
        {
            yield return line.Replace("\r", "[CR]").Replace("\t", " ⇥ ");
        }
    }

    private void WriteColored(string text, TrafficRecord rec)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = rec.Kind switch
        {
            TrafficKind.Handshake => ConsoleColor.Cyan,
            TrafficKind.CommandDelivery => ConsoleColor.Yellow,
            TrafficKind.CommandResult => rec.Notes.Any(n => n.Contains("FAILED")) ? ConsoleColor.Red : ConsoleColor.Green,
            TrafficKind.RtLog => ConsoleColor.Magenta,
            TrafficKind.RtState => ConsoleColor.DarkCyan,
            TrafficKind.Heartbeat => ConsoleColor.DarkGray,
            TrafficKind.Unknown => ConsoleColor.Red,
            _ => ConsoleColor.Gray
        };
        Console.Write(text);
        Console.ForegroundColor = old;
    }

    private void WriteFile(string text)
    {
        if (string.IsNullOrWhiteSpace(Options.FileFolder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Options.FileFolder);
            var path = Path.Combine(Options.FileFolder, $"push-traffic-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_consoleLock)
            {
                File.AppendAllText(path, text);
            }
        }
        catch (IOException)
        {
            // Logging must never break the device conversation.
        }
    }
}
