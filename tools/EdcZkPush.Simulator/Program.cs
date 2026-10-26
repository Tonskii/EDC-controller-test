// Fake ZKTeco C3-400 Plus speaking the Security PUSH protocol — lets you test the server without hardware.
//
//   dotnet run --project tools/EdcZkPush.Simulator -- --server http://localhost:8088
//   dotnet run --project tools/EdcZkPush.Simulator -- --server http://localhost:8088 --selftest
//
// Plain mode: handshake, then poll for commands forever, answer them with Return=0, send door states and an event.
// --selftest: additionally calls the management API, checks every command reaches the device in the right format
//             and finishes with Succeeded. Exit code 0 = all good (used by the GitHub Actions build).

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var server = Arg("--server") ?? "http://localhost:8088";
var sn = Arg("--sn") ?? "SIMC3400PLUS01";
var selfTest = args.Contains("--selftest");
var http = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(30) };
var failures = new List<string>();
var seenWire = new List<string>();
string? token = null;

void Check(bool ok, string what)
{
    if (!ok && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
    {
        Console.WriteLine($"::error title=Self test::{what}");
    }

    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    Console.ResetColor();
    if (!ok) failures.Add(what);
}

async Task<string> Device(HttpMethod method, string path, string? body = null)
{
    var req = new HttpRequestMessage(method, path);
    req.Headers.TryAddWithoutValidation("User-Agent", "iClock Proxy/1.09");
    if (token != null) req.Headers.TryAddWithoutValidation("Cookie", $"token={token}");
    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/push");
    using var resp = await http.SendAsync(req);
    var text = await resp.Content.ReadAsStringAsync();
    if (resp.Headers.TransferEncodingChunked == true) failures.Add($"{path} answered with chunked encoding");
    return $"{(int)resp.StatusCode}|{text}";
}

// ───── handshake (Security PUSH 4.1 → 4.4 → 4.5) ─────
var init = await Device(HttpMethod.Get, $"/iclock/cdata?SN={sn}&options=all&pushver=3.1.2&DeviceType=acc");
Check(init == "200|OK", $"init GET cdata answers OK (got '{init}')");

var registryBody = string.Join(",",
    "DeviceType=acc", "~DeviceName=C3-400 Plus", "FirmVer=AC Ver 5.7.8.3033 Jan 15 2025", "PushVersion=Ver 3.1.2",
    "MAC=00:17:61:12:34:56", "CommType=ethernet", "MaxPackageSize=2048000", "LockCount=4", "ReaderCount=8",
    "AuxInCount=4", "AuxOutCount=4", "MachineType=2", "~IsOnlyRFMachine=1", "~MaxUserCount=100000",
    "~MaxAttLogCount=500000", "IPAddress=192.168.1.201", "NetMask=255.255.255.0", "GATEIPAddress=0.0.0.0",
    "DateFmtFunOn=1", "DisableUserFunOn=1", "MulCardUserFunOn=1", "StringPinFunOn=0", "UserNameFunOn=1", "CmdFormat=0", "DevNSLevel=0",
    "WGRuleVer=2.0", "RemoteOpenDoorExFunOn=1",
    "AccSupportFunList=" + AccBits(67, 1, 2, 5, 7, 11, 15, 26, 33, 35, 40, 47, 49, 60, 61, 62),
    "MultiStageControlFunOn=0", "MasterControlOn=0", "SubControlOn=0", "IsSupportQRcode=1", "QRCodeEnable=1");
var reg = await Device(HttpMethod.Post, $"/iclock/registry?SN={sn}", registryBody);
var regCode = Regex.Match(reg, @"RegistryCode=(\S+)").Groups[1].Value;
Check(reg.StartsWith("200|RegistryCode=") && regCode.Length > 0, $"registry returns RegistryCode (got '{reg}')");

var push = await Device(HttpMethod.Post, $"/iclock/push?SN={sn}");
var sessionId = Regex.Match(push, @"SessionID=(\S+)").Groups[1].Value;
Check(sessionId.Length > 0, "push config contains SessionID");
Check(push.Contains("PushProtVer=3.0.1"), "push config announces PushProtVer=3.0.1 (plain text, no encryption)");
Check(push.Contains("SvrNSLevel=0"), "push config echoes DevNSLevel as SvrNSLevel");
var delay = int.TryParse(Regex.Match(push, @"RequestDelay=(\d+)").Groups[1].Value, out var d) ? d : 3;
token = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(regCode + sn + sessionId))).ToLowerInvariant();

var time = await Device(HttpMethod.Get, $"/iclock/rtdata?SN={sn}&type=time");
Check(Regex.IsMatch(time, @"^200\|DateTime=\d+,ServerTZ=[+-]\d{4}$"), $"rtdata time answer is encoded seconds (got '{time}')");

var tabledata = await Device(HttpMethod.Post, $"/iclock/cdata?SN={sn}&table=tabledata&tablename=user&count=1",
    "user uid=1\tcardno=\tpin=1\tpassword=\tgroup=1\tstarttime=0\tendtime=0\tname=\tprivilege=0\tdisable=0\tverify=0");
Check(tabledata == "200|user=1", $"tabledata upload answered 'user=1' (got '{tabledata}')");

var rtlog = await Device(HttpMethod.Post, $"/iclock/cdata?SN={sn}&table=rtlog",
    $"time={DateTime.Now:yyyy-MM-dd HH:mm:ss}\tpin=0\tcardno=0\teventaddr=1\tevent=206\tinoutstatus=0\tverifytype=200\tindex=1");
Check(rtlog == "200|OK", "rtlog accepted");
Console.WriteLine($"Handshake done. Polling every {delay}s as {sn}. Ctrl+C to stop.");

// ───── device loop ─────
using var cts = new CancellationTokenSource();
var loop = Task.Run(async () =>
{
    var tick = 0;
    while (!cts.IsCancellationRequested)
    {
        var resp = await Device(HttpMethod.Get, $"/iclock/getrequest?SN={sn}");
        var body = resp[(resp.IndexOf('|') + 1)..];
        if (body != "OK")
        {
            // Commands start with C:<id>: — a multi-record command continues on following lines.
            var commands = Regex.Split(body, @"\n(?=C:\d+:)");
            foreach (var wire in commands)
            {
                lock (seenWire) seenWire.Add(wire);
                Console.WriteLine($"  device got: {wire.Replace("\t", "[TAB]").Replace("\n", "[LF]")}");
                var m = Regex.Match(wire, @"^C:(\d+):(.*)$", RegexOptions.Singleline);
                if (!m.Success) { failures.Add($"malformed command line: {wire}"); continue; }
                var id = m.Groups[1].Value;
                var text = m.Groups[2].Value;
                var ret = 0;
                var cmdName = "DATA";

                if (text.StartsWith("DATA QUERY"))
                {
                    var table = Regex.Match(text, @"tablename=([^,]+)").Groups[1].Value;
                    var rows = table.Equals("DevProperty", StringComparison.OrdinalIgnoreCase)
                        ? $"DevProperty ID=7\tType=0\tBusType=0\tMachineType=10\tAddress=0\tMac=\tIPAddress=127.0.0.1\tSN={sn}\tIsMaster=0"
                        : table == "user"
                        ? "user uid=1\tcardno=\tpin=1001\tpassword=1234\tgroup=0\tstarttime=0\tendtime=0\tname=Ana Novak\tprivilege=0\tdisable=0\tverify=0\nuser uid=2\tcardno=\tpin=1002\tpassword=\tgroup=0\tstarttime=0\tendtime=0\tname=Test\tprivilege=0\tdisable=0\tverify=0"
                        : $"{table} pin=1001";
                    var count = rows.Split('\n').Length;
                    await Device(HttpMethod.Post, $"/iclock/querydata?SN={sn}&type=tabledata&cmdid={id}&tablename={table}&count={count}&packcnt=1&packidx=1", rows);
                    ret = count;
                }
                else if (text.StartsWith("DATA COUNT"))
                {
                    var table = text.Split(' ')[2];
                    await Device(HttpMethod.Post, $"/iclock/querydata?SN={sn}&type=count&cmdid={id}&tablename={table}&count=1&packcnt=1&packidx=1", $"{table}=2");
                }
                else if (text.StartsWith("GET OPTIONS"))
                {
                    cmdName = "GET OPTIONS";
                    await Device(HttpMethod.Post, $"/iclock/querydata?SN={sn}&type=options&cmdid={id}&tablename=options&count=1&packcnt=1&packidx=1",
                        "IPAddress=192.168.1.201,NetMask=255.255.255.0,GATEIPAddress=0.0.0.0");
                }
                else if (text.StartsWith("CONTROL DEVICE"))
                {
                    cmdName = "CONTROL DEVICE";
                    if (!Regex.IsMatch(text, @"^CONTROL DEVICE [0-9A-F]{8}([0-9A-F]{62})?$")) { ret = -620; }
                }
                else if (text.StartsWith("SET OPTIONS")) cmdName = "SET OPTIONS";
                else if (text.StartsWith("REMOTE OPENDOOREX")) cmdName = "REMOTE OPENDOOREX";
                else if (text.StartsWith("DATA UPDATE") || text.StartsWith("DATA DELETE"))
                {
                    if (text.Contains('\r')) ret = -620;
                }
                else ret = -13;

                await Device(HttpMethod.Post, $"/iclock/devicecmd?SN={sn}", $"ID={id}&Return={ret}&CMD={cmdName}");
            }
        }

        if (tick++ % 2 == 0)
        {
            var sensor = tick % 10 == 0 ? "02" : "55";
            await Device(HttpMethod.Post, $"/iclock/cdata?SN={sn}&table=rtstate",
                $"time={DateTime.Now:yyyy-MM-dd HH:mm:ss}\tsensor={sensor}\trelay=00\talarm=0000000000000000");
        }

        await Device(HttpMethod.Get, $"/iclock/ping?SN={sn}");
        try { await Task.Delay(TimeSpan.FromSeconds(selfTest ? 1 : delay), cts.Token); } catch (TaskCanceledException) { }
    }
});

if (!selfTest)
{
    await loop;
    return 0;
}

// ───── self test through the management API ─────
async Task<List<int>> Call(HttpMethod method, string path, object? body, string what)
{
    var req = new HttpRequestMessage(method, path) { Content = body == null ? null : JsonContent.Create(body) };
    using var resp = await http.SendAsync(req);
    var text = await resp.Content.ReadAsStringAsync();
    Check(resp.IsSuccessStatusCode, $"API {what} → HTTP {(int)resp.StatusCode} {(resp.IsSuccessStatusCode ? "" : text)}");
    if (!resp.IsSuccessStatusCode) return new();
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.GetProperty("commands").EnumerateArray()
        .Where(c => c.TryGetProperty("commandId", out _))
        .Select(c => c.GetProperty("commandId").GetInt32()).ToList();
}

// The server reads DevProperty right after the handshake to learn the DevID — wait for it.
var devIdDetected = false;
for (var i = 0; i < 30 && !devIdDetected; i++)
{
    var list = await http.GetStringAsync("/api/devices");
    devIdDetected = list.Contains("controller DevProperty table") && list.Contains("\"devId\":7");
    if (!devIdDetected) await Task.Delay(500);
}
Check(devIdDetected, "DevID 7 auto-detected from DevProperty");

var ids = new List<int>();
ids.AddRange(await Call(HttpMethod.Post, "/api/users", new { pin = "1001", name = "Ana Novak", password = "1234", validFrom = "2026-10-20T00:00:00", validUntil = "2030-12-31T23:59:59", cardNo = "12345678" }, "add user + card"));
ids.AddRange(await Call(HttpMethod.Put, "/api/access", new { pin = "1001", doors = new[] { 1, 2, 3, 4 }, timezoneId = 1 }, "access to 4 doors"));
ids.AddRange(await Call(HttpMethod.Put, "/api/access", new { pin = "1001", doors = new[] { 1, 2, 3, 4 }, timezoneId = 2, includeDevId = "Yes", devId = 1 }, "access with explicit DevID"));
var setDevId = await http.PutAsync($"/api/devices/{sn}/devid?value=190", null);
Check(setDevId.IsSuccessStatusCode, "store DevID 190 for the controller");
ids.AddRange(await Call(HttpMethod.Put, "/api/access", new { pin = "1001", doors = new[] { 1 }, timezoneId = 2, includeDevId = "Yes" }, "access using stored DevID"));
ids.AddRange(await Call(HttpMethod.Put, "/api/access/batch", new { items = new object[] { new { pin = "1001", doors = new[] { 1, 2 }, timezoneId = 2, devId = 1 }, new { pin = "1002", doors = new[] { 3, 4 }, timezoneId = 2, devId = 1 } } }, "access batch with DevID"));
ids.AddRange(await Call(HttpMethod.Put, "/api/timerules", new { timezoneId = 2, weekdays = new[] { "07:00-12:00", "12:30-17:00" }, weekend = new[] { "08:00-12:00" } }, "time rule"));
ids.AddRange(await Call(HttpMethod.Put, "/api/timerules", new { timezoneId = 1, everyDay = new[] { "00:00-23:59" } }, "24h rule 1 like CVSecurity"));
ids.AddRange(await Call(HttpMethod.Put, "/api/holidays", new { date = "2026-12-25", holidayType = 1, loop = 1 }, "holiday"));
ids.AddRange(await Call(HttpMethod.Post, "/api/cards", new { pin = "1001", cardNo = "0xABCDEF" }, "second card"));
ids.AddRange(await Call(HttpMethod.Post, "/api/device/doors/3/open?seconds=15", null, "open door 3"));
ids.AddRange(await Call(HttpMethod.Post, "/api/device/doors/1/open-ex?seconds=5&pin=1001", null, "open door ex"));
ids.AddRange(await Call(HttpMethod.Post, "/api/device/sync-time", null, "sync time"));
ids.AddRange(await Call(HttpMethod.Post, "/api/device/options/get", new { names = new[] { "IPAddress", "NetMask" } }, "get options"));
var queryIds = await Call(HttpMethod.Post, "/api/users/query", null, "query users");
ids.AddRange(queryIds);
ids.AddRange(await Call(HttpMethod.Post, "/api/users/count", null, "count users"));
ids.AddRange(await Call(HttpMethod.Delete, "/api/users/1001/complete", null, "delete user completely"));

// Elevator (same wire format as captured from ZKBio CVSecurity → WSQ2260800005).
// Remove the manual DevID 190 stored above so the auto-detected DevProperty ID (7) is used again.
var clearDevId = await http.PutAsync($"/api/devices/{sn}/devid", null);
Check(clearDevId.IsSuccessStatusCode, "manual DevID override removed");
ids.AddRange(await Call(HttpMethod.Put, "/api/elevator/access", new { pin = "1001", allFloors = true, floorCount = 16, timezoneId = 1 }, "elevator: all 16 floors"));
ids.AddRange(await Call(HttpMethod.Put, "/api/elevator/access", new { pin = "1002", floors = new[] { 8, 9 }, timezoneId = 1 }, "elevator: floors 8+9"));
ids.AddRange(await Call(HttpMethod.Post, "/api/elevator/floors/release", new { floors = new[] { 1 }, seconds = 5 }, "elevator: release floor 1"));
ids.AddRange(await Call(HttpMethod.Post, "/api/elevator/setup", new { floors = 16 }, "elevator: setup 16 floor relays"));
ids.AddRange(await Call(HttpMethod.Delete, "/api/elevator/access/1002", null, "elevator: delete floor access"));

var preview = await http.PostAsJsonAsync("/api/users?preview=true", new { pin = "9", name = "Preview" });
var previewText = await preview.Content.ReadAsStringAsync();
Check(preview.IsSuccessStatusCode && previewText.Contains("C:<id>:DATA UPDATE user"), "preview returns wire text without queuing");

var bad = await http.PutAsJsonAsync("/api/access", new { pin = "1001", doors = new[] { 5 } });
Check((int)bad.StatusCode == 400, "door 5 rejected on a 4-door controller");
var badDate = await http.PostAsJsonAsync("/api/users", new { pin = "77", validUntil = "2226-01-01T00:00:00" });
Check((int)badDate.StatusCode == 400, "year 2226 end date rejected (does not fit the 32-bit device field)");

foreach (var id in ids)
{
    using var r = await http.GetAsync($"/api/commands/{id}?waitSeconds=30");
    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    var status = doc.RootElement.GetProperty("status").GetString();
    var label = doc.RootElement.GetProperty("label").GetString();
    Check(status == "Succeeded", $"command #{id} [{label}] → {status}");
}

if (queryIds.Count > 0)
{
    using var r = await http.GetAsync($"/api/commands/{queryIds[0]}");
    var txt = await r.Content.ReadAsStringAsync();
    Check(txt.Contains("Ana Novak"), "user query rows are attached to the command");
}

lock (seenWire)
{
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE user CardNo=\tPin=1001\tPassword=1234\tGroup=0\tStartTime=861408000\tEndTime=996364799\tName=\tSuperAuthorize=0\tDisable=0")), "user command in ZKBio CVSecurity format (encoded dates)");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE extuser Pin=1001\tFunSwitch=0\tFirstName=Ana Novak\tLastName=")), "name sent in extuser");
    Check(seenWire.Any(w => w.EndsWith("DATA DELETE SuperAuthorize Pin=1001")), "SuperAuthorize cleared");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE mulcarduser Pin=1001\tCardNo=bc614e\tLossCardFlag=0\tCardType=0")), "card 12345678 sent as lowercase hex bc614e");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE userauthorize Pin=1001\tAuthorizeTimezoneId=1\tAuthorizeDoorId=1\tDevID=7\nPin=1001\tAuthorizeTimezoneId=1\tAuthorizeDoorId=2\tDevID=7\nPin=1001\tAuthorizeTimezoneId=1\tAuthorizeDoorId=3\tDevID=7\nPin=1001\tAuthorizeTimezoneId=1\tAuthorizeDoorId=4\tDevID=7")), "one access row per door with auto-detected DevID 7");
    Check(seenWire.Any(w => w.EndsWith("Pin=1001\tAuthorizeTimezoneId=2\tAuthorizeDoorId=4\tDevID=1")), "explicit DevID=1 used");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE userauthorize Pin=1001\tAuthorizeTimezoneId=2\tAuthorizeDoorId=1\tDevID=190")), "stored per-controller DevID 190 used");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE userauthorize Pin=1001\tAuthorizeTimezoneId=2\tAuthorizeDoorId=1\tDevID=1\nPin=1001\tAuthorizeTimezoneId=2\tAuthorizeDoorId=2\tDevID=1\nPin=1002\tAuthorizeTimezoneId=2\tAuthorizeDoorId=3\tDevID=1\nPin=1002\tAuthorizeTimezoneId=2\tAuthorizeDoorId=4\tDevID=1")), "batch access, one row per door");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE timezone TimezoneId=1\tSunTime1=2359\tSunTime2=0\tSunTime3=0\tMonTime1=2359\tMonTime2=0\tMonTime3=0\tTueTime1=2359\tTueTime2=0\tTueTime3=0\tWedTime1=2359\tWedTime2=0\tWedTime3=0\tThuTime1=2359\tThuTime2=0\tThuTime3=0\tFriTime1=2359\tFriTime2=0\tFriTime3=0\tSatTime1=2359\tSatTime2=0\tSatTime3=0\tHol1Time1=2359\tHol1Time2=0\tHol1Time3=0\tHol2Time1=2359\tHol2Time2=0\tHol2Time3=0\tHol3Time1=2359\tHol3Time2=0\tHol3Time3=0")), "24h rule identical to ZKBio CVSecurity");
    Check(seenWire.Any(w => w.Contains("MonTime1=45876400\tMonTime2=80610980") && w.Contains("SunTime1=52430000")), "time rule encoding");
    Check(seenWire.Any(w => w.EndsWith("CONTROL DEVICE 0103010F")), "open door 3 for 15 s uses hex 0F");
    Check(seenWire.Any(w => w.Contains("REMOTE OPENDOOREX id=1\topentime=5\tpin=1001")), "REMOTE OPENDOOREX format");
    Check(seenWire.Any(w => Regex.IsMatch(w, @"SET OPTIONS DateTime=\d+$")), "time sync uses SET OPTIONS DateTime=<seconds>");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE usereleauthorize Pin=1001\tAuthorizeTimezoneId=1\tAuthorizeFloorId=0\tAuthorizeFloors=FFFF\tDevID=7\tStartTime=0\tEndTime=0")), "elevator all floors = FFFF like CVSecurity");
    Check(seenWire.Any(w => w.EndsWith("AuthorizeFloors=180\tDevID=7\tStartTime=0\tEndTime=0")), "elevator floors 8+9 = 180 (protocol document example)");
    Check(seenWire.Any(w => w.EndsWith("CONTROL DEVICE 11" + new string('0', 63) + "10105")), "floor 1 release identical to CVSecurity");
    Check(seenWire.Any(w => w.EndsWith("DATA UPDATE DoorParameters ID=16\tName=DoorDrivertime\tValue=5\tDevID=7\nID=16\tName=DoorKeepOpenTimeZone\tValue=0\tDevID=7\nID=16\tName=DoorValidTZ\tValue=1\tDevID=7")), "elevator relay parameters like CVSecurity");
    Check(seenWire.Any(w => w.EndsWith("SET OPTIONS EleNegativeFloorCount=0")), "negative floors option");
    Check(seenWire.Any(w => w.Contains("DATA UPDATE holiday Holiday=20261225\tHolidayType=1\tLoop=1")), "holiday format");
}

cts.Cancel();
try { await loop; } catch { }

Console.WriteLine(failures.Count == 0 ? "\nSELF TEST PASSED" : $"\nSELF TEST FAILED ({failures.Count}):\n - " + string.Join("\n - ", failures));
return failures.Count == 0 ? 0 : 1;

static string AccBits(int length, params int[] on)
{
    var chars = Enumerable.Repeat('0', length).ToArray();
    foreach (var i in on) chars[i] = '1';
    return new string(chars);
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
