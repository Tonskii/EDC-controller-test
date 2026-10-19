using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EdcZkPush.Controllers;

/// <summary>What every command endpoint returns.</summary>
public sealed class QueueResult
{
    /// <summary>False when preview=true (nothing was queued).</summary>
    public bool Queued { get; set; }
    public string SerialNumber { get; set; } = "";
    public List<QueuedCommand> Commands { get; set; } = new();
    /// <summary>Decisions taken from the device capabilities (date format, card table, DevID, ...).</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class QueuedCommand
{
    public int? CommandId { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Exactly what the device receives, with [TAB]/[LF] made visible.</summary>
    public string Wire { get; set; } = "";
    /// <summary>GET this to see Sent / Succeeded / Failed and the return code.</summary>
    public string? StatusUrl { get; set; }
}

/// <summary>Shared helpers for the command endpoints.</summary>
public abstract class ApiBase : ControllerBase
{
    protected ApiBase(DeviceStore devices, CommandQueue queue, IOptions<PushOptions> options)
    {
        Devices = devices;
        Queue = queue;
        Options = options.Value;
    }

    protected DeviceStore Devices { get; }
    protected CommandQueue Queue { get; }
    protected PushOptions Options { get; }

    /// <summary>
    /// Resolves the target device. If sn is empty and exactly one device has connected, that one is used.
    /// When previewing, an unknown SN is allowed (commands are built with default capabilities).
    /// </summary>
    protected (string Sn, DeviceCapabilities Caps, IActionResult? Error) Target(string? sn, bool preview)
    {
        if (string.IsNullOrWhiteSpace(sn))
        {
            var all = Devices.All();
            if (all.Count == 1)
            {
                var only = all[0];
                return (only.SerialNumber, only.Capabilities ?? Empty, null);
            }

            if (preview)
            {
                return ("PREVIEW", Empty, null);
            }

            return ("", Empty, BadRequest(new
            {
                error = all.Count == 0
                    ? "No controller has connected yet. Pass sn=... explicitly, or use preview=true to just see the command."
                    : "More than one controller is connected; pass sn=...",
                devices = all.Select(d => d.SerialNumber)
            }));
        }

        var session = Devices.Get(sn);
        if (session == null && !preview)
        {
            // Allow queueing before the device connects: it will pick the commands up after the handshake.
            session = Devices.Touch(sn, null);
        }

        return (sn.Trim(), session?.Capabilities ?? Empty, null);
    }

    protected static readonly DeviceCapabilities Empty = new(new Dictionary<string, string>());

    protected QueueResult Enqueue(string sn, bool preview, IEnumerable<(string Label, string Text)> commands, IEnumerable<string>? notes = null)
    {
        var result = new QueueResult { Queued = !preview, SerialNumber = sn };
        if (notes != null)
        {
            result.Notes.AddRange(notes);
        }

        foreach (var (label, text) in commands)
        {
            if (preview)
            {
                result.Commands.Add(new QueuedCommand
                {
                    Label = label,
                    Wire = $"C:<id>:{text}".Replace("\t", "[TAB]").Replace("\n", "[LF]")
                });
                continue;
            }

            var cmd = Queue.Enqueue(sn, label, text);
            result.Commands.Add(new QueuedCommand
            {
                CommandId = cmd.Id,
                Label = label,
                Wire = cmd.WireVisible,
                StatusUrl = $"/api/commands/{cmd.Id}"
            });
        }

        if (!preview && Devices.Get(sn)?.ConfigDownloadedAt == null)
        {
            result.Notes.Add($"Device {sn} has not completed the handshake yet — commands wait in the queue until it polls /iclock/getrequest.");
        }

        return result;
    }

    protected IActionResult Done(string sn, bool preview, IEnumerable<(string Label, string Text)> commands, IEnumerable<string>? notes = null)
    {
        try
        {
            return Ok(Enqueue(sn, preview, commands.ToList(), notes));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ───── capability-driven formatting ─────

    /// <summary>User / access validity date in the format the device asked for (DateFmtFunOn).</summary>
    protected static string ValidityValue(DateTime? value, DeviceCapabilities caps, DateFormatMode mode, List<string> notes, string field)
    {
        if (value == null)
        {
            return "0";
        }

        var useYmd = mode == DateFormatMode.Yyyymmdd || (mode == DateFormatMode.Auto && caps.DateFmtFunOn == 0);
        if (useYmd)
        {
            notes.Add($"{field}: YYYYMMDD format.");
            return ZkTime.ToYyyyMmDd(value.Value);
        }

        var encoded = ZkTime.Encode(value.Value);
        notes.Add($"{field}: {value:yyyy-MM-dd HH:mm:ss} → {encoded} (Appendix 5 seconds).");
        return encoded.ToString();
    }

    protected static void ValidatePin(string pin, DeviceCapabilities caps)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            throw new ArgumentException("PIN (user ID) is required.");
        }

        if (caps.StringPinFunOn != 1 && !pin.All(char.IsDigit))
        {
            throw new ArgumentException($"PIN '{pin}' must be digits only on this device (StringPinFunOn != 1).");
        }

        if (pin.Length > 14)
        {
            throw new ArgumentException($"PIN '{pin}' is longer than 14 characters (C3 Plus limit).");
        }
    }

    protected static int DoorMask(IReadOnlyCollection<int>? doors, int? mask, DeviceCapabilities caps)
    {
        var lockCount = caps.LockCount ?? 4;
        var max = (1 << lockCount) - 1;
        int value;
        if (doors is { Count: > 0 })
        {
            value = 0;
            foreach (var d in doors)
            {
                if (d < 1 || d > lockCount)
                {
                    throw new ArgumentException($"Door {d} does not exist (device has {lockCount} doors).");
                }

                value |= 1 << (d - 1);
            }
        }
        else
        {
            value = mask ?? max;
        }

        if (value < 1 || value > max)
        {
            throw new ArgumentException($"Door mask {value} is outside 1..{max} for a {lockCount}-door controller.");
        }

        return value;
    }

    protected static bool CardsInMulCardUser(DeviceCapabilities caps, CardTarget target) => target switch
    {
        CardTarget.MulCardUser => true,
        CardTarget.UserTable => false,
        _ => caps.CardsInMulCardUser ?? true
    };
}

public enum DateFormatMode
{
    /// <summary>Follow DateFmtFunOn from the registry (seconds when unknown).</summary>
    Auto,
    Seconds,
    Yyyymmdd
}

public enum CardTarget
{
    /// <summary>mulcarduser when the device supports multiple cards per user, otherwise user.CardNo.</summary>
    Auto,
    MulCardUser,
    UserTable
}

public enum IncludeOption
{
    Auto,
    Yes,
    No
}
