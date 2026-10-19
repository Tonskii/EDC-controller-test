using System.Collections.Concurrent;

namespace EdcZkPush.Protocol;

public enum CommandStatus
{
    Queued,
    Sent,
    Succeeded,
    Failed,
    /// <summary>Return=-5000: accepted by a master controller, waiting for a sub-controller.</summary>
    AcceptedPending,
    Cancelled
}

public sealed class DeviceCommand
{
    public required int Id { get; init; }
    public required string SerialNumber { get; init; }

    /// <summary>Short label, e.g. "Add user", shown in logs.</summary>
    public required string Label { get; init; }

    /// <summary>Command body without the "C:ID:" prefix, e.g. "DATA UPDATE user Pin=1\tName=Ana".</summary>
    public required string Text { get; init; }

    public CommandStatus Status { get; set; } = CommandStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? SentAt { get; set; }
    public int SendCount { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ReturnCode { get; set; }
    public string? ReturnMeaning { get; set; }
    public string? ResultRecord { get; set; }

    /// <summary>Data the device uploaded to /iclock/querydata for this command (DATA QUERY / COUNT / GET OPTIONS).</summary>
    public List<string> QueryData { get; } = new();

    /// <summary>The command exactly as it goes on the wire, tabs and line feeds made visible.</summary>
    public string WireVisible => $"C:{Id}:{Text}".Replace("\t", "[TAB]").Replace("\n", "[LF]");
}

/// <summary>Per-device FIFO of commands, delivered on /iclock/getrequest and closed by /iclock/devicecmd.</summary>
public sealed class CommandQueue
{
    private readonly ConcurrentDictionary<int, DeviceCommand> _all = new();
    private readonly object _lock = new();
    private int _nextId = 100;

    public DeviceCommand Enqueue(string sn, string label, string text)
    {
        if (text.Contains('\r'))
        {
            throw new ArgumentException("Commands must not contain carriage returns (CR); use LF between records.");
        }

        var cmd = new DeviceCommand
        {
            Id = Interlocked.Increment(ref _nextId),
            SerialNumber = sn.Trim(),
            Label = label,
            Text = text
        };
        _all[cmd.Id] = cmd;
        return cmd;
    }

    /// <summary>Next commands to hand to the device. Marks them as Sent.</summary>
    public IReadOnlyList<DeviceCommand> TakeForDelivery(string sn, int max, int resendAfterSeconds)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.Now;
            var candidates = _all.Values
                .Where(c => string.Equals(c.SerialNumber, sn, StringComparison.OrdinalIgnoreCase))
                .Where(c => c.Status == CommandStatus.Queued ||
                            (resendAfterSeconds > 0 && c.Status == CommandStatus.Sent &&
                             c.SentAt.HasValue && (now - c.SentAt.Value).TotalSeconds > resendAfterSeconds))
                .OrderBy(c => c.Id)
                .Take(Math.Max(1, max))
                .ToList();

            foreach (var c in candidates)
            {
                c.Status = CommandStatus.Sent;
                c.SentAt = now;
                c.SendCount++;
            }

            return candidates;
        }
    }

    public DeviceCommand? Get(int id) => _all.TryGetValue(id, out var c) ? c : null;

    public IReadOnlyList<DeviceCommand> ForDevice(string? sn, bool onlyOpen = false) =>
        _all.Values
            .Where(c => sn == null || string.Equals(c.SerialNumber, sn, StringComparison.OrdinalIgnoreCase))
            .Where(c => !onlyOpen || c.Status is CommandStatus.Queued or CommandStatus.Sent or CommandStatus.AcceptedPending)
            .OrderBy(c => c.Id)
            .ToList();

    public int CancelOpen(string sn)
    {
        var n = 0;
        foreach (var c in ForDevice(sn, onlyOpen: true).Where(c => c.Status == CommandStatus.Queued))
        {
            c.Status = CommandStatus.Cancelled;
            n++;
        }

        return n;
    }

    public DeviceCommand? Complete(int id, int returnCode, string record)
    {
        var cmd = Get(id);
        if (cmd == null)
        {
            return null;
        }

        cmd.ReturnCode = returnCode;
        cmd.ReturnMeaning = ReturnCodes.Describe(returnCode);
        cmd.ResultRecord = record;
        cmd.CompletedAt = DateTimeOffset.Now;
        cmd.Status = returnCode switch
        {
            >= 0 => CommandStatus.Succeeded,
            -5000 => CommandStatus.AcceptedPending,
            _ => CommandStatus.Failed
        };
        return cmd;
    }
}
