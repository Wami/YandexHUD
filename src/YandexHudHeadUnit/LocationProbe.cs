#if DEBUG
using Android.Content;
using System.Text.Json;

namespace YandexHudHeadUnit;

// Explicit ADB test only. Verifies the location source without the future ESP32
// transport; excluded from the distributable Release APK.
internal static class LocationProbe
{
    internal static async Task RunAsync(Context context, int seconds = 15)
    {
        string path = Path.Combine(context.CacheDir!.AbsolutePath, "location-probe.jsonl");
        using var source = new LocationSource(context);
        if (!source.Start())
        {
            Android.Util.Log.Warn("YandexLocationProbe", source.Status);
            return;
        }
        try
        {
            using var output = new StreamWriter(path, false);
            int samples = Math.Clamp(seconds, 1, 600);
            for (int i = 0; i < samples; i++)
            {
                await Task.Delay(1000);
                var location = source.Latest;
                string line = JsonSerializer.Serialize(new
                {
                    location?.Latitude,
                    location?.Longitude,
                    location?.SpeedMetersPerSecond,
                    location?.BearingDegrees,
                    location?.AccuracyMeters,
                    location?.TimestampUnixMilliseconds,
                    source.Status,
                    ObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                await output.WriteLineAsync(line);
                await output.FlushAsync();
                Android.Util.Log.Info("YandexLocationProbe", line);
            }
            Android.Util.Log.Info("YandexLocationProbe", "Completed local location probe");
        }
        catch (Exception ex) { Android.Util.Log.Error("YandexLocationProbe", ex.ToString()); }
    }
}
#endif
