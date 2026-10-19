namespace EdcZkPush.Protocol;

/// <summary>Bound from the "Push" section of appsettings.json.</summary>
public sealed class PushOptions
{
    public string ServerName { get; set; } = "EDC-Test";
    public string ServerVersion { get; set; } = "3.0.1";

    /// <summary>
    /// Protocol version we claim to implement. The device uses min(its version, this).
    /// 3.1.1 and above means "communication encryption supported" (/iclock/exchange) — we do NOT implement it,
    /// so keep this at 3.0.1 to force plain-text communication.
    /// </summary>
    public string PushProtVer { get; set; } = "3.0.1";

    /// <summary>Also send the legacy "PushVersion=" line in /iclock/push (the old server sent PushVersion=3.2.3). Empty = don't send.</summary>
    public string LegacyPushVersionLine { get; set; } = "";

    /// <summary>When true and the SN registered before, answer the init GET with "registry=ok ..." instead of plain "OK".</summary>
    public bool AnswerRegistryOkForKnownDevices { get; set; } = false;

    public int ErrorDelay { get; set; } = 30;

    /// <summary>Seconds between /iclock/getrequest polls (the "heartbeat").</summary>
    public int RequestDelay { get; set; } = 3;

    public string TransTimes { get; set; } = "00:00;14:00";
    public int TransInterval { get; set; } = 1;
    public string TransTables { get; set; } = "User Transaction";
    public int Realtime { get; set; } = 1;
    public int TimeoutSec { get; set; } = 10;

    /// <summary>Reply SvrNSLevel = the DevNSLevel the device registered with (they must match to connect).</summary>
    public bool EchoNsLevel { get; set; } = true;

    /// <summary>"auto" (follow the device's CmdFormat), "0" (plain C:ID:CMD) or "1" (DataType=1,... prefix).</summary>
    public string CmdFormat { get; set; } = "auto";

    /// <summary>How many queued commands to hand out per getrequest (records separated by LF).</summary>
    public int MaxCommandsPerPoll { get; set; } = 1;

    /// <summary>If a command was sent but no /devicecmd result arrived after this many seconds, send it again. 0 = never.</summary>
    public int ResendUnansweredAfterSeconds { get; set; } = 0;

    /// <summary>HTTP status for /iclock/exchange (encryption key exchange, not implemented).</summary>
    public int ExchangeResponseStatus { get; set; } = 404;

    /// <summary>Time zone of the site (IANA or Windows id).</summary>
    public string TimeZone { get; set; } = "Europe/Ljubljana";

    /// <summary>rtdata?type=time answer: "gmt" (per protocol doc) or "local".</summary>
    public string RtDataTimeMode { get; set; } = "gmt";

    /// <summary>Send "Connection: close" like the old server did.</summary>
    public bool ConnectionClose { get; set; } = true;

    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local;
        }
    }
}
