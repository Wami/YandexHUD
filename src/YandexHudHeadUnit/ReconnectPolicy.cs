namespace YandexHudHeadUnit;

// Why a reconnect is attempted. App, boot and automation can re-arm a transfer
// that the user stopped; navigator and notification events may not.
internal enum Activation
{
    App,
    Boot,
    Navigator,
    Notification,
    Automation,
}

internal static class ReconnectPolicy
{
    internal static bool ShouldResume(bool automatic, bool enabled, Activation activation, string address,
        bool running, bool permission) =>
        automatic && (enabled || activation is Activation.App or Activation.Boot or Activation.Automation) &&
        !string.IsNullOrWhiteSpace(address) && !running && permission;
    internal static bool WokeFromSleep(long previous, long now) => previous > 0 && now - previous > 10000;
    internal static int RetrySeconds(int attempts) => Math.Clamp(attempts, 1, 6) * 5;
}

// Coalesce adapter/screen/button events without losing a wakeup during a delay.
internal sealed class ReconnectSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    internal void Pulse() { try { _signal.Release(); } catch (SemaphoreFullException) { } }
    internal Task<bool> WaitAsync(int milliseconds, CancellationToken token) => _signal.WaitAsync(milliseconds, token);
    public void Dispose() => _signal.Dispose();
}
