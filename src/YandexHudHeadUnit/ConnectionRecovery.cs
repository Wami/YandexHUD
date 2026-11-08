using Android.Content;
using Android.OS;

namespace YandexHudHeadUnit;

internal static class ConnectionRecovery
{
    private static long _lastAttempt = -10000;
    private static readonly object Gate = new();
    internal static void TryStart(Context context, Activation activation)
    {
        bool permission = BluetoothAccess.ConnectGranted(context);
        if (ReconnectPolicy.ShouldResume(Options.Boot, Options.Enabled, activation, Options.Address,
                HudService.Running, permission))
        {
            lock (Gate)
            {
                long now = SystemClock.ElapsedRealtime();
                if (now - _lastAttempt < 5000) return;
                _lastAttempt = now;
            }
            // App, boot and automation may re-arm a transfer stopped by the user.
            Options.Enabled = true;
            try { context.StartForegroundService(new Intent(context, typeof(HudService))); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("YandexHUD", "Autostart: " + ex.GetType().Name);
                HudService.SetStatus("Android отложил автозапуск. Откройте приложение для подключения к HUD.");
            }
            return;
        }
        // Only explicit activations explain why nothing happened; background
        // navigator updates must stay silent when the user stopped the transfer.
        if (activation is Activation.App or Activation.Boot or Activation.Automation &&
            Options.Boot && !HudService.Running && !string.IsNullOrWhiteSpace(Options.Address))
        {
            if (!permission)
                HudService.SetStatus("Автоподключение: разрешите доступ к Bluetooth в приложении.");
            else if (BluetoothAccess.Problem(context, scan: false) is { } problem)
                HudService.SetStatus("Автоподключение: " + problem);
        }
    }
}
