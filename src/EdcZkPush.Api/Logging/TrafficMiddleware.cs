using System.Diagnostics;
using System.Text;

namespace EdcZkPush.Logging;

/// <summary>Records every request under /iclock (the device side) with full request and response bodies.</summary>
public sealed class TrafficMiddleware
{
    private static readonly string[] InterestingHeaders =
        { "User-Agent", "Cookie", "Content-Type", "Content-Length", "Accept", "Accept-Language", "Connection", "Transfer-Encoding", "Host" };

    private readonly RequestDelegate _next;
    private readonly TrafficLog _log;

    public TrafficMiddleware(RequestDelegate next, TrafficLog log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/iclock", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var sw = Stopwatch.StartNew();
        ctx.Request.EnableBuffering();
        byte[] requestBytes;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms);
            requestBytes = ms.ToArray();
        }

        ctx.Request.Body.Position = 0;

        var notes = new List<string>();
        ctx.Items[ZkItems.Notes] = notes;

        var originalBody = ctx.Response.Body;
        using var captured = new MemoryStream();
        ctx.Response.Body = captured;

        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            notes.Add($"SERVER EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 500;
            }
        }
        finally
        {
            ctx.Response.Body = originalBody;
        }

        var responseBytes = captured.ToArray();
        if (ctx.Response.ContentLength == null)
        {
            // The device firmware does not like chunked transfer encoding: always send Content-Length.
            ctx.Response.ContentLength = responseBytes.Length;
        }

        await originalBody.WriteAsync(responseBytes);
        sw.Stop();

        var headers = new Dictionary<string, string>();
        foreach (var name in InterestingHeaders)
        {
            if (ctx.Request.Headers.TryGetValue(name, out var v))
            {
                headers[name] = v.ToString();
            }
        }

        // Any header we did not expect is interesting when comparing firmware versions.
        foreach (var h in ctx.Request.Headers.Where(h => !InterestingHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)))
        {
            headers[h.Key] = h.Value.ToString();
        }

        var record = new TrafficRecord
        {
            RemoteIp = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString(),
            Method = ctx.Request.Method,
            PathAndQuery = ctx.Request.Path + ctx.Request.QueryString,
            Sn = ctx.Request.Query["SN"].FirstOrDefault(),
            Headers = headers,
            RequestBody = Encoding.UTF8.GetString(requestBytes),
            RequestBytes = requestBytes.Length,
            Status = ctx.Response.StatusCode,
            ResponseBody = Encoding.UTF8.GetString(responseBytes),
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            Kind = ctx.Items.TryGetValue(ZkItems.Kind, out var k) && k is TrafficKind kind ? kind : TrafficKind.Unknown
        };
        record.Notes.AddRange(notes);

        var changed = ctx.Items.TryGetValue(ZkItems.RtStateChanged, out var c) && c is true;
        _log.Write(record, changed);
    }
}

public static class ZkItems
{
    public const string Notes = "zk.notes";
    public const string Kind = "zk.kind";
    public const string RtStateChanged = "zk.rtchanged";

    public static void Note(this HttpContext ctx, string note)
    {
        if (ctx.Items.TryGetValue(Notes, out var n) && n is List<string> list)
        {
            list.Add(note);
        }
    }

    public static void SetKind(this HttpContext ctx, TrafficKind kind) => ctx.Items[Kind] = kind;
}
