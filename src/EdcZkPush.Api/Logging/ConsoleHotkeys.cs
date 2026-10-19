namespace EdcZkPush.Logging;

/// <summary>
/// Lets you flip log filters while the server runs: H = heartbeats, S = rtstate (all/changes/none),
/// B = bodies, D = headers, ? = help. Only active when the app runs in an interactive console.
/// </summary>
public sealed class ConsoleHotkeys : BackgroundService
{
    private readonly TrafficLog _log;

    public ConsoleHotkeys(TrafficLog log)
    {
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        await Task.Delay(1500, stoppingToken);
        PrintHelp();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(100, stoppingToken);
                continue;
            }

            var key = Console.ReadKey(intercept: true).Key;
            var o = _log.Options;
            switch (key)
            {
                case ConsoleKey.H:
                    o.ShowHeartbeat = !o.ShowHeartbeat;
                    _log.Info($"[hotkey] heartbeat (ping / empty getrequest) logging: {(o.ShowHeartbeat ? "ON" : "OFF (summary only)")}", ConsoleColor.White);
                    break;
                case ConsoleKey.S:
                    o.RtState = o.RtState.ToLowerInvariant() switch
                    {
                        "changes" => "all",
                        "all" => "none",
                        _ => "changes"
                    };
                    _log.Info($"[hotkey] rtstate logging: {o.RtState}", ConsoleColor.White);
                    break;
                case ConsoleKey.B:
                    o.ShowBodies = !o.ShowBodies;
                    _log.Info($"[hotkey] request/response bodies: {(o.ShowBodies ? "ON" : "OFF")}", ConsoleColor.White);
                    break;
                case ConsoleKey.D:
                    o.ShowHeaders = !o.ShowHeaders;
                    _log.Info($"[hotkey] headers: {(o.ShowHeaders ? "ON" : "OFF")}", ConsoleColor.White);
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }
    }

    private void PrintHelp()
    {
        var o = _log.Options;
        _log.Info($"Hotkeys: [H] heartbeat={(o.ShowHeartbeat ? "on" : "off")}  [S] rtstate={o.RtState}  [B] bodies={(o.ShowBodies ? "on" : "off")}  [D] headers={(o.ShowHeaders ? "on" : "off")}  [?] help",
            ConsoleColor.White);
    }
}
