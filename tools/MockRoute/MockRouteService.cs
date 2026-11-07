using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Locations.Provider;
using Android.OS;
using OperationCanceledException = System.OperationCanceledException;

namespace MockRoute;

[Service(Name = "com.avashield.mockroute.MockRouteService", Exported = true,
    ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class MockRouteService : Service
{
    internal const string ActionStart = "mockroute.START";
    internal const string ActionStop = "mockroute.STOP";
    private const string Channel = "mock_route";
    private const string Provider = LocationManager.GpsProvider;
    private CancellationTokenSource? _cancel;
    private Task? _worker;
    private LocationManager? _manager;
    internal static string Status { get; private set; } = "Ожидание";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Android.Util.Log.Info("MockRoute", "OnStartCommand " + intent?.Action);
        if (intent?.Action == ActionStop)
        {
            StopPlayback();
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        try { StartForeground(1, Notification(), ForegroundService.TypeLocation); }
        catch (Exception ex)
        {
            Android.Util.Log.Error("MockRoute", "StartForeground failed: " + ex);
            Status = "Не удалось запустить foreground: " + ex.Message;
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (_worker is null || _worker.IsCompleted) StartPlayback();
        return StartCommandResult.Sticky;
    }

    private void StartPlayback()
    {
        var (route, source) = RouteStore.Load(this);
        Android.Util.Log.Info("MockRoute", $"Route: {route?.Points.Count ?? 0} points from {source}");
        if (route is null)
        {
            Status = "Маршрут не найден. Загрузите route.json в files приложения.";
            StopSelf();
            return;
        }
        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Android.Content.PM.Permission.Granted)
        {
            Status = "Нет разрешения на геолокацию.";
            StopSelf();
            return;
        }
        try
        {
            _manager = (LocationManager)GetSystemService(LocationService)!;
            var properties = new ProviderProperties.Builder()
                .SetHasNetworkRequirement(false)
                .SetHasSatelliteRequirement(true)
                .SetHasCellRequirement(false)
                .SetHasMonetaryCost(false)
                .SetHasAltitudeSupport(true)
                .SetHasSpeedSupport(true)
                .SetHasBearingSupport(true)
                .SetPowerUsage((int)Power.High)
                .SetAccuracy((int)Accuracy.Fine)
                .Build();
            _manager.AddTestProvider(Provider, properties);
            _manager.SetTestProviderEnabled(Provider, true);
            Android.Util.Log.Info("MockRoute", "Test provider enabled");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("MockRoute", "AddTestProvider failed: " + ex);
            Status = "Не удалось включить mock GPS: " + ex.Message;
            CleanupProvider();
            StopSelf();
            return;
        }
        _cancel = new CancellationTokenSource();
        Status = $"Старт: {route.Name ?? source} · {route.Points.Count} точек";
        _worker = Task.Run(() => PlayAsync(route.Points, _cancel.Token));
    }

    private async Task PlayAsync(List<RoutePoint> points, CancellationToken token)
    {
        try
        {
            for (int i = 0; i < points.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                Publish(points[i]);
                Status = $"Точка {i + 1}/{points.Count}" +
                    (i > 0 ? $" · {points[i].Speed * 3.6:0} км/ч" : " · подготовка");
                await Task.Delay(1000, token);
            }
            // Hold the final position briefly so Navigator can finish the route.
            var last = points[^1];
            Publish(last with { Speed = 0 });
            Status = "Маршрут завершён";
            await Task.Delay(3000, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "Ошибка воспроизведения: " + ex.Message; }
        finally
        {
            CleanupProvider();
            if (!token.IsCancellationRequested)
            {
                _cancel?.Cancel();
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    try { StopForeground(StopForegroundFlags.Remove); } catch (Exception) { }
                    StopSelf();
                });
            }
        }
    }

    private void Publish(RoutePoint point)
    {
        if (_manager is null) return;
        var location = new Location(Provider)
        {
            Latitude = point.Lat,
            Longitude = point.Lon,
            Accuracy = 3,
            Altitude = 150,
            Speed = (float)point.Speed,
            Bearing = (float)point.Bearing,
            Time = Java.Lang.JavaSystem.CurrentTimeMillis(),
            ElapsedRealtimeNanos = SystemClock.ElapsedRealtimeNanos(),
        };
        _manager.SetTestProviderLocation(Provider, location);
    }

    private void StopPlayback()
    {
        _cancel?.Cancel();
        CleanupProvider();
        try { StopForeground(StopForegroundFlags.Remove); } catch (Exception) { }
        Status = "Остановлено";
    }

    private void CleanupProvider()
    {
        try { _manager?.SetTestProviderEnabled(Provider, false); } catch (Exception) { }
        try { _manager?.RemoveTestProvider(Provider); } catch (Exception) { }
        _manager = null;
    }

    private Notification Notification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Mock GPS", NotificationImportance.Low));
        using var open = new Intent(this, typeof(MainActivity));
        var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
        return new Notification.Builder(this, Channel)
            .SetContentTitle("Mock Route")
            .SetContentText("Эмуляция GPS по маршруту")
            .SetSmallIcon(Android.Resource.Drawable.IcMenuMyLocation)
            .SetContentIntent(PendingIntent.GetActivity(this, 0, open, flags))
            .SetOngoing(true)
            .Build();
    }

    public override void OnDestroy()
    {
        _cancel?.Cancel();
        CleanupProvider();
        base.OnDestroy();
    }
}
