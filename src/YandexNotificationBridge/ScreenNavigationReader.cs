using System.Globalization;
using System.Text.RegularExpressions;

namespace YandexNotificationBridge;

internal sealed record ScreenField(string Id, string? Text, string? Description, ScreenArea? Area = null);
internal sealed record ScreenArea(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
    internal bool ValidArrow => Left >= 0 && Top >= 0 && Width is >= 16 and <= 640 && Height is >= 16 and <= 640;
}

internal static class ScreenNavigationReader
{
    internal static readonly HashSet<string> FieldIds = new(StringComparer.Ordinal)
    {
        "image_maneuverballoon_maneuver", "text_maneuverballoon_distance", "text_maneuverballoon_metrics",
        "image_manoeuvre_balloon_manoeuvre", "text_manoeuvre_balloon_distance", "text_manoeuvre_balloon_metrics",
        "text_nextstreet", "status_panel_text", "textview_eta_distance", "textview_eta_time",
        "resetroutebutton2", "text_speedlimit", "custom_view_speed_limit_primary_text",
        "distanceToCamera", "navi_guidance_next_camera_view", "navi_service_next_camera",
        "text_guidance_dialog_widget_title", "text_guidance_dialog_widget_subtitle",
        "dialog_widget_title", "dialog_widget_subtitle", "navi_service_controller_service_name",
        "navi_service_automatic_freedrive_container", "text_speed_value"
    };
    private static readonly Regex DistancePattern = new(@"^\s*(?<n>(?:\d{1,3}(?:[ \u00a0\u202f]\d{3})+|\d+)(?:[.,]\d+)?)\s*(?<u>км|м|km|m)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex DurationPattern = new(@"(?<n>\d+)\s*(?<u>ч|h|мин|min)\b", RegexOptions.IgnoreCase);
    private static readonly Regex RoundaboutPattern = new(
        @"\b(?:круг\w*|кольц\w*|roundabout|traffic\s+circle|rotary)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static BridgeState Read(IReadOnlyList<ScreenField> fields, long now, IReadOnlyDictionary<string, string>? labels = null)
    {
        string? Text(string id) => fields.Where(f => f.Id == id).Select(f => f.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        bool Has(string id) => fields.Any(f => f.Id == id);
        var route = Has("resetroutebutton2") || Has("textview_eta_time");
        var driving = route || Has("navi_service_controller_service_name") || Has("navi_service_automatic_freedrive_container") || Has("text_speed_value");
        if (!driving) return BridgeState.Inactive with { Source = "screen", ObservedAtUnixMilliseconds = now, SourceTimestamp = now };
        // A landscape layout can contain an empty duplicate before the actual
        // maneuver. Read the populated field, and reject conflicting directions.
        var directions = fields.Where(f => IsCurrentArrow(f.Id))
            .Select(f => ReadManeuver(f.Description, labels) ?? ReadManeuver(f.Text, labels))
            .Where(d => d is not null).Distinct().ToArray();
        var maneuver = route && directions.Length == 1 ? directions[0] : null;
        var distance = ReadTurnDistance(fields);
        if (distance is null) maneuver = null;
        var camera = CameraText.ApproachingDistance(Text("distanceToCamera"));
        var title = Text("text_guidance_dialog_widget_title") ?? Text("dialog_widget_title");
        var subtitle = Text("text_guidance_dialog_widget_subtitle") ?? Text("dialog_widget_subtitle");
        var roadEvent = ReadEvent(title);
        var eventDistance = ReadDistance(Regex.Replace(subtitle ?? "", @"^\s*(?:Через|In)\s+", "", RegexOptions.IgnoreCase)) ?? 0;
        if (roadEvent == "Камера" && camera == 0) camera = CameraText.ApproachingDistance(subtitle);
        int.TryParse(Text("text_speedlimit") ?? Text("custom_view_speed_limit_primary_text"), out int limit);
        if (limit < 5 || limit > 200) limit = 0;
        return new(route, maneuver, distance ?? 0, route ? Text("text_nextstreet") : null,
            route ? ReadDistance(Text("textview_eta_distance")) ?? 0 : 0,
            route ? ReadDuration(Text("textview_eta_time")) : 0, now, 0, now,
            CameraDistanceMeters: camera, CameraObservedAtUnixMilliseconds: now, Source: "screen",
            Mode: route ? "route" : "free", CurrentRoad: Text("status_panel_text"), SpeedLimitKph: limit,
            RoadEvent: roadEvent, RoadEventDistanceMeters: eventDistance, SourceTimestamp: now);
    }

    internal static bool IsCurrentArrow(string id) => id is "image_maneuverballoon_maneuver" or "image_manoeuvre_balloon_manoeuvre";
    internal static double? ReadTurnDistance(IReadOnlyList<ScreenField> fields)
    {
        // These two pairs belong to the CURRENT automotive maneuver. The
        // next_upcoming/next_maneuver fields describe a different junction.
        var values = new List<double>();
        foreach (string prefix in new[] { "text_maneuverballoon_", "text_manoeuvre_balloon_" })
        {
            string? Text(string id) => fields.Where(f => f.Id == id).Select(f => f.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var number = Text(prefix + "distance");
            var distance = ReadDistance(number) ?? ReadDistance(number + Text(prefix + "metrics"));
            if (distance is not null) values.Add(distance.Value);
        }
        return values.Distinct().Count() == 1 ? values[0] : null;
    }

    internal static string NormalizeManeuver(string? text) => Regex.Replace(text ?? "", @"\s+", " ")
        .Trim().TrimEnd('.', '!', '…').ToLowerInvariant();

    internal static string? ReadManeuver(string? text, IReadOnlyDictionary<string, string>? labels = null)
    {
        var value = NormalizeManeuver(text);
        if (labels?.TryGetValue(value, out var installed) == true) return installed;
        // A roundabout exit number does not give the exit angle. Send a neutral
        // roundabout symbol with the distance instead of guessing a turn.
        if (RoundaboutPattern.IsMatch(value)) return "Roundabout";
        return value switch
        {
            "поверните направо" or "поворот направо" or "turn right" => "Right",
            "поверните налево" or "поворот налево" or "turn left" => "Left",
            "держитесь правее" or "keep right" => "RightFork",
            "держитесь левее" or "keep left" => "LeftFork",
            "плавно поверните направо" or "поверните правее" or "slight right" or "make a slight right" => "RightWide",
            "плавно поверните налево" or "поверните левее" or "slight left" or "make a slight left" => "LeftWide",
            "резко поверните направо" or "круто поверните направо" or "sharp right" or "make a sharp right turn" => "RightSharp",
            "резко поверните налево" or "круто поверните налево" or "sharp left" or "make a sharp left turn" => "LeftSharp",
            "двигайтесь прямо" or "прямо" or "go straight" => "Straight",
            // Maps 30.7.3 uses navi_make_a_left/right_u_turn, not the spoken imperative.
            "развернитесь" or "развернитесь налево" or "разворот налево" or "make a u-turn" or "make a left u-turn" => "UTurnLeft",
            "развернитесь направо" or "разворот направо" or "make a right u-turn" => "UTurnRight",
            "вы приехали" or "финиш" or "destination" => "Finish",
            _ => null
        };
    }
    private static string? ReadEvent(string? text)
    {
        string value = (text ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("камера") || value.StartsWith("speed camera")) return "Камера";
        if (value == "дтп" || value.StartsWith("авария") || value.StartsWith("accident")) return "ДТП";
        if (value.StartsWith("дорожные работы") || value.StartsWith("road works")) return "Дорожные работы";
        if (value.StartsWith("перекрытие") || value.StartsWith("road closed")) return "Перекрытие";
        if (value.StartsWith("опасность") || value.StartsWith("hazard")) return "Опасность";
        if (value.StartsWith("лежачий полицейский") || value.StartsWith("speed bump")) return "Лежачий полицейский";
        return null;
    }
    internal static double? ReadDistance(string? text)
    {
        var match = DistancePattern.Match(text ?? "");
        if (!match.Success) return null;
        string number = match.Groups["n"].Value.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        if (match.Groups["u"].Value.Length == 2) value *= 1000;
        return double.IsFinite(value) && value <= 50_000_000 ? value : null;
    }
    private static double ReadDuration(string? text)
    {
        double seconds = 0;
        foreach (Match match in DurationPattern.Matches(text ?? ""))
            if (double.TryParse(match.Groups["n"].Value, out var n)) seconds += n * (match.Groups["u"].Value.Length == 1 ? 3600 : 60);
        return Math.Min(seconds, 3_000_000);
    }
}
