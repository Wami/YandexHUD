using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Notification;

namespace YandexNotificationBridge;

#if HEADUNIT
[Service(Name = YandexHudHeadUnit.BuildFlavor.ComponentNamespace + ".YandexNotificationService", Label = YandexHudHeadUnit.BuildFlavor.NotificationServiceLabel,
#else
[Service(Name = "com.avashield.yandexnotificationbridge.YandexBridgeNotificationListenerService", Label = "Yandex Navigation Bridge",
#endif
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
    Exported = true, Enabled = true)]
[IntentFilter(["android.service.notification.NotificationListenerService"])]
public sealed class YandexBridgeNotificationListenerService : NotificationListenerService
{
    private static YandexBridgeNotificationListenerService? _instance;
    private static BridgeState _state = BridgeState.Inactive;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private long _sequence;
    public static bool Connected => _instance is not null;
    internal static BridgeState CurrentState => Volatile.Read(ref _state);

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        _instance = this;
        Refresh();
        BridgeHub.Start();
#if HEADUNIT
        YandexHudHeadUnit.ConnectionRecovery.TryStart(this, YandexHudHeadUnit.Activation.Notification);
#endif
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        base.OnNotificationPosted(sbn);
        if (IsNavigation(sbn))
        {
            Refresh();
#if HEADUNIT
            YandexHudHeadUnit.ConnectionRecovery.TryStart(this, YandexHudHeadUnit.Activation.Notification);
#endif
        }
    }
    public override void OnNotificationRemoved(StatusBarNotification? sbn)
    {
        base.OnNotificationRemoved(sbn);
        if (IsNavigation(sbn)) Refresh();
    }
    private static bool IsNavigation(StatusBarNotification? sbn) => NavigationSource.AcceptsPackage(sbn?.PackageName) && sbn?.Id == 2;

    // Verify that the notification still exists on EVERY request. A failed parser,
    // disconnected listener or removed route can never leave cached arrows active.
    internal static Task<BridgeState> ReadVerifiedAsync()
    {
        var service = _instance;
        if (service is null) return Task.FromResult(BridgeState.Inactive);
        var result = new TaskCompletionSource<BridgeState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service._handler.Post(() =>
        {
            if (_instance != service) { result.TrySetResult(BridgeState.Inactive); return; }
            service.Refresh();
            result.TrySetResult(CurrentState);
        });
        return result.Task;
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var active = GetActiveNotifications();
            try
            {
                var notification = active?.Where(IsNavigation).OrderByDescending(n => n.PostTime).FirstOrDefault();
                BridgeState value;
                if (notification is null)
                {
                    value = BridgeState.Inactive;
                }
                else
                {
                    value = NotificationReader.Read(this, notification) with { SourceTimestamp = notification.PostTime };
                }
                // Source age is independent of the socket heartbeat: an old warning
                // must not be made fresh simply because the plugin polls it again.
                if (value.CameraDistanceMeters > 0 &&
                    (value.CameraObservedAtUnixMilliseconds < now - 10000 || value.CameraObservedAtUnixMilliseconds > now + 5000))
                    value = value with { CameraDistanceMeters = 0 };
                var previous = CurrentState;
                bool changed = value.Active != previous.Active || value.Maneuver != previous.Maneuver ||
                    value.NextTurnDistanceMeters != previous.NextTurnDistanceMeters || value.Road != previous.Road ||
                    value.RemainingDistanceMeters != previous.RemainingDistanceMeters || value.RemainingTimeSeconds != previous.RemainingTimeSeconds ||
                    value.CameraDistanceMeters != previous.CameraDistanceMeters;
                if (changed) _sequence++;
                Volatile.Write(ref _state, value with
                {
                    Sequence = _sequence,
                    UpdatedAtUnixMilliseconds = changed ? now : previous.UpdatedAtUnixMilliseconds,
                    ObservedAtUnixMilliseconds = now
                });
            }
            finally { if (active is not null) foreach (var item in active) item.Dispose(); }
        }
        catch (Exception)
        {
            Volatile.Write(ref _state, BridgeState.Inactive with { Sequence = ++_sequence, ObservedAtUnixMilliseconds = now });
        }
    }

    public override void OnListenerDisconnected() { Disconnect(); base.OnListenerDisconnected(); }
    public override void OnDestroy() { Disconnect(); base.OnDestroy(); }
    private void Disconnect()
    {
        if (_instance == this) _instance = null;
        Volatile.Write(ref _state, BridgeState.Inactive);
    }
}
