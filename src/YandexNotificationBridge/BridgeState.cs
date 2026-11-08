namespace YandexNotificationBridge;

internal sealed record BridgeState(bool Active, string? Maneuver, double NextTurnDistanceMeters,
    string? Road, double RemainingDistanceMeters, double RemainingTimeSeconds,
    long UpdatedAtUnixMilliseconds, long Sequence, long ObservedAtUnixMilliseconds,
    int ProtocolVersion = 5, double CameraDistanceMeters = 0, long CameraObservedAtUnixMilliseconds = 0,
    string Source = "notification", string Mode = "route", string? CurrentRoad = null,
    int SpeedLimitKph = 0, string? RoadEvent = null, double RoadEventDistanceMeters = 0,
    bool ScreenReaderConnected = false, long SourceTimestamp = 0,
    string? Lanes = null, long LanesObservedAtUnixMilliseconds = 0,
    int RoundaboutExit = 0)
{
    public static BridgeState Inactive => new(false, null, 0, null, 0, 0, 0, 0, 0, Mode: "idle");
}
