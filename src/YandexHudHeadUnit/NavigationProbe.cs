#if DEBUG
using Android.Content;
using System.Text;
using System.Text.Json;
using YandexNotificationBridge;

namespace YandexHudHeadUnit;

// Explicit ADB test only. Excluded from the distributable Release APK.
internal static class NavigationProbe
{
    internal static async Task RunAsync(Context context, int seconds = 25)
    {
        var path = Path.Combine(context.CacheDir!.AbsolutePath, "navigation-probe.jsonl");
        try
        {
            using var output = new StreamWriter(path, false);
            int samples = Math.Clamp(seconds, 1, 600);
            for (int i = 0; i < samples; i++)
            {
                var state = await BridgeHub.ReadAsync();
                var data = HudProtocol.Map(state, true);
                var sample = JsonSerializer.SerializeToNode(state)!;
                sample["HudDirection"] = HudProtocol.Direction(data.NextTurnDirection);
                sample["HudFrames"] = JsonSerializer.SerializeToNode(
                    HudProtocol.Frames(data).Select(Encoding.ASCII.GetString).ToArray());
                sample["ManeuverDescription"] = YandexScreenService.ManeuverDescription;
                var line = sample.ToJsonString();
                await output.WriteLineAsync(line);
                await output.FlushAsync();
                Android.Util.Log.Info("YandexHeadunitProbe", line);
                await Task.Delay(1000);
            }
            Android.Util.Log.Info("YandexHeadunitProbe", "Completed local navigation probe");
        }
        catch (Exception ex) { Android.Util.Log.Error("YandexHeadunitProbe", ex.ToString()); }
    }
}
#endif
