# ZKTeco PUSH: documents compared, and why the C3-400 Plus may not work with the old server

Written 2026-10-19 while preparing the test server in this repository. No C3-400 Plus was available yet, so
everything marked **hypothesis** must be confirmed with the traffic log on the first real connection
(see the checklist at the end).

## 1. Which document applies to which device

| Document | Date / version | Protocol | Device family | Applies to C3-400 Plus? |
|---|---|---|---|---|
| *PUSH SDK HTTP Protocol V3.2.0* ("info-push-sdk-user-manual") | 2018-06, doc 3.2.0 | Time & attendance PUSH | Fingerprint/face clocks (TFT, uFace) | **No** |
| *Security PUSH Communication Protocol 20240112* | 2024-01, doc v4.8, protocol 3.1.2 | Access-control PUSH | C3 / C4 / C5 / inBio panels, access terminals | Yes (old server was built on this) |
| *Security PUSH Communication Protocol 20260422* | 2026-04, doc v7.3, protocol 3.1.2 | Access-control PUSH | Same family, newest | **Yes — reference for this repo** |

* The info-push manual is the **attendance** protocol: tables `ATTLOG`, `OPERLOG`, `USERINFO`, `FINGERTMP`,
  init answer `GET OPTION FROM: <SN>`, commands like `DATA UPDATE USERINFO PIN=1\tName=..`. It is older (2018)
  than the Security PUSH documents and has no notion of doors, `userauthorize`, `rtlog` or `rtstate`.
  It was dropped from the test server on request.
* ZKTeco's C3 Plus datasheet states the panel supports *"firmware protocol access control push"* and is managed by
  ZKBio CVAccess / CVSecurity — i.e. the Security PUSH family. So the C3-400 Plus speaks the **same protocol as the
  C3-200 Pro**; the differences are in optional capabilities the device announces and in details the old
  server got wrong but the C3-200 Pro happened to tolerate.

## 2. 2024 (v4.8) → 2026 (v7.3) Security PUSH: what changed

The handshake (init → registry → push → getrequest/ping → devicecmd) is **word-for-word the same**. Changes are
additive and opt-in:

| Version | Change | Impact on our server |
|---|---|---|
| v5.6 (2024-07) | `AccSupportFunList` positions 64–66 (reader parameter delivery, IPC linkage, new anti-passback/interlock rules) | Shown in `GET /api/devices/{sn}`; nothing required |
| v5.6 | `Disable` field in `DATA UPDATE user` | Optional `disable` in `POST /api/users` |
| v5.6 | Aux-input fields (ExtBoardID, InputType, …), new event codes 4014–4017, 5023–5026, 6011–6016 | Logged; event names fall back to category |
| v6.2 (2025-04) | Registry adds `RemoteOpenDoorExFunOn`, `SipConnStatusSupport`, `FaceBgVerifySupport` | Parsed; `REMOTE OPENDOOREX` supported |
| v6.2 | New command **9.4.2 `REMOTE OPENDOOREX id=..\topentime=..\tpin=..`** (remote open with a PIN in the event) | `POST /api/device/doors/{door}/open-ex` |
| v6.2 | `time_offset`, `longitude`, `latitude` fields in `rtlog` / `rtstate` | Shown in the event log line |
| v6.2 | 7.16 device uploads `tabledata` for `extuser`; chapter 11 *universal transparent transmission* (`POST /iclock/cdata?ServerReq=transparent`, JSON) for SIP status / background face comparison | Logged and answered; we never advertise support, so the device should not use it |
| v7.3 (2026-04) | Chapter 4.5 corrected: `/iclock/push` is **POST** (was printed as GET) | Server accepts both |
| v7.3 | `SIPVersion` in registry; SIP contact-group tables; holiday/pass-mode fields in the channel-controller time rule; new chapter 11.3 (elevator call) | Not relevant for C3 |

Unchanged and still important: protocol versions are only 3.0.1 / 3.1.1 / 3.1.2; **3.1.1+ means communication
encryption** (`/iclock/exchange`); the lower of device `pushver` and server `PushProtVer` is used (Appendix 13).

## 3. What the old server does differently from the document

Line numbers refer to `Piramida.Edc.API` (`Controllers/ZKTecoPushController.cs` unless noted).

| # | Old behaviour | Document says | Risk on C3-400 Plus |
|---|---|---|---|
| 1 | `/iclock/push` answers `PushVersion=3.2.3` (`Services/ZKTeco/ZKTecoPushService.cs`) and the init answer has no `PushProtVer` | Server announces `PushProtVer=x`; 3.1.1+ ⇒ encryption key exchange via `POST /iclock/exchange` | **Hypothesis A (high):** a 2024/25 firmware that supports encryption ("Data Encryption" is on by default in the C3 Plus web UI) sees a server claiming 3.2.3, starts `/iclock/exchange`, gets **404** (no such route) and never finishes the handshake. The C3-200 Pro firmware was too old to support encryption, so it ignored this. |
| 2 | No `SvrNSLevel` in the push configuration | Since v4.2: *"device and software can only be connected if they are of the same level"* | **Hypothesis B:** if the new firmware reports `DevNSLevel` ≠ 0, the connection is refused. |
| 3 | Commands always sent as `C:ID:COMMAND` | Appendix 14: if the device registers `CmdFormat=1`, every command needs `DataType=1,SN=..,Priority=0,CmdID=..,CmdDesc=C:..` | **Hypothesis C:** commands are rejected (e.g. -801) or ignored. |
| 4 | `StartTime`/`EndTime` always encoded as seconds | Seconds only when `DateFmtFunOn=1`; when `0` the format is `YYYYMMDD` | **Hypothesis D:** users are written but are immediately "expired" or "not yet valid" (events 29/53). |
| 5 | `PushDataToDevice` uses `EndTime = Now + 200 years` | The device stores the value as unsigned 32-bit: max ≈ year 2133 | Value 7 264 xxx xxx does not fit; firmware may reject (-620) or wrap the date. |
| 6 | `set-access` rejects door masks > 3 (line 1025) and `PushDataToDevice` always sends `AuthorizeDoorId=3` (line 1733) | 4-door panel: masks 1..15 (door 3 = 4, door 4 = 8) | **Certain:** doors 3 and 4 never get access. |
| 7 | Cards always written to `mulcarduser` | Use `mulcarduser` when multi-card is supported (`AccSupportFunList[7]`/`MulCardUserFunOn`), otherwise `user.CardNo` | Wrong table ⇒ -629 / -621 or card ignored. |
| 8 | `rtdata?type=time` answered `DateTime=2026-10-19 17:33:00,ServerTZ=+0200` (line 1629) | `DateTime=<Appendix 5 seconds, GMT>,ServerTZ=+0200` | Clock set wrong ⇒ time rules deny access. The C3-200 Pro may simply never have asked. |
| 9 | Time sync command `SET OPTION DateTime=yyyy-MM-dd HH:mm:ss` | `SET OPTIONS DateTime=<seconds>` | Command fails or clock is wrong. |
| 10 | `CONTROL DEVICE 01{door:00}01{seconds:00}` (line 450) uses decimal | Each byte is `%02X` (hex) | "15 s" becomes 0x15 = 21 s; doors ≥ 10 wrong. Minor. |
| 11 | `table=tabledata` uploads answered `OK` (line 82–94) | Answer must be `<tablename>=<count>` (e.g. `user=1`) | Newer firmware pushes `user`/`extuser`/`DeviceAuthorize` tables at start-up and may resend forever. |
| 12 | Unanswered commands re-sent after 30 s (`ReleaseTimedOutDeliveries`) | — | Duplicate IDs give -7 "repeated command" on slower devices. |
| 13 | No route for `/iclock/exchange`, no catch-all | — | Any new endpoint the firmware calls returns 404 and is invisible in the log. |

## 4. How the new test server handles each point

| # | Test server |
|---|---|
| 1 | Init answer `OK`, push configuration `PushProtVer=3.0.1` ⇒ plain text. `/iclock/exchange` is logged in red if it ever happens. |
| 2 | `SvrNSLevel` echoes the device's `DevNSLevel` (`Push:EchoNsLevel`). |
| 3 | `Push:CmdFormat=auto` follows the device's `CmdFormat`. |
| 4, 5 | Dates follow `DateFmtFunOn`; dates after 2133 are refused with HTTP 400. |
| 6 | Door masks 1..(2^LockCount − 1); request with `doors: [1,2,3,4]`. |
| 7 | Card table chosen from the registry (`cardTarget` to override). |
| 8, 9 | `rtdata` answers encoded GMT seconds; sync uses `SET OPTIONS DateTime=<seconds>`. |
| 10 | `CONTROL DEVICE` bytes formatted `X2`. |
| 11 | `tabledata` answered `<table>=<count>`. |
| 12 | No automatic re-send (configurable). |
| 13 | Catch-all logs every unknown `/iclock/*` call. |

## 5. Command cheat-sheet (Security PUSH, as sent by the test server)

| Purpose | Command (`[TAB]` = tab) |
|---|---|
| Add/update user | `DATA UPDATE user Pin=1001[TAB]Password=1234[TAB]Group=0[TAB]StartTime=861408000[TAB]EndTime=996364799[TAB]Name=Ana Novak` |
| Add card | `DATA UPDATE mulcarduser Pin=1001[TAB]CardNo=12345678[TAB]LossCardFlag=0[TAB]CardType=0` |
| Access to all 4 doors, rule 1 | `DATA UPDATE userauthorize Pin=1001[TAB]AuthorizeTimezoneId=1[TAB]AuthorizeDoorId=15` |
| Time rule 2 (Mon 07:00–12:00) | `DATA UPDATE timezone TimezoneId=2[TAB]SunTime1=0[TAB]…[TAB]MonTime1=45876400[TAB]…` (30 slots, `start<<16 + end`) |
| Holiday | `DATA UPDATE holiday Holiday=20261225[TAB]HolidayType=1[TAB]Loop=1` |
| Delete user completely | `DATA DELETE userauthorize Pin=1001`, `DATA DELETE mulcarduser Pin=1001`, `DATA DELETE user Pin=1001` |
| Delete time rule | `DATA DELETE timezone TimezoneId=2` |
| Read users | `DATA QUERY tablename=user,fielddesc=*,filter=*` → device posts rows to `/iclock/querydata` |
| Count | `DATA COUNT user` |
| Open door 1 for 5 s | `CONTROL DEVICE 01010105` |
| Open door + PIN in event (v6.2+) | `REMOTE OPENDOOREX id=1[TAB]opentime=5[TAB]pin=1001` |
| Reboot | `CONTROL DEVICE 03000000` |
| Set clock | `SET OPTIONS DateTime=<Appendix 5 seconds>` |
| Read parameters | `GET OPTIONS IPAddress,NetMask,GATEIPAddress` |

## 6. First-connection checklist (tomorrow)

1. Controller web UI: `https://192.168.1.201`, user `admin`, password `zkteco@12345` (factory defaults per the C3 Plus manual).
2. *Communication Settings → PUSH Server Settings*: IP of the PC running the server, port **8088**, plain IP (not a domain with https). Leave *Communication Password* empty.
3. Start the server, watch the console. In the cyan `POST /iclock/registry` block, note:
   `PushVersion`, `DevNSLevel`, `CmdFormat`, `DateFmtFunOn`, `MulCardUserFunOn`, `LockCount`, `StringPinFunOn`,
   `WGRuleVer`, `AccSupportFunList` (`GET /api/devices/{sn}` explains every bit).
4. If you see `/iclock/exchange` in red → the device wants encryption; confirm `Push:PushProtVer` is 3.0.1.
   If it persists, turn off *System → Data Encryption* in the web UI or ask ZKTeco for the encryption scheme.
5. If the handshake completes (`Handshake complete` line) but commands fail, the return code meaning is printed next to
   each result; compare with the decisions printed at registration.
6. Send the file `logs/push-traffic-<date>.log` along with any question — it contains every request unfiltered.

## 7. First real connection (2026-10-20, SN AJU8261200103)

Handshake init → registry → push completed in plain text; no `/iclock/exchange`, `WebServerURL=http://…` (no TLS).

| Value | Meaning |
|---|---|
| `pushver=3.1.2` (init URL) | **Protocol** version — the one Appendix 13 talks about. 3.1.2 ≥ 3.1.1 ⇒ the firmware *can* encrypt; our `PushProtVer=3.0.1` kept it plain. |
| `PushVersion=Ver 2.0.34.ACCS-20250410` (registry) | Version of the PUSH client **module inside the firmware** (build date). Different numbering, normal — the doc's own example is `PushVersion=Ver 2.0.22-20161201` with `pushver=3.0.1`. |
| `FirmVer=AC Ver 19.0.18 Apr 23 2025`, `MachineType=10` | Appendix 7: 10 = C3-460 / inBio460 family. |
| `DateFmtFunOn=1`, `DevNSLevel=0`, no `CmdFormat`, no `StringPinFunOn` | Dates as seconds, plain `C:ID:CMD`, numeric PINs. |
| `AccSupportFunList` bits set | 1,2,5–12,14–26,32,33,44,47,48,49,51 → multi-card (`mulcarduser`), DevID field (26), no access/time-rule validity (60/62), none of the 2025/26 bits (64–66), no `RemoteOpenDoorExFunOn`. |
| Cookie `token=…,timestamp=2026-10-20-15-52-24` | token = **MD5(RegistryCode + SN + timestamp)** (verified), not the documented MD5(RegistryCode + SN + SessionID). The timestamp is the controller clock: it was **+6 h** ahead of local time (looks like the China UTC+8 default) → fix with `POST /api/device/sync-time` / web UI time zone, otherwise time rules are shifted. |

**Experiment to confirm whether the old server caused encryption:** set `Push:LegacyPushVersionLine` to `3.2.3`
(what the old server sent), restart, power-cycle the controller. If `/iclock/exchange` appears, that line is the
root cause. Then also try `Push:PushProtVer=3.1.2`.
