using System.Buffers.Binary;
using System.Text;
using WiiYiiHudNavigator.Common.Models;
using YandexHudHeadUnit;
using YandexNotificationBridge;

int checks = 0;
void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
Check(NavigationSource.AcceptsPackage("ru.yandex.yandexnavi"), "Head unit reads the standalone Navigator");
Check(!NavigationSource.AcceptsPackage("ru.yandex.yandexmaps"), "Maps cannot override the Navigator route");
Check(!NavigationSource.AcceptsPackage(null) && !NavigationSource.AcceptsPackage("ru.yandex.yandexnavi.fake"), "Exact source whitelist");
byte[] Decode(byte[] frame)
{
    string encoded = Encoding.ASCII.GetString(frame);
    Check(encoded[0] == '^' && encoded[^1] == '$', "Frame delimiters");
    var bytes = Convert.FromBase64String(encoded[1..^1]);
    Check(bytes[0] == 0x31 && bytes[1] == 0x30 && bytes[4] == 7, "Frame header");
    Check((bytes[2] << 8 | bytes[3]) == bytes.Where((_, i) => i != 2 && i != 3).Sum(b => (int)b), "Checksum independently verified");
    return bytes;
}
var clear = HudProtocol.Frames(HudProtocol.Clear());
string[] clearGolden = ["3130006A0701000000000100000000", "31300071070900000000", "3130009C0706012D", "3130006B0703000000", "3130012007040F0F0F0F0F0F0F0F0F0F0F0F"];
for (int i = 0; i < clear.Length; i++) Check(Convert.ToHexString(Decode(clear[i])) == clearGolden[i], "Exact cleared frame " + i);

long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var source = new BridgeState(true, "UTurnLeft", 1000, "Разворот — улица Ёлочная", 12500, 3690, now, 1, now,
    CameraDistanceMeters: 1000, CameraObservedAtUnixMilliseconds: now, Lanes: "L!,S,R", LanesObservedAtUnixMilliseconds: now);
var data = HudProtocol.Map(source, true);
var frames = HudProtocol.Frames(data);
Check(Convert.ToHexString(Decode(frames[0])) == "313001650701000803E80100000008", "Left U-turn frame, 1000 m");
Check(Convert.ToHexString(Decode(frames[3])) == "3130015707030103E8", "Camera 1000 m, separate command");
Check(Decode(frames[1])[6..].SequenceEqual(new byte[] { 1, 2, 0, 125 }), "ETA rounds half minute away from zero");
Check(Decode(frames[4])[6..].SequenceEqual(new byte[] { 0x81, 0, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15 }), "Lane positions + recommendation + padding");
var road = Decode(frames[2]);
Check(road[6] == road.Length - 7, "Street byte length");
Check(Encoding.ASCII.GetString(road[7..]) == "Razvorot - ulitsa Yolochnaya", "No CJK street corruption");

foreach (var (name, code) in new (string, byte)[] { ("Left", 2), ("Right", 3), ("LeftWide", 4), ("RightWide", 5), ("LeftFork", 4), ("RightFork", 5), ("LeftSharp", 6), ("RightSharp", 7), ("UTurnLeft", 8), ("UTurnRight", 19), ("Straight", 9), ("Finish", 15) })
{
    var raw = Decode(HudProtocol.Frames(HudProtocol.Map(source with { Maneuver = name }, true))[0]);
    Check(raw[6] == 0 && raw[7] == code && raw[13] == 0 && raw[14] == code, "Native maneuver " + name);
}
var largeDistance = Decode(HudProtocol.Frames(HudProtocol.Map(source with { NextTurnDistanceMeters = 70000 }, true))[0]);
Check(largeDistance[8..13].SequenceEqual(new byte[] { 0x11, 0x70, 1, 0, 1 }), "Distance above 65535 preserved");
var noTurn = HudProtocol.Map(source with { Maneuver = "unrecognized" }, true);
Check(Decode(HudProtocol.Frames(noTurn)[0])[6..10].All(b => b == 0), "Unknown turn does not draw P or stale distance");
Check(noTurn.CameraDistance == 1000 && noTurn.NextTurnRoadName != "-", "Unknown turn preserves warning and street");
var stale = HudProtocol.Map(source with { ObservedAtUnixMilliseconds = now - 6000 }, true);
Check(HudProtocol.Frames(stale).Select(Convert.ToHexString).SequenceEqual(clear.Select(Convert.ToHexString)), "Stale whole snapshot clears every field");
var noRoute = HudProtocol.Map(source with { Active = false, Mode = "idle" }, true);
Check(HudProtocol.Frames(noRoute).Select(Convert.ToHexString).SequenceEqual(clear.Select(Convert.ToHexString)), "Route end clears every field");
Check(HudProtocol.Map(source with { CameraObservedAtUnixMilliseconds = now - 11000 }, true).CameraDistance == 0, "Expired camera");
Check(HudProtocol.Map(source with { CameraDistanceMeters = -5 }, true).CameraDistance == 0, "Already passed camera");
Check(HudProtocol.Map(source with { LanesObservedAtUnixMilliseconds = now - 6000 }, true).LaneGuidance!.Lanes[0].Direction == LaneDirection.Cancel, "Expired lane row");
Check(HudProtocol.Map(source, false).LaneGuidance!.Lanes[0].Direction == LaneDirection.Cancel, "Disabled lane row");
var free = HudProtocol.Map(source with { Active = false, Mode = "free", CurrentRoad = "Ленина", SpeedLimitKph = 60 }, true);
Check(free.CameraDistance == 1000 && free.NextTurnRoadName == "60 km/h | Lenina" && free.NextTurnDistance == 0, "Free drive without stale maneuver");
var sent = new List<byte>();
using var stop = new CancellationTokenSource();
bool cancelled = false;
try
{
    await HudPacketWriter.SendAsync(frames, chunk =>
    {
        Check(chunk.Length <= 20, "MTU 23 uses at most 20 payload bytes");
        sent.AddRange(chunk);
        stop.Cancel(); // Stop arrives in the middle of the first navigation frame.
        return Task.CompletedTask;
    }, stop.Token, () => Task.CompletedTask);
}
catch (OperationCanceledException) { cancelled = true; }
Check(cancelled && sent.SequenceEqual(frames[0]), "Stop finishes the current frame but sends no stale next frame");
sent.Clear();
await HudPacketWriter.SendAsync(clear, chunk => { sent.AddRange(chunk); return Task.CompletedTask; }, CancellationToken.None, () => Task.CompletedTask);
Check(sent.SequenceEqual(clear.SelectMany(f => f)), "Clear writes all complete frames in order");
int writes = 0;
bool disconnected = false;
try
{
    await HudPacketWriter.SendAsync(frames, _ => { writes++; throw new IOException("Disconnected"); }, CancellationToken.None, () => Task.CompletedTask);
}
catch (IOException) { disconnected = true; }
Check(disconnected && writes == 1, "Native failure aborts remaining chunks");
Check(ReconnectPolicy.ShouldResume(true, true, Activation.Navigator, "00:11:22:33:44:55", false, true), "saved enabled HUD resumes on navigator activation");
Check(!ReconnectPolicy.ShouldResume(true, false, Activation.Navigator, "00:11:22:33:44:55", false, true), "explicit Stop blocks navigator reconnection");
Check(!ReconnectPolicy.ShouldResume(true, false, Activation.Notification, "00:11:22:33:44:55", false, true), "explicit Stop blocks notification reconnection");
Check(ReconnectPolicy.ShouldResume(true, false, Activation.App, "00:11:22:33:44:55", false, true), "opening the app re-arms a stopped transfer");
Check(ReconnectPolicy.ShouldResume(true, false, Activation.Boot, "00:11:22:33:44:55", false, true), "boot re-arms a stopped transfer");
Check(ReconnectPolicy.ShouldResume(true, false, Activation.Automation, "00:11:22:33:44:55", false, true), "automation connect always arms the transfer");
Check(!ReconnectPolicy.ShouldResume(false, true, Activation.App, "00:11:22:33:44:55", false, true), "automatic connection toggle is honored");
Check(!ReconnectPolicy.ShouldResume(true, true, Activation.Navigator, "", false, true), "first install never connects to an arbitrary HUD");
Check(!ReconnectPolicy.ShouldResume(true, true, Activation.Navigator, "00:11:22:33:44:55", true, true), "recovery does not create parallel workers");
Check(!ReconnectPolicy.ShouldResume(true, true, Activation.Navigator, "00:11:22:33:44:55", false, false), "recovery waits for Bluetooth permission");
const string package = "com.avashield.yandexhud.phone";
Check(HudAutomation.Parse(package, package + ".CONNECT") == HudCommand.Connect, "automation CONNECT action parsed");
Check(HudAutomation.Parse(package, package + ".DISCONNECT") == HudCommand.Disconnect, "automation DISCONNECT action parsed");
Check(HudAutomation.Parse(package, package + ".EXIT") == HudCommand.Exit, "automation EXIT action parsed");
Check(HudAutomation.Parse(package, package + ".OPEN") == HudCommand.Open, "automation OPEN action parsed");
Check(HudAutomation.Parse(package, "com.avashield.yandexhud.headunit.CONNECT") == HudCommand.None, "the other flavor action is ignored");
Check(HudAutomation.Parse(package, null) == HudCommand.None && HudAutomation.Parse(null, package + ".CONNECT") == HudCommand.None, "empty automation intents are ignored");
Check(HudAutomation.Action(package, HudCommand.Exit) == package + ".EXIT", "automation EXIT action built");
Check(ReconnectPolicy.WokeFromSleep(1000, 61000), "stale connected GATT is renewed after head unit sleep");
Check(!ReconnectPolicy.WokeFromSleep(0, 61000) && !ReconnectPolicy.WokeFromSleep(1000, 1800), "normal cycles and initial startup keep connection");
Check(ReconnectPolicy.RetrySeconds(1) == 5 && ReconnectPolicy.RetrySeconds(100) == 30, "offline HUD retries remain bounded");
using var signal = new ReconnectSignal();
signal.Pulse(); signal.Pulse();
Check(await signal.WaitAsync(0, CancellationToken.None), "wakeup before wait is not lost");
Check(!await signal.WaitAsync(0, CancellationToken.None), "duplicate wakeups do not cause reconnect storms");
var pendingWake = signal.WaitAsync(30000, CancellationToken.None);
signal.Pulse();
Check(await pendingWake.WaitAsync(TimeSpan.FromSeconds(1)), "button or adapter change interrupts long backoff immediately");
using var cancelWait = new CancellationTokenSource();
var pendingStop = signal.WaitAsync(30000, cancelWait.Token);
cancelWait.Cancel();
bool waitStopped = false;
try { await pendingStop; } catch (OperationCanceledException) { waitStopped = true; }
Check(waitStopped, "Stop interrupts offline backoff");
foreach (var (elapsed, code) in new (long, byte)[] { (0, 2), (3000, 3), (6000, 8), (9000, 19) })
{
    var sample = HudArrowCheck.At(elapsed)!;
    Check(sample.NextTurnRoadName!.StartsWith("TEST ") && sample.CameraDistance == 0 && sample.EtaDistanceRemaining == 0,
        "stationary test is labeled and contains no real route warnings");
    Check(Decode(HudProtocol.Frames(sample)[0])[7] == code, "stationary arrow check reaches native command " + code);
}
Check(HudArrowCheck.At(-1) is null && HudArrowCheck.At(12000) is null, "stationary test is bounded and returns to current navigation");

long locationNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var location = new HudLocation(55.75, 37.62, 12.5, 180, 5, locationNow);
Check(location.IsUsable(locationNow), "fresh GPS location is usable");
Check(!location.IsUsable(locationNow + HudLocation.MaxAgeMilliseconds + 1), "stale GPS location is rejected");
Check(!(location with { Latitude = 91 }).IsUsable(locationNow), "latitude outside range is rejected");
Check(!(location with { Longitude = -181 }).IsUsable(locationNow), "longitude outside range is rejected");
Check(!(location with { Latitude = double.NaN }).IsUsable(locationNow), "nonfinite coordinate is rejected");
Check(!(location with { AccuracyMeters = HudLocation.MaxAccuracyMeters + 1 }).IsUsable(locationNow), "low accuracy is rejected");
Check(!(location with { AccuracyMeters = double.NaN }).IsUsable(locationNow), "nonfinite accuracy is rejected");
Check(!(location with { TimestampUnixMilliseconds = locationNow + 10000 }).IsUsable(locationNow), "future location is rejected");
Check(!(location with { TimestampUnixMilliseconds = 0 }).IsUsable(locationNow), "missing timestamp is rejected");
Check(!(location with { BearingDegrees = 361 }).IsUsable(locationNow), "bearing outside range is rejected");
Check(!(location with { SpeedMetersPerSecond = -1 }).IsUsable(locationNow), "negative speed is rejected");
Check(StartupPolicy.ShouldAutoStart(true, false, false), "autostart opens Navigator when the app is opened");
Check(!StartupPolicy.ShouldAutoStart(false, false, false), "disabled autostart does nothing");
Check(!StartupPolicy.ShouldAutoStart(true, true, false), "returning from Navigator does not reopen it");
Check(!StartupPolicy.ShouldAutoStart(true, false, true), "autostart is suppressed for debug probes");
// ---- ESP32 HUD protocol v1 encoder (golden vectors) ----
long espTs = 1762500000000;
string Hex(byte[] bytes) => Convert.ToHexString(bytes);
void Vector(string label, byte[] actual, string expected) =>
    Check(Hex(actual) == expected.ToUpperInvariant(), label);
byte[]? Group(byte[] message, byte group)
{
    int i = 8;
    int end = message.Length - 2;
    while (i + 2 <= end)
    {
        int length = message[i + 1];
        if (i + 2 + length > end) return null;
        if (message[i] == group) return message[(i + 2)..(i + 2 + length)];
        i += 2 + length;
    }
    return null;
}
bool HasGroup(byte[] message, byte group) => Group(message, group) is not null;
BridgeState State(bool active, string? maneuver = null, double distance = 0, string? road = null,
    double remaining = 0, double seconds = 0, string mode = "route", string? currentRoad = null,
    int limit = 0, double camera = 0, long cameraAt = 0, string? lanes = null,
    string source = "notification", bool screen = false, string? roadEvent = null,
    double eventDistance = 0, long observed = 0) =>
    new(active, maneuver, distance, road, remaining, seconds, espTs, 1, observed,
        Mode: mode, CameraDistanceMeters: camera, CameraObservedAtUnixMilliseconds: cameraAt,
        Source: source, CurrentRoad: currentRoad, SpeedLimitKph: limit, RoadEvent: roadEvent,
        RoadEventDistanceMeters: eventDistance, ScreenReaderConnected: screen, Lanes: lanes,
        LanesObservedAtUnixMilliseconds: lanes is null ? 0 : observed);

var espIdle = State(false, mode: "idle", observed: espTs);
var espRoute = State(true, "Right", 300, "Тверская", 12500, 3690, "route", "Ленина", 60,
    lanes: "R!,S,L", source: "screen", screen: true, observed: espTs);
var espCamera = State(true, null, 0, null, 12500, 3690, "route", null, 60,
    camera: 1000, cameraAt: espTs, roadEvent: "ДТП", eventDistance: 800, observed: espTs);
var espFree = State(false, null, 0, null, 0, 0, "free", "Ленина", 60, observed: espTs);
var espFar = State(true, "Right", 70000, "М-7", 0, 0, "route", null, 90, observed: espTs);
var espRerouting = State(true, "Rerouting", 0, null, 12500, 3690, "route", null, 0, observed: espTs);
var espFreeCamera = State(false, null, 0, null, 0, 0, "free", "Ленина", 60,
    camera: 1000, cameraAt: espTs, observed: espTs);
var espRoundabout = State(true, "Roundabout", 350, null, 4000, 240, "route", null, 0, observed: espTs);

Vector("ESP32 idle", Esp32HudProtocol.EncodeState(espIdle, true, espTs, 100), Esp32HudVectors.Idle);
Vector("ESP32 route right 300 m", Esp32HudProtocol.EncodeState(espRoute, true, espTs, 101), Esp32HudVectors.RouteRight300M);
Vector("ESP32 camera 1000 m", Esp32HudProtocol.EncodeState(espCamera, true, espTs, 102), Esp32HudVectors.Camera1000M);
Vector("ESP32 free", Esp32HudProtocol.EncodeState(espFree, true, espTs, 103), Esp32HudVectors.Free);
Vector("ESP32 far maneuver", Esp32HudProtocol.EncodeState(espFar, true, espTs, 104, includeMeta: false), Esp32HudVectors.FarManeuver70000M);
Vector("ESP32 rerouting", Esp32HudProtocol.EncodeState(espRerouting, true, espTs, 105, includeMeta: false), Esp32HudVectors.Rerouting);
Vector("ESP32 position", Esp32HudProtocol.EncodePosition(new HudLocation(55.7558, 37.6173, 12.5, 45, 3.5, espTs), 900), Esp32HudVectors.Position);
Vector("ESP32 free camera", Esp32HudProtocol.EncodeState(espFreeCamera, true, espTs, 106), Esp32HudVectors.FreeCamera);
Vector("ESP32 roundabout", Esp32HudProtocol.EncodeState(espRoundabout, true, espTs, 107), Esp32HudVectors.Roundabout);
Vector("ESP32 position NaN", Esp32HudProtocol.EncodePosition(new HudLocation(55.7558, 37.6173, null, null, 3.5, espTs), 901), Esp32HudVectors.PositionNan);

// Coordinate round-trip: negative latitude/longitude, zero speed, NaN bearing.
var espPosition2 = new HudLocation(-33.8688, 151.2093, 0.0, null, 1.5, espTs);
var espPositionBytes = Esp32HudProtocol.EncodePosition(espPosition2, 1234);
Check(espPositionBytes.Length == 48, "ESP32 position: length");
Check(espPositionBytes[0] == Esp32HudProtocolConstants.Version &&
      espPositionBytes[1] == Esp32HudProtocolConstants.TypePosition, "ESP32 position: header");
var espPositionG9 = Group(espPositionBytes, Esp32HudProtocolConstants.GroupG9_POSITION);
Check(espPositionG9 is { Length: 36 }, "ESP32 position: G9 length");
var espG9 = espPositionG9!;
Check(BinaryPrimitives.ReadDoubleLittleEndian(espG9.AsSpan(0)) == -33.8688, "ESP32 position: negative latitude");
Check(BinaryPrimitives.ReadDoubleLittleEndian(espG9.AsSpan(8)) == 151.2093, "ESP32 position: longitude");
Check(BinaryPrimitives.ReadSingleLittleEndian(espG9.AsSpan(16)) == 0.0f, "ESP32 position: zero speed");
Check(BinaryPrimitives.ReadInt32LittleEndian(espG9.AsSpan(20)) == unchecked((int)0x7FC00000),
    "ESP32 position: canonical NaN bearing");
Check(BinaryPrimitives.ReadSingleLittleEndian(espG9.AsSpan(24)) == 1.5f, "ESP32 position: accuracy");
Check(BinaryPrimitives.ReadInt64LittleEndian(espG9.AsSpan(28)) == espTs, "ESP32 position: timestamp");
Check(Esp32HudProtocol.Crc16(espPositionBytes.AsSpan(0, espPositionBytes.Length - 2)) ==
      BinaryPrimitives.ReadUInt16LittleEndian(espPositionBytes.AsSpan(espPositionBytes.Length - 2)),
    "ESP32 position: CRC");
var espPositionChunks = Esp32HudProtocol.Fragment(espPositionBytes, 23);
Check(espPositionChunks.Count == 3, "ESP32 position: MTU 23 uses 3 chunks");
Check(espPositionChunks.All(c => c[0] == Esp32HudProtocolConstants.FragmentMagic), "ESP32 position: fragment magic");
Check(espPositionChunks.SelectMany(c => c.Skip(3)).SequenceEqual(espPositionBytes), "ESP32 position: fragments reassemble");
Check(Esp32HudProtocol.Fragment(espPositionBytes, 247).Count == 1, "ESP32 position: MTU 247 uses one write");

var espRouteMessage = Esp32HudProtocol.EncodeState(espRoute, true, espTs, 101);
var espChunks = Esp32HudProtocol.Fragment(espRouteMessage, 23);
Check(espChunks.Count == 5, "ESP32 MTU 23 route message uses 5 chunks");
Check(espChunks.All(c => c[0] == Esp32HudProtocolConstants.FragmentMagic), "ESP32 fragment magic");
Check(espChunks.SelectMany(c => c.Skip(3)).SequenceEqual(espRouteMessage), "ESP32 fragments reassemble");
Check(Esp32HudProtocol.Fragment(espRouteMessage, 247).Count == 1, "ESP32 MTU 247 uses one write");
Check(Esp32HudProtocol.Crc16(Encoding.ASCII.GetBytes("123456789")) == 0x29B1, "ESP32 CRC check vector");

var espEncoder = new Esp32HudEncoder();
_ = espEncoder.BuildState(espRoute, true, espTs);
var espNoManeuver = espRoute with { Maneuver = "Camera", NextTurnDistanceMeters = 0 };
Check(HasGroup(espEncoder.BuildState(espNoManeuver, true, espTs + 1000), Esp32HudProtocolConstants.GroupG1_MANEUVER),
    "ESP32 holds G1 for 4 s without a fresh maneuver");
Check(!HasGroup(espEncoder.BuildState(espNoManeuver, true, espTs + 6000), Esp32HudProtocolConstants.GroupG1_MANEUVER),
    "ESP32 stops holding G1 after 4 s");

var espLongText = new string('я', 64);
var espTextMessage = Esp32HudProtocol.EncodeState(espRoute with { Road = espLongText }, true, espTs, 1);
var espG3 = Group(espTextMessage, Esp32HudProtocolConstants.GroupG3_NEXT_STREET);
Check(espG3 is { Length: 64 }, "ESP32 text trims to 64 bytes");
Check(espG3 is not null && Encoding.UTF8.GetString(espG3).All(ch => ch == 'я'),
    "ESP32 text trims on a code-point boundary");
var espDirtyMessage = Esp32HudProtocol.EncodeState(espRoute with { Road = "Тест\nРоад" }, true, espTs, 1);
var espDirty = Group(espDirtyMessage, Esp32HudProtocolConstants.GroupG3_NEXT_STREET);
Check(espDirty is not null && Encoding.UTF8.GetString(espDirty) == "ТестРоад",
    "ESP32 removes control characters from text");

Console.WriteLine($"PASS: {checks} head-unit protocol/transport/recovery checks");
