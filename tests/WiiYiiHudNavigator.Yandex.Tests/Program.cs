using WiiYiiHudNavigator.Common.Models;
using WiiYiiHudNavigator.Plugin.Yandex;

// Synthetic fixtures, NOT samples captured from a phone. Run without Android,
// NuGet test frameworks or a HUD: dotnet run --project tests/WiiYiiHudNavigator.Yandex.Tests
var checks = 0;
void Equal<T>(T expected, T actual, string name)
{
    checks++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, got {actual}");
}
YandexUpdate Parse(string title, string text = "", string? bigText = null) =>
    YandexNotificationParser.Parse(new(YandexNotificationParser.NavigatorPackage, title, text, bigText));
void Turn(string title, string text, DirectionInstruction direction, double distance)
{
    var update = Parse(title, text);
    Equal(YandexUpdateKind.Navigation, update.Kind, title);
    Equal<DirectionInstruction?>(direction, update.Data?.NextTurnDirection, "direction");
    Equal<double?>(distance, update.Data?.NextTurnDistance, "distance");
}

Turn("Через 200 м поверните направо", "", DirectionInstruction.Right, 200);
Turn("Через 1,2 км", "Поверните налево", DirectionInstruction.Left, 1200);
Turn("350\u00a0м", "Держитесь правее", DirectionInstruction.RightFork, 350);
Turn("1\u202f200 м", "Держитесь левее", DirectionInstruction.LeftFork, 1200);
Turn("Через 50 метров развернитесь", "", DirectionInstruction.UTurnLeft, 50);
Turn("Через 30 м развернитесь направо", "", DirectionInstruction.UTurnRight, 30);
Turn("Через 400 м поверните резко направо", "", DirectionInstruction.RightSharpSE, 400);
Turn("Через 2 километра двигайтесь прямо", "", DirectionInstruction.Straight, 2000);
Turn("Сейчас поверните налево", "", DirectionInstruction.Left, 0);
Turn("In 0.5 km turn right", "", DirectionInstruction.Right, 500);
Turn("200 m", "Turn left", DirectionInstruction.Left, 200);
Turn("Через 100 м поверните налево, затем через 50 м поверните направо", "", DirectionInstruction.Left, 100);

Equal(YandexUpdateKind.Ignore, YandexNotificationParser.Parse(new("com.example.other", "Через 100 м поверните налево", "")).Kind, "package filtering");
Equal(YandexUpdateKind.Unsupported, Parse("Яндекс Навигатор", "Впереди камера через 300 м").Kind, "camera is not a maneuver");
Equal(YandexUpdateKind.Unsupported, Parse("Через 300 м", "Правый берег").Kind, "road name is not a maneuver");
Equal(YandexUpdateKind.Unsupported, Parse("Осталось 20 км · 30 мин", "Поверните направо").Kind, "ETA is not turn distance");
Equal(YandexUpdateKind.Unsupported, Parse("Поверните направо").Kind, "missing distance is not zero");
Equal(YandexUpdateKind.Unsupported, Parse("Через 100 м на круговом движении поверните направо").Kind, "unknown roundabout angle");
Equal(YandexUpdateKind.Unsupported, Parse("Навигация", "").Kind, "generic notification");
Equal(YandexUpdateKind.Unsupported, Parse("Через -100 м поверните направо").Kind, "negative distance");
Equal(YandexUpdateKind.Unsupported, Parse("При 100 км/ч поверните направо").Kind, "speed is not distance");
Equal(YandexUpdateKind.Unsupported, Parse("Поверните направо, затем через 100 м налево").Kind, "second distance not reused");
Equal(YandexUpdateKind.Rerouting, Parse("Перестроение маршрута", "", "Через 100 м поверните направо").Kind, "rerouting clears old turn");
Equal(YandexUpdateKind.Finished, Parse("Вы приехали", "", "Через 100 м поверните направо").Kind, "arrival clears old turn");
Equal<double?>(1500, YandexNotificationParser.ReadDistance("1.5 km"), "decimal point");
Equal<double?>(null, YandexNotificationParser.ReadDistance("12 мин"), "minutes");
Equal<double?>(null, YandexNotificationParser.ReadDistance("Infinity км"), "invalid number");
Equal<double?>(null, YandexNotificationParser.ReadDistance("999999999 км"), "distance limit");
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var real = new BridgeState(true, "Right", 300, "Тестовая улица", 670, 180, now, 1, now, 2);
Equal<DirectionInstruction?>(DirectionInstruction.Right, real.ToHudData()?.NextTurnDirection, "captured right turn");
Equal<double?>(300, real.ToHudData()?.NextTurnDistance, "captured turn distance");
Equal<double?>(670, real.ToHudData()?.EtaDistanceRemaining, "captured remaining distance");
Equal<double?>(180, real.ToHudData()?.EtaDurationTimeRemaining, "captured duration");
Equal<HudNavigationData?>(null, (real with { Active = false }).ToHudData(), "removed route");
Equal<HudNavigationData?>(null, (real with { ObservedAtUnixMilliseconds = now - 6000 }).ToHudData(), "stale data cleared");
Equal<HudNavigationData?>(null, (real with { ObservedAtUnixMilliseconds = now + 6000 }).ToHudData(), "future data rejected");
Equal<HudNavigationData?>(null, (real with { ProtocolVersion = 1 }).ToHudData(), "old bridge requires update");
Equal<HudNavigationData?>(null, (real with { Maneuver = "RoundaboutExit3" }).ToHudData(), "unsupported circle angle");
Equal<HudNavigationData?>(null, (real with { NextTurnDistanceMeters = -10 }).ToHudData(), "negative turn distance rejected");
Equal<HudNavigationData?>(null, (real with { RemainingTimeSeconds = double.NaN }).ToHudData(), "nonfinite time rejected");
Equal<DirectionInstruction?>(DirectionInstruction.LeftSharpSE, (real with { Maneuver = "LeftSharp" }).ToHudData()?.NextTurnDirection, "sharp left enum conversion");
Equal(true, (real with { Maneuver = "Rerouting" }).ToHudData()?.IsRerouting, "rerouting conversion");
Equal<DirectionInstruction?>(DirectionInstruction.Right,
    YandexNotificationParser.Parse(new(YandexNotificationParser.MapsPackage, "300 м", "Поверните направо")).Data?.NextTurnDirection, "maps text fallback");
// Camera format verified in Maps 30.7.3: title = distance, description = localized
// notification_speed_camera, visible primaryIcon (untinted), hidden turn icon.
Equal(true, YandexNotificationBridge.CameraText.IsCameraDescription("Камера контроля скорости", "Камера контроля скорости"), "camera description");
Equal(true, YandexNotificationBridge.CameraText.IsCameraDescription("Speed camera", "Speed camera"), "localized camera description");
Equal(false, YandexNotificationBridge.CameraText.IsCameraDescription("Улица Камерная", "Камера контроля скорости"), "road not camera");
Equal(false, YandexNotificationBridge.CameraText.IsCameraDescription("Камера контроля скорости", null), "missing label fail closed");
foreach (var sample in new[] { ("300 м", 300d), ("1,2 км", 1200d), ("1.5 km", 1500d),
    ("1\u202f200 м", 1200d), ("350\u00a0м", 350d), ("−200 м", 0d), ("-100 м", 0d),
    ("0 м", 0d), ("60 км/ч", 0d), ("12 мин", 0d), ("Впереди 300 м", 0d), ("Infinity м", 0d),
    ("6 км", 0d), ("1 20 м", 0d), ("0.5 м", 0d) })
    Equal(sample.Item2, YandexNotificationBridge.CameraText.ApproachingDistance(sample.Item1), "camera distance " + sample.Item1);
var camera = real with { ProtocolVersion = 3, Maneuver = "Camera", NextTurnDistanceMeters = 0, Road = null,
    CameraDistanceMeters = 350, CameraObservedAtUnixMilliseconds = now };
Equal<double?>(350, camera.ToHudData()?.CameraDistance, "camera reaches HUD model");
Equal<DirectionInstruction?>((DirectionInstruction)(-1), camera.ToHudData()?.NextTurnDirection, "camera must not show P parking symbol");
Equal<double?>(0, (camera with { CameraDistanceMeters = 0 }).ToHudData()?.CameraDistance, "explicit camera cancel");
Equal<double?>(0, (camera with { CameraObservedAtUnixMilliseconds = now - 11000 }).ToHudData()?.CameraDistance, "heartbeat does not refresh old camera");
Equal<double?>(0, (camera with { CameraObservedAtUnixMilliseconds = now + 6000 }).ToHudData()?.CameraDistance, "future camera rejected");
Equal<double?>(0, (camera with { CameraDistanceMeters = double.NaN }).ToHudData()?.CameraDistance, "NaN camera rejected");
Equal<double?>(0, (camera with { CameraDistanceMeters = -200 }).ToHudData()?.CameraDistance, "passed camera clears");
Equal<double?>(0, (camera with { CameraDistanceMeters = 99999 }).ToHudData()?.CameraDistance, "camera protocol overflow rejected");
Equal<double?>(0, (camera with { Maneuver = "Rerouting" }).ToHudData()?.CameraDistance, "reroute clears camera");
Equal<HudNavigationData?>(null, (camera with { Active = false }).ToHudData(), "route removal clears all");
Equal<HudNavigationData?>(null, (camera with { ObservedAtUnixMilliseconds = now - 6000 }).ToHudData(), "lost bridge invalidates camera");
Equal<double?>(0, real.ToHudData()?.CameraDistance, "next maneuver cancels preceding camera");
Equal<HudNavigationData?>(null, (camera with { ProtocolVersion = 2 }).ToHudData(), "camera requires new protocol");
Equal<double?>(0, (real with { CameraDistanceMeters = 350, CameraObservedAtUnixMilliseconds = now }).ToHudData()?.CameraDistance, "old protocol cannot inject camera");
now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var fields = new List<YandexNotificationBridge.ScreenField>
{
    new("resetroutebutton2", null, "Закрыть"),
    new("image_maneuverballoon_maneuver", null, "Поверните направо"),
    new("text_maneuverballoon_distance", "300", null), new("text_maneuverballoon_metrics", " м", null),
    new("text_nextstreet", "Следующая улица", null), new("status_panel_text", "Текущая улица", null),
    new("textview_eta_distance", "5 км", "Осталось 5 км"), new("textview_eta_time", "13 мин", null),
    new("text_speedlimit", "60", "Ограничение 60")
};
var screen = YandexNotificationBridge.ScreenNavigationReader.Read(fields, now);
Equal("Right", screen.Maneuver, "captured foreground turn description");
Equal(300d, screen.NextTurnDistanceMeters, "separate screen number and unit");
Equal(5000d, screen.RemainingDistanceMeters, "screen ETA distance");
Equal(780d, screen.RemainingTimeSeconds, "screen ETA duration");
Equal(60, screen.SpeedLimitKph, "screen speed limit");
Equal("Текущая улица", screen.CurrentRoad, "current street preserved separately");
BridgeState Wire(YandexNotificationBridge.BridgeState value) => System.Text.Json.JsonSerializer.Deserialize<BridgeState>(System.Text.Json.JsonSerializer.Serialize(value))!;
var liveHud = Wire(screen).ToHudData()!;
Equal<DirectionInstruction?>(DirectionInstruction.Right, liveHud.NextTurnDirection, "foreground data across wire into HUD");
Equal("60 km/h | Sleduyushchaya ulitsa", liveHud.NextTurnRoadName, "limit and road reach HUD text");
fields.RemoveAll(f => f.Id == "image_maneuverballoon_maneuver");
var unrecognized = Wire(YandexNotificationBridge.ScreenNavigationReader.Read(fields, now)).ToHudData()!;
Equal<DirectionInstruction?>((DirectionInstruction)(-1), unrecognized.NextTurnDirection, "missing arrow is not P");
Equal(0d, unrecognized.NextTurnDistance, "unknown arrow has no false distance");
Equal(5000d, unrecognized.EtaDistanceRemaining, "unknown arrow keeps ETA updated");
Equal("60 km/h | Sleduyushchaya ulitsa", unrecognized.NextTurnRoadName, "unknown arrow keeps road updated");
fields.Add(new("distanceToCamera", "350 м", null));
Equal<double?>(350, Wire(YandexNotificationBridge.ScreenNavigationReader.Read(fields, now)).ToHudData()?.CameraDistance, "camera independent of arrow recognition");
var freeFields = new List<YandexNotificationBridge.ScreenField>
{
    new("navi_service_controller_service_name", "Навигатор", null), new("text_speedlimit", "40", null),
    new("distanceToCamera", "200 м", null), new("status_panel_text", "Текущая улица", null),
    new("text_guidance_dialog_widget_title", "ДТП", null), new("text_guidance_dialog_widget_subtitle", "300 м", null)
};
var free = YandexNotificationBridge.ScreenNavigationReader.Read(freeFields, now + 100);
var freeHud = Wire(free).ToHudData()!;
Equal("free", free.Mode, "free drive mode");
Equal(false, free.Active, "free drive has no route");
Equal<double?>(200, freeHud.CameraDistance, "camera without route");
Equal(0d, freeHud.EtaDistanceRemaining, "free drive clears old route ETA");
Equal("40 km/h | DTP 300 m | Tekushchaya ulitsa", freeHud.NextTurnRoadName, "event, limit and current road without route");
var idle = YandexNotificationBridge.ScreenNavigationReader.Read([], now + 200);
var idleHud = Wire(idle).ToHudData()!;
Equal("-", idleHud.NextTurnRoadName, "ended route explicitly replaces stale road");
Equal<double?>(0, idleHud.CameraDistance, "ended route explicitly cancels camera");
Equal(0d, idleHud.EtaDurationTimeRemaining, "ended route clears duration");
Equal<DirectionInstruction?>((DirectionInstruction)(-1), idleHud.NextTurnDirection, "idle never sends parking symbol");
var selector = new YandexNotificationBridge.SourceSelector();
var notification = screen with { Source = "notification", SourceTimestamp = now - 100 };
Equal("screen", selector.Select(screen, notification, now).Source, "open Maps screen has priority");
Equal("free", selector.Select(free, notification, now + 100).Mode, "route ending on screen wins over stale notification");
Equal(false, selector.Select(null, notification, now + 200).Active, "old notification cannot resurrect ended route");
Equal(true, selector.Select(null, notification with { SourceTimestamp = now + 300 }, now + 300).Active, "new background route works");
var freshSelector = new YandexNotificationBridge.SourceSelector();
Equal("notification", freshSelector.Select(screen with { ObservedAtUnixMilliseconds = now - 3000 }, notification, now).Source, "stale screen falls back to actual notification");
Equal(false, freshSelector.Select(null, YandexNotificationBridge.BridgeState.Inactive, now).Active, "no source clears route");
Equal<string?>("Roundabout", YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("На круговом движении третий съезд"), "roundabout maps to a neutral symbol without guessing the exit");
var slowHud = new SlowHud();
var pump = new HudOutputPump(slowHud);
pump.Publish(new() { NextTurnRoadName = "first", CameraDistance = 300 });
await slowHud.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
pump.Publish(new() { NextTurnRoadName = "obsolete second", CameraDistance = 200 });
pump.Publish(new() { NextTurnRoadName = "-", CameraDistance = 0 });
Equal(1, slowHud.Calls.Count, "slow write never overlaps another write");
slowHud.Release.TrySetResult();
await slowHud.Cleared.Task.WaitAsync(TimeSpan.FromSeconds(3));
Equal(2, slowHud.Calls.Count, "intermediate obsolete route dropped");
Equal("-", slowHud.Calls[1].NextTurnRoadName, "latest stop replaces pending route");
Equal<double?>(0, slowHud.Calls[1].CameraDistance, "camera clear survives delayed Bluetooth");
now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
foreach (var sample in new[] { ("Разворот налево", "UTurnLeft", DirectionInstruction.UTurnLeft),
    ("Разворот направо", "UTurnRight", DirectionInstruction.UTurnRight),
    ("Make a left U-turn", "UTurnLeft", DirectionInstruction.UTurnLeft),
    ("Make a right U-turn", "UTurnRight", DirectionInstruction.UTurnRight) })
{
    var parsed = YandexNotificationBridge.ScreenNavigationReader.Read([
        new("resetroutebutton2", null, null), new("image_maneuverballoon_maneuver", null, sample.Item1),
        new("text_maneuverballoon_distance", "150", null), new("text_maneuverballoon_metrics", "м", null),
        new("text_nextstreet", "Разворот — ул. Ёлочная", null)], now);
    Equal(sample.Item2, parsed.Maneuver, "actual Maps U-turn description " + sample.Item1);
    Equal<DirectionInstruction?>(sample.Item3, Wire(parsed).ToHudData()?.NextTurnDirection, "U-turn survives bridge and plugin");
    Equal<double?>(150, Wire(parsed).ToHudData()?.NextTurnDistance, "U-turn retains distance");
}
Equal("Razvorot - ul. Yolochnaya", HudText.Encode("Разворот — ул. Ёлочная"), "HUD font-safe U-turn text");
Equal("-", HudText.Encode("\u0001\u001b\n"), "control bytes cannot corrupt HUD messages");
Equal(true, HudText.Encode("Щёлковское шоссе · № 25 → разворот\n道路").All(c => c is >= ' ' and <= '~'), "only printable ASCII goes to GB2312 converter");
Equal(64, HudText.Encode(new string('Щ', 100)).Length, "limit applied after transliteration expansion");
Equal("Русская улица", HudText.Encode("Русская улица", false), "Cyrillic option preserved");
Equal("RightSharp", YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("Круто поверните направо"), "captured sharp right description");
Equal("LeftSharp", YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("Круто поверните налево"), "installed sharp left description");
var lanesState = real with { ObservedAtUnixMilliseconds = now, ProtocolVersion = 5, Lanes = "L!,S,R", LanesObservedAtUnixMilliseconds = now };
var bytes = lanesState.ToHudData()!.LaneGuidance!.ToByteArray();
Equal((byte)0x81, bytes[0], "native recommended left lane");
Equal((byte)0x00, bytes[1], "native unselected straight lane");
Equal((byte)0x03, bytes[2], "native unselected right lane");
Equal((byte)0x0F, bytes[3], "native lanes terminate with cancel");
Equal((byte)0x0F, lanesState.ToHudData(showLanes: false)!.LaneGuidance!.ToByteArray()[0], "disable immediately clears lane row");
foreach (var invalid in new[] { lanesState with { Active = false }, lanesState with { Lanes = null },
    lanesState with { Lanes = "L,S,R" }, lanesState with { Lanes = "L!,bad,R" },
    lanesState with { LanesObservedAtUnixMilliseconds = now - 6000 },
    lanesState with { LanesObservedAtUnixMilliseconds = now + 2000 } })
    Equal((byte)0x0F, invalid.ToHudData()!.LaneGuidance!.ToByteArray()[0], "missing, invalid or stale lanes send explicit cancel");
var laneSource = screen with { ObservedAtUnixMilliseconds = now, SourceTimestamp = now };
var laneNotice = laneSource with { Source = "notification", Lanes = "L!,S,R", LanesObservedAtUnixMilliseconds = now };
var laneSelector = new YandexNotificationBridge.SourceSelector();
Equal("L!,S,R", laneSelector.Select(laneSource, laneNotice, now).Lanes, "fresh identical maneuver can supply notification lanes");
Equal<string?>(null, laneSelector.Select(laneSource, laneNotice with { NextTurnDistanceMeters = 900 }, now).Lanes, "different junction distance never supplies lanes");
Equal<string?>(null, laneSelector.Select(laneSource, laneNotice with { Road = "Другая улица" }, now).Lanes, "different road never supplies lanes");
Equal<string?>(null, laneSelector.Select(laneSource, laneNotice with { SourceTimestamp = now - 5000 }, now).Lanes, "old notification cannot supply foreground lanes");
var abbreviated = laneSource with { Maneuver = null, Road = "2-я Лучевая ул." };
var expandedNotice = laneNotice with { Road = "2-я Лучевая улица", Maneuver = "Right" };
Equal("Right", laneSelector.Select(abbreviated, expandedNotice, now).Maneuver, "abbreviated foreground street can recover the same notification turn");
Equal("L!,S,R", laneSelector.Select(abbreviated, expandedNotice, now).Lanes, "abbreviation does not suppress fresh same-junction lanes");
Equal<string?>(null, laneSelector.Select(abbreviated, expandedNotice with { Road = "1-я Лучевая улица" }, now).Maneuver, "similar street with different number cannot supply a turn");
Equal<string?>(null, laneSelector.Select(abbreviated, expandedNotice with { NextTurnDistanceMeters = 400 }, now).Maneuver, "distance change cannot reuse previous turn");
Equal<string?>(null, laneSelector.Select(abbreviated, expandedNotice with { SourceTimestamp = now - 5000 }, now).Maneuver, "stale matching notification cannot restore arrow");
Equal<string?>(null, laneSelector.Select(abbreviated, expandedNotice with { Maneuver = "Camera" }, now).Maneuver, "camera is never a replacement turn");
Equal("Left", laneSelector.Select(abbreviated with { Maneuver = "Left" }, expandedNotice, now).Maneuver, "fresh screen turn wins over conflicting notification");
var endedSelector = new YandexNotificationBridge.SourceSelector();
endedSelector.Select(abbreviated, expandedNotice, now);
endedSelector.Select(idle with { SourceTimestamp = now + 1, ObservedAtUnixMilliseconds = now + 1 }, expandedNotice, now + 1);
Equal(false, endedSelector.Select(null, expandedNotice, now + 2).Active, "abbreviation fallback cannot resurrect ended route");
var duplicateFields = new YandexNotificationBridge.ScreenField[] {
    new("resetroutebutton2", null, null),
    new("image_maneuverballoon_maneuver", null, null),
    new("image_maneuverballoon_maneuver", null, " Поверните\u00a0направо. "),
    new("text_maneuverballoon_distance", "", null),
    new("text_maneuverballoon_distance", "300 м", null),
    new("text_maneuverballoon_metrics", "м", null)
};
var duplicateTurn = YandexNotificationBridge.ScreenNavigationReader.Read(duplicateFields, now);
Equal("Right", duplicateTurn.Maneuver, "blank duplicate and whitespace do not hide populated maneuver");
Equal(300d, duplicateTurn.NextTurnDistanceMeters, "combined distance is not suffixed with a second unit");
Equal<string?>(null, YandexNotificationBridge.ScreenNavigationReader.Read(
    [..duplicateFields, new("image_maneuverballoon_maneuver", null, "Поверните налево")], now).Maneuver, "conflicting screen directions are not guessed");
Equal("Right", YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("Поворот направо"), "noun maneuver description");
Equal<string?>(null, YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("Не поворачивайте направо"), "negative sentence cannot match a turn");
Equal<string?>(null, YandexNotificationBridge.ScreenNavigationReader.ReadManeuver("После поворота направо поверните налево"), "compound instructions cannot match a random substring");
var imageFields = new YandexNotificationBridge.ScreenField[] {
    new("resetroutebutton2", null, null), new("text_nextstreet", "Тестовая улица", null),
    new("image_manoeuvre_balloon_manoeuvre", null, null, new(20, 30, 170, 180)),
    new("text_manoeuvre_balloon_distance", "300", null), new("text_manoeuvre_balloon_metrics", "м", null)
};
var imageState = YandexNotificationBridge.ScreenNavigationReader.Read(imageFields, now);
Equal(true, imageState.Active, "image-only foreground route stays active");
Equal<string?>(null, imageState.Maneuver, "image-only balloon does not invent a text direction");
Equal(300d, imageState.NextTurnDistanceMeters, "automotive alternate current-distance fields");
Equal("Right", YandexNotificationBridge.ScreenNavigationReader.Read(
    imageFields.Select(f => f.Id == "image_manoeuvre_balloon_manoeuvre" ? f with { Description = "Поверните направо" } : f).ToArray(), now).Maneuver,
    "alternate current-arrow description is also supported");
Equal(false, YandexNotificationBridge.ScreenNavigationReader.IsCurrentArrow("next_maneuver_image"), "subsequent arrow is never the current one");
Equal(false, YandexNotificationBridge.ScreenNavigationReader.IsCurrentArrow("next_upcoming_maneuver"), "classic subsequent arrow is excluded");
var area = YandexNotificationBridge.ScreenArrowGuard.Region(imageFields);
Equal(150, area!.Width, "only current identified arrow is cropped");
Equal<YandexNotificationBridge.ScreenArea?>(null, YandexNotificationBridge.ScreenArrowGuard.Region(
    [..imageFields, new("image_maneuverballoon_maneuver", null, null, area)]), "multiple balloons cannot select an arbitrary crop");
Equal<YandexNotificationBridge.ScreenArea?>(null, YandexNotificationBridge.ScreenArrowGuard.Region(
    [new("next_upcoming_maneuver", null, null, area)]), "no current arrow means no capture");
Equal(true, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, imageState, area, area, 4, 4, 100), "fresh unchanged frame accepts result");
foreach (var changed in new[] { imageState with { Active = false }, imageState with { Road = "Другая улица" },
    imageState with { NextTurnDistanceMeters = 200 }, imageState with { Mode = "rerouting" } })
    Equal(false, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, changed, area, area, 4, 4, 100), "route change during capture rejects image result");
Equal(false, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, imageState, area, area, 4, 5, 100), "switching app or window discards image result");
Equal(false, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, imageState, area, area with { Left = 50 }, 4, 4, 100), "rotation or layout change discards image result");
Equal(false, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, imageState, area, area, 4, 4, 1201), "slow capture expires");
Equal(false, YandexNotificationBridge.ScreenArrowGuard.SameFrame(imageState, imageState, area, area, 4, 4, -1), "invalid capture clock rejects result");
Equal<YandexNotificationBridge.ScreenArea?>(null, YandexNotificationBridge.ScreenArrowGuard.Region(
    [new("image_maneuverballoon_maneuver", null, null, new(0, 0, 2000, 1000))]), "whole-screen bounds cannot masquerade as icon");
Equal<double?>(null, YandexNotificationBridge.ScreenNavigationReader.ReadTurnDistance(
    [..imageFields, new("text_maneuverballoon_distance", "600 м", null)]), "conflicting current distances discard direction");
Equal<double?>(0d, YandexNotificationBridge.ScreenNavigationReader.ReadTurnDistance(
    [new("text_maneuverballoon_distance", "0 м", null)]), "actual zero distance remains a valid observation");
Equal<double?>(null, YandexNotificationBridge.ScreenNavigationReader.ReadTurnDistance([]), "missing distance is distinct from actual zero");
var solid = Enumerable.Repeat(unchecked((int)0xff1762ff), 64 * 64).ToArray();
Equal<bool[]?>(null, YandexNotificationBridge.ScreenArrowMask.Extract(solid, 64, 64), "empty solid balloon yields no arrow");
var stripe = (int[])solid.Clone();
for (int y = 0; y < 64; y++) for (int x = 22; x < 40; x++) stripe[y * 64 + x] = -1;
Equal<bool[]?>(null, YandexNotificationBridge.ScreenArrowMask.Extract(stripe, 64, 64), "clipped edge-to-edge shape is rejected");
var rectangle = (int[])solid.Clone();
for (int y = 10; y < 54; y++) for (int x = 22; x < 40; x++) rectangle[y * 64 + x] = -1;
Equal(true, YandexNotificationBridge.ScreenArrowMask.Extract(rectangle, 64, 64) is not null, "opaque contrasting shape is extracted for subsequent template checking");
Equal<bool[]?>(null, YandexNotificationBridge.ScreenArrowMask.Extract(rectangle, 63, 64), "malformed image dimensions rejected");
Console.WriteLine($"PASS: {checks} navigation checks, including route lifecycle, arrow recovery, source switching and stale HUD clearing.");

sealed class SlowHud : WiiYiiHudNavigator.Common.Hud.IHudConnection
{
    public List<HudNavigationData> Calls { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void NoNavigationData() { }
    public async Task UpdateNavigationData(HudNavigationData data, bool hasNoChange = false)
    {
        Calls.Add(data);
        if (Calls.Count == 1) { Started.TrySetResult(); await Release.Task; }
        else Cleared.TrySetResult();
    }
}
