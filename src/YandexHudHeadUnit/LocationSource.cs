using Android.Content;
using Android.Locations;
using Android.OS;

namespace YandexHudHeadUnit;

// Foreground location source for the ESP32 transport. It is created only when
// the user enables location transfer and a transport that accepts coordinates
// is connected; WiiYii never reaches this path.
internal sealed class LocationSource : Java.Lang.Object, ILocationListener
{
    private const long MinUpdateMilliseconds = 1000;
    private const float MinDistanceMeters = 0;
    private readonly Context _context;
    private readonly LocationManager? _manager;
    private string _status = "GPS: выключено";
    private HudLocation? _latest;
    private bool _started;

    internal LocationSource(Context context)
    {
        _context = context;
        _manager = context.GetSystemService(Context.LocationService) as LocationManager;
    }

    internal string Status => Volatile.Read(ref _status);
    internal HudLocation? Latest => Volatile.Read(ref _latest);

    internal bool Start()
    {
        if (_started) return true;
        if (_manager is null)
        {
            Volatile.Write(ref _status, "GPS: служба недоступна");
            return false;
        }
        bool fine = _context.CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) ==
            Android.Content.PM.Permission.Granted;
        bool coarse = _context.CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation) ==
            Android.Content.PM.Permission.Granted;
        if (!fine && !coarse)
        {
            Volatile.Write(ref _status, "GPS: нет разрешения");
            return false;
        }
        try
        {
            bool any = false;
            foreach (string provider in new[] { LocationManager.GpsProvider, LocationManager.NetworkProvider })
            {
                if (!_manager.IsProviderEnabled(provider)) continue;
                _manager.RequestLocationUpdates(provider, MinUpdateMilliseconds, MinDistanceMeters, this, Looper.MainLooper);
                any = true;
                try
                {
                    if (_manager.GetLastKnownLocation(provider) is { } last) Apply(last);
                }
                catch (Exception) { /* A provider can disappear between the two calls. */ }
            }
            if (!any)
            {
                Volatile.Write(ref _status, "GPS: провайдеры выключены");
                return false;
            }
            _started = true;
            Volatile.Write(ref _status, fine ? "GPS: ожидание сигнала" : "GPS: только примерная точность");
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _status, "GPS: " + ex.Message);
            return false;
        }
    }

    internal void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _manager?.RemoveUpdates(this); } catch (Exception) { }
        Volatile.Write(ref _latest, null);
        Volatile.Write(ref _status, "GPS: выключено");
    }

    public void OnLocationChanged(Location location) => Apply(location);

    private void Apply(Location location)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double? accuracy = location.HasAccuracy ? location.Accuracy : null;
        double? speed = location.HasSpeed ? location.Speed : null;
        double? bearing = location.HasBearing ? location.Bearing : null;
        var value = new HudLocation(location.Latitude, location.Longitude, speed, bearing, accuracy,
            location.Time > 0 ? location.Time : now);
        Volatile.Write(ref _latest, value);
        Volatile.Write(ref _status, value.IsUsable(now)
            ? "GPS: " + (accuracy is { } meters ? $"{meters:0} м" : "сигнал")
            : "GPS: слабый сигнал");
    }

    public void OnProviderDisabled(string provider) => Volatile.Write(ref _status, "GPS: провайдер выключен");

    public void OnProviderEnabled(string provider) { }

    public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }

    public void OnFlushComplete(int requestCode) { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Stop();
        base.Dispose(disposing);
    }
}
