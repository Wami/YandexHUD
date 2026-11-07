namespace YandexHudHeadUnit;

// Android-independent location snapshot used by the future ESP32 transport.
internal sealed record HudLocation(
    double Latitude,
    double Longitude,
    double? SpeedMetersPerSecond,
    double? BearingDegrees,
    double? AccuracyMeters,
    long TimestampUnixMilliseconds)
{
    internal const long MaxAgeMilliseconds = 5000;
    internal const double MaxAccuracyMeters = 100;

    internal bool IsUsable(long now)
    {
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude)) return false;
        if (Latitude is < -90 or > 90 || Longitude is < -180 or > 180) return false;
        if (TimestampUnixMilliseconds <= 0 || now < TimestampUnixMilliseconds) return false;
        if (now - TimestampUnixMilliseconds > MaxAgeMilliseconds) return false;
        if (AccuracyMeters is { } accuracy &&
            (!double.IsFinite(accuracy) || accuracy <= 0 || accuracy > MaxAccuracyMeters)) return false;
        if (SpeedMetersPerSecond is { } speed && (!double.IsFinite(speed) || speed < 0)) return false;
        if (BearingDegrees is { } bearing && (!double.IsFinite(bearing) || bearing < 0 || bearing > 360)) return false;
        return true;
    }
}
