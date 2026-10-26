# EDC – ZKTeco C3 Plus PUSH test server

A small ASP.NET Core 8 server that speaks ZKTeco's **Security PUSH protocol** (access control, document
*Security PUSH Communication Protocol 2026-04-22, v7.3*) so we can bring up the **C3-400 Plus**, see exactly
what it sends, and drive every basic command from Swagger.

* Why the old `Piramida.Edc.API` code may not work with the C3-400 Plus, and what changed between the protocol
  documents: **[docs/PROTOCOL-COMPARISON.md](docs/PROTOCOL-COMPARISON.md)**
* Step-by-step Swagger test (add → query → delete): **[docs/FULL-CYCLE-TEST.txt](docs/FULL-CYCLE-TEST.txt)**
* Elevator controller (floors) test steps: **[docs/ELEVATOR-TEST.txt](docs/ELEVATOR-TEST.txt)**
* A fake controller (`tools/EdcZkPush.Simulator`) lets you try everything without hardware; GitHub Actions runs it
  as a self test on every push.

## Run

Requires the .NET 8 SDK.

```bash
dotnet run --project src/EdcZkPush.Api
```

The console prints the IP addresses to enter in the controller. Swagger: `http://<pc-ip>:8088/swagger`.
On Windows allow inbound TCP 8088 in the firewall (`netsh advfirewall firewall add rule name="ZK PUSH 8088" dir=in action=allow protocol=TCP localport=8088`).

Try it without a controller (second terminal):

```bash
dotnet run --project tools/EdcZkPush.Simulator -- --server http://localhost:8088            # behaves like a C3-400 Plus
dotnet run --project tools/EdcZkPush.Simulator -- --server http://localhost:8088 --selftest # runs all checks, exit code 0/1
```

## Controller setup (C3-400 Plus)

1. Web UI `https://192.168.1.201` — `admin` / `zkteco@12345` (factory default). PC and controller in the same subnet.
2. *Network Setting → TCP/IP*: give it an address in your LAN, reboot.
3. *Communication Settings → PUSH Server Settings*: IP mode, server = PC IP, port **8088**.
   Leave *Communication Password* empty.
4. The controller connects within ~30 s. Watch the cyan handshake block in the console.

## Logging

Everything the controller sends and everything we answer is printed with headers and bodies (tabs shown as `⇥`),
plus a decoded line for events, door states and command results.

| Key | Toggles |
|---|---|
| **H** | heartbeats (`ping`, empty `getrequest`) — off by default, a summary line is printed every 60 s |
| **S** | `rtstate` (door/relay/alarm status posted every few seconds): `changes` (default) → `all` → `none` |
| **B** | request/response bodies |
| **D** | headers |

The same switches: `GET/PUT /api/logging` or `TrafficLog` in `appsettings.json`.
An **unfiltered** copy of all traffic goes to `logs/push-traffic-<date>.log`.

## Swagger groups

Every command endpoint returns the exact wire text and a `statusUrl`. Add **`preview=true`** to only see the command.
Leave **`sn`** empty when one controller is connected. Follow a command with `GET /api/commands/{id}?waitSeconds=10`
(status, return code + meaning, and rows uploaded for queries).

| Group | Endpoints |
|---|---|
| 1 · Devices, commands & logging | `GET /api/devices`, `GET /api/devices/{sn}` (registry + what it means), `GET /api/devices/{sn}/traffic`, `GET /api/commands`, `GET /api/commands/{id}`, `DELETE /api/devices/{sn}/queue`, `GET/PUT /api/logging` |
| 2 · Users | `POST /api/users` (optionally with a card), `POST /api/users/batch`, `DELETE /api/users/{pin}`, `DELETE /api/users/{pin}/complete`, `DELETE /api/users/all`, `POST /api/users/query`, `POST /api/users/count` |
| 3 · Cards | `POST /api/cards`, `POST /api/cards/batch`, `DELETE /api/cards/{pin}`, `POST /api/cards/query` |
| 4 · Access | `PUT /api/access`, `PUT /api/access/batch`, `DELETE /api/access/{pin}`, `POST /api/access/query` |
| 5 · Time rules & holidays | `PUT /api/timerules`, `DELETE /api/timerules/{id}`, `POST /api/timerules/query`, `GET /api/timerules/decode/{value}`, `PUT/DELETE /api/holidays` |
| 6 · Doors, device & options | open / open-ex / close / normal-open / lock door, aux output, cancel alarm, reboot, sync time, `options/get`, `options/set` |
| 8 · Elevator (floors) | `PUT /api/elevator/access`, `PUT /api/elevator/access/batch`, `DELETE /api/elevator/access/{pin}`, `POST /api/elevator/access/query`, `POST /api/elevator/floors/release`, `POST /api/elevator/setup` |
| 7 · Records & raw | `POST /api/transactions/query`, `POST /api/tables/{table}/count`, `POST /api/tables/{table}/query`, `DELETE /api/transactions`, `POST /api/raw` |

### Minimal "give someone access" sequence

```http
POST /api/users      { "pin": "1001", "name": "Ana Novak", "cardNo": "12345678" }
PUT  /api/access     { "pin": "1001", "doors": [1,2,3,4], "timezoneId": 1 }
```

Restricted hours: create a rule and reference it.

```http
PUT  /api/timerules  { "timezoneId": 2, "weekdays": ["07:00-17:00"], "weekend": [] }
PUT  /api/access     { "pin": "1001", "doors": [1,2], "timezoneId": 2 }
```

## Settings (`appsettings.json` → `Push`)

| Setting | Default | Why |
|---|---|---|
| `PushProtVer` | `3.0.1` | 3.1.1+ makes the controller start encrypted communication (`/iclock/exchange`), which is not implemented |
| `RequestDelay` | `3` | seconds between `getrequest` polls |
| `EchoNsLevel` | `true` | answer `SvrNSLevel` = device `DevNSLevel` (must match) |
| `CmdFormat` | `auto` | follow the device; force `0` or `1` if needed |
| `MaxCommandsPerPoll` | `1` | commands handed out per poll |
| `ResendUnansweredAfterSeconds` | `0` | re-send commands without result (0 = never) |
| `AnswerRegistryOkForKnownDevices` | `false` | `true` skips re-registration of known devices |
| `RtDataTimeMode` | `gmt` | `rtdata?type=time` answer basis (`local` if the clock ends up 2 h off) |
| `TimeZone` | `Europe/Ljubljana` | for time sync |

State (devices, queue) is in memory — restarting the server forgets it; the controller simply registers again.
