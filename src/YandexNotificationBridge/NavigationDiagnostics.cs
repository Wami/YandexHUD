namespace YandexNotificationBridge;

// Small in-memory snapshots for the user's Copy/Save diagnostic actions.
// No screen images, coordinates, street fields, or other apps are recorded.
internal static class NavigationDiagnostics
{
    private static string _screen = "Экран маршрута ещё не прочитан";
    private static string _notification = "Уведомление маршрута ещё не прочитано";
    internal static string Screen => Volatile.Read(ref _screen);
    internal static string Notification => Volatile.Read(ref _notification);

    internal static void RecordScreen(IReadOnlyList<ScreenField> fields, BridgeState state)
    {
        if (!state.Active) return;
        var values = fields.Where(f => ScreenNavigationReader.IsCurrentArrow(f.Id) || f.Id is "text_maneuverballoon_distance" or "text_maneuverballoon_metrics" or "text_manoeuvre_balloon_distance" or "text_manoeuvre_balloon_metrics" or "exit_number_text")
            .Take(12).Select(f => $"{f.Id}: текст={Quote(f.Text)}, описание={Quote(f.Description)}");
        Volatile.Write(ref _screen, $"Последний экран маршрута: {state.SourceTimestamp}; поворот={state.Maneuver ?? "нет"}; расстояние={state.NextTurnDistanceMeters}\n" + string.Join("\n", values));
    }

    internal static BridgeState RecordNotification(BridgeState state, IEnumerable<string> layouts, long postTime)
    {
        Volatile.Write(ref _notification, $"Чтение уведомления: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}; опубликовано={postTime}; поворот={state.Maneuver ?? "нет"}; расстояние={state.NextTurnDistanceMeters}\n" + string.Join("\n", layouts.Take(3)));
        return state;
    }

    private static string Quote(string? value)
    {
        if (value is null) return "нет";
        if (value.Length > 160) value = value[..160];
        return '"' + value.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\u00a0", "[NBSP]").Replace("\u202f", "[NNBSP]") + '"';
    }
}
