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
Check(ReconnectPolicy.ShouldResume(true, true, "00:11:22:33:44:55", false, true), "saved enabled HUD resumes on app/reader/boot activation");
Check(!ReconnectPolicy.ShouldResume(true, false, "00:11:22:33:44:55", false, true), "explicit Stop blocks automatic reconnection");
Check(!ReconnectPolicy.ShouldResume(false, true, "00:11:22:33:44:55", false, true), "automatic connection toggle is honored");
Check(!ReconnectPolicy.ShouldResume(true, true, "", false, true), "first install never connects to an arbitrary HUD");
Check(!ReconnectPolicy.ShouldResume(true, true, "00:11:22:33:44:55", true, true), "recovery does not create parallel workers");
Check(!ReconnectPolicy.ShouldResume(true, true, "00:11:22:33:44:55", false, false), "recovery waits for Bluetooth permission");
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
Check(StartupPolicy.ShouldAutoStart(true, true, false), "autostart opens Navigator on a normal first create");
Check(!StartupPolicy.ShouldAutoStart(false, true, false), "disabled autostart does nothing");
Check(!StartupPolicy.ShouldAutoStart(true, false, false), "autostart does not repeat after recreation");
Check(!StartupPolicy.ShouldAutoStart(true, true, true), "autostart is suppressed for debug probes");
Console.WriteLine($"PASS: {checks} head-unit protocol/transport/recovery checks");
