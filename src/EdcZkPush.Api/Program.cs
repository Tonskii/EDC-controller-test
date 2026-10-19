using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Serialization;
using EdcZkPush.Logging;
using EdcZkPush.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// The controller is a small embedded HTTP client: be generous with slow uploads and body sizes.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MinRequestBodyDataRate = null;
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    k.AddServerHeader = false;
});

// Keep the console readable: framework noise down, our TrafficLog prints the device conversation.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.Configure<PushOptions>(builder.Configuration.GetSection("Push"));
var trafficOptions = builder.Configuration.GetSection("TrafficLog").Get<TrafficLogOptions>() ?? new TrafficLogOptions();
builder.Services.AddSingleton(trafficOptions);
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<CommandQueue>();
builder.Services.AddSingleton<TrafficLog>();
builder.Services.AddHostedService<ConsoleHotkeys>();

builder.Services
    .AddControllers(o => o.Filters.Add<ArgumentExceptionFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "EDC – ZKTeco C3 Plus PUSH test API",
        Version = "v1",
        Description =
            "Test server for ZKTeco access controllers (C3-400 Plus, C3-200 Pro, inBio) using the **Security PUSH protocol** " +
            "(document 2026-04-22, v7.3, protocol 3.1.2).\n\n" +
            "* Point the controller's *PUSH Server* setting at this machine (default port 8088).\n" +
            "* Every command endpoint queues a command; the controller picks it up on its next `/iclock/getrequest` poll.\n" +
            "* Add `preview=true` to any command endpoint to see the exact wire text without queuing it.\n" +
            "* Leave `sn` empty when only one controller is connected.\n" +
            "* Follow a command with `GET /api/commands/{id}?waitSeconds=10`."
    });

    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
    {
        c.IncludeXmlComments(xml);
    }

    c.TagActionsBy(api =>
    {
        var tag = api.ActionDescriptor.EndpointMetadata.OfType<TagsAttribute>().FirstOrDefault()?.Tags.FirstOrDefault();
        return new[] { tag ?? api.ActionDescriptor.RouteValues["controller"] ?? "Other" };
    });
    c.OrderActionsBy(api => api.ActionDescriptor.EndpointMetadata.OfType<TagsAttribute>().FirstOrDefault()?.Tags.FirstOrDefault() + api.RelativePath);
});

var app = builder.Build();

app.UseMiddleware<TrafficMiddleware>();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ZKTeco PUSH test API");
    c.DocumentTitle = "EDC ZKTeco PUSH test";
    c.DisplayRequestDuration();
});
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<TrafficLog>();
    var opt = app.Services.GetRequiredService<IOptions<PushOptions>>().Value;
    var urls = app.Urls.ToList();
    var port = urls.Select(u => new Uri(u.Replace("*", "localhost").Replace("+", "localhost").Replace("0.0.0.0", "localhost")).Port).FirstOrDefault();

    log.Info("══════════════════════════════════════════════════════════════════════", ConsoleColor.Cyan);
    log.Info(" EDC ZKTeco PUSH test server (Security PUSH / access control)", ConsoleColor.Cyan);
    log.Info($" Listening on: {string.Join(", ", urls)}", ConsoleColor.Cyan);
    foreach (var ip in LocalIPv4())
    {
        log.Info($"   → set the controller's PUSH server to  {ip}  port {port}   (Swagger: http://{ip}:{port}/swagger)", ConsoleColor.Cyan);
    }

    log.Info($" PushProtVer={opt.PushProtVer}  RequestDelay={opt.RequestDelay}s  CmdFormat={opt.CmdFormat}  TimeZone={opt.TimeZone}", ConsoleColor.Cyan);
    log.Info($" Full unfiltered traffic is written to ./{trafficOptions.FileFolder}/push-traffic-<date>.log", ConsoleColor.Cyan);
    log.Info("══════════════════════════════════════════════════════════════════════", ConsoleColor.Cyan);
});

app.Run();

static IEnumerable<string> LocalIPv4() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString())
        .Distinct();

/// <summary>Validation problems inside command builders come back as HTTP 400 with the message.</summary>
public sealed class ArgumentExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is ArgumentException ex)
        {
            context.Result = new BadRequestObjectResult(new { error = ex.Message });
            context.ExceptionHandled = true;
        }
    }
}


