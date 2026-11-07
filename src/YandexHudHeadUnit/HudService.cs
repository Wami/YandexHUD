using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using YandexNotificationBridge;
using OperationCanceledException = System.OperationCanceledException;

namespace YandexHudHeadUnit;

internal static class Options
{
    private static ISharedPreferences Prefs => Application.Context.GetSharedPreferences("hud", FileCreationMode.Private)!;
    internal static string Address { get => Prefs.GetString("address", "")!; set => Prefs.Edit()!.PutString("address", value)!.Commit(); }
    internal static bool Enabled { get => Prefs.GetBoolean("enabled", false); set => Prefs.Edit()!.PutBoolean("enabled", value)!.Commit(); }
    internal static bool Lanes { get => Prefs.GetBoolean("lanes", true); set => Prefs.Edit()!.PutBoolean("lanes", value)!.Apply(); }
    internal static bool VisualArrows { get => Prefs.GetBoolean("visual_arrows", false); set => Prefs.Edit()!.PutBoolean("visual_arrows", value)!.Commit(); }
    internal static bool Boot { get => Prefs.GetBoolean("boot", true); set => Prefs.Edit()!.PutBoolean("boot", value)!.Apply(); }
    internal static bool AutoStartNavigator { get => Prefs.GetBoolean("auto_start_navigator", false); set => Prefs.Edit()!.PutBoolean("auto_start_navigator", value)!.Apply(); }
    internal static bool SendLocation { get => Prefs.GetBoolean("send_location", false); set => Prefs.Edit()!.PutBoolean("send_location", value)!.Apply(); }
    internal static bool Esp32 { get => Prefs.GetBoolean("esp32", false); set => Prefs.Edit()!.PutBoolean("esp32", value)!.Apply(); }
}

[Service(Name = BuildFlavor.ComponentNamespace + ".HudService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice | ForegroundService.TypeLocation)]
public sealed class HudService : Service
{
    internal const string StopAction = "yandexhud.STOP";
    internal const string ReconnectAction = "yandexhud.RECONNECT";
    internal const string ArrowCheckAction = "yandexhud.CHECK_ARROWS";
    internal const string LocationAction = "yandexhud.LOCATION";
    private long _arrowCheckStart;
    private long _lastLocationSent;
    private const string Channel = "hud_connection";
    private CancellationTokenSource? _cancel;
    private Task? _worker;
    private readonly object _attemptGate = new();
    private CancellationTokenSource? _attempt;
    private readonly ReconnectSignal _wake = new();
    private static readonly HudScanner HudScan = new();
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _destroyed, _restart;
    private int _renewRequested;
    private WakeReceiver? _receiver;
    private static string _status = "Выберите HUD кнопкой «Найти HUD».";
    internal static string Status => Volatile.Read(ref _status);
    internal static bool Running { get; private set; }
    internal static long LastWrite { get; private set; }
    internal static bool Connected { get; private set; }
    internal static string LastDirection { get; private set; } = "—";
    internal static bool TransportSupportsLocation { get; private set; }
    internal static bool LocationActive => Volatile.Read(ref _locationActive) != 0;
    internal static string LocationStatus
    {
        get
        {
            var source = Volatile.Read(ref _location);
            return source?.Status ?? Volatile.Read(ref _locationStatus);
        }
    }
    private static string _locationStatus = "GPS: выключено";
    private static LocationSource? _location;
    private static int _locationActive;
    private static long _locationRetryAfter;
    private static readonly object LocationGate = new();

    public override void OnCreate()
    {
        base.OnCreate();
        _receiver = new WakeReceiver(this);
        var filter = new IntentFilter(Android.Bluetooth.BluetoothAdapter.ActionStateChanged);
        filter.AddAction(Intent.ActionScreenOn); filter.AddAction(Intent.ActionUserPresent);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
        else RegisterReceiver(_receiver, filter);
    }

    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction || !Options.Enabled || string.IsNullOrEmpty(Options.Address))
        {
            Interlocked.Exchange(ref _arrowCheckStart, 0);
            Options.Enabled = false;
            _cancel?.Cancel();
            if (_worker is null) StopSelf();
            return StartCommandResult.NotSticky;
        }
        try { StartForegroundCompat(location: false); }
        catch (Exception ex)
        {
            SetStatus("Не удалось запустить фоновую передачу: " + ex.Message);
            StopSelf(); return StartCommandResult.NotSticky;
        }
        if (_worker is null)
        {
            StartWorker();
        }
        else if (_worker.IsCompleted || _cancel?.IsCancellationRequested == true) _restart = true;
        else if (intent?.Action == ReconnectAction) Reconnect();
        else if (intent?.Action == LocationAction)
        {
            if (Options.SendLocation && TransportSupportsLocation) StartLocation();
            else StopLocation();
        }
        else if (intent?.Action == ArrowCheckAction && Connected)
        {
            Interlocked.Exchange(ref _arrowCheckStart, SystemClock.ElapsedRealtime());
            _wake.Pulse();
        }
        return StartCommandResult.Sticky;
    }

    private void StartForegroundCompat(bool location)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var type = ForegroundService.TypeConnectedDevice;
            if (location) type |= ForegroundService.TypeLocation;
            StartForeground(1, Notification(), type);
        }
        else StartForeground(1, Notification());
    }

    private void RefreshForegroundType()
    {
        _main.Post(() =>
        {
            if (_destroyed) return;
            try { StartForegroundCompat(LocationActive); }
            catch (Exception ex) { SetLocationStatus("GPS: foreground: " + ex.Message); }
        });
    }

    private void StartLocation()
    {
        lock (LocationGate)
        {
            if (LocationActive) return;
            if (SystemClock.ElapsedRealtime() < _locationRetryAfter) return;
            var source = _location ??= new LocationSource(this);
            if (!source.Start())
            {
                _locationRetryAfter = SystemClock.ElapsedRealtime() + 30000;
                SetLocationStatus(source.Status);
                return;
            }
            _locationRetryAfter = 0;
            Volatile.Write(ref _locationActive, 1);
            SetLocationStatus(source.Status);
        }
        RefreshForegroundType();
    }

    private void StopLocation()
    {
        bool wasActive = LocationActive;
        lock (LocationGate)
        {
            Volatile.Write(ref _locationActive, 0);
            var source = _location;
            _location = null;
            try { source?.Dispose(); } catch (Exception) { }
            _locationRetryAfter = 0;
            SetLocationStatus("GPS: выключено");
        }
        if (wasActive) RefreshForegroundType();
    }

    internal static void SetLocationStatus(string status) => Volatile.Write(ref _locationStatus, status);

    private void StartWorker()
    {
        _cancel?.Dispose(); _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Running = true; _restart = false;
        _worker = Task.Run(() => RunAsync(token));
    }

    private void Reconnect()
    {
        Interlocked.Exchange(ref _arrowCheckStart, 0);
        Interlocked.Exchange(ref _renewRequested, 1);
        lock (_attemptGate) _attempt?.Cancel();
        _wake.Pulse();
    }

    private Notification Notification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Связь с HUD", NotificationImportance.Low));
        using var open = new Intent(this, typeof(MainActivity));
        using var stop = new Intent(this, typeof(HudService)).SetAction(StopAction);
        var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
        return new Notification.Builder(this, Channel)
            .SetContentTitle(BuildFlavor.Title)!
            .SetContentText("Передача навигации работает в фоне")!
            .SetSmallIcon(Resource.Drawable.hud_icon)!
            .SetContentIntent(PendingIntent.GetActivity(this, 0, open, flags))!
            .AddAction(new Notification.Action.Builder(null, "Остановить", PendingIntent.GetService(this, 1, stop, flags)).Build())!
            .SetOngoing(true)!.Build();
    }

    internal static void SetStatus(string status) => Volatile.Write(ref _status, status);
    private async Task RunAsync(CancellationToken token)
    {
        using var readCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reader = ReadLoopAsync(readCancel.Token);
        IHudTransport? connection = null;
        try
        {
            int retry = 0;
            long previousCycle = 0;
            while (!token.IsCancellationRequested)
            {
                int delay = 0;
                if (!Options.SendLocation || !TransportSupportsLocation)
                {
                    if (LocationActive) StopLocation();
                }
                else if (!LocationActive) StartLocation();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_attemptGate) _attempt = attempt;
                try
                {
                    long elapsed = SystemClock.ElapsedRealtime();
                    bool renew = Interlocked.Exchange(ref _renewRequested, 0) != 0;
                    if (connection is not null && (renew || ReconnectPolicy.WokeFromSleep(previousCycle, elapsed)))
                    {
                        Interlocked.Exchange(ref _arrowCheckStart, 0);
                        // Some head units keep GATT marked connected across sleep.
                        connection.Dispose(); connection = null; Connected = false;
                        TransportSupportsLocation = false; StopLocation();
                    }
                    previousCycle = elapsed;
                    if (connection?.Connected != true)
                    {
                        Interlocked.Exchange(ref _arrowCheckStart, 0);
                        connection?.Dispose(); connection = null;
                        Connected = false;
                        TransportSupportsLocation = false; StopLocation();
                        if (BluetoothAccess.Problem(this, scan: false) is { } problem) throw new IOException(problem);
                        FoundHud? found = null;
                        if (BluetoothAccess.Problem(this) is null)
                        {
                            SetStatus("Ищу сохранённый HUD…");
                            var scanner = HudScan;
                            try { found = await scanner.FindSavedAsync(this, Options.Address, attempt.Token); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { Android.Util.Log.Warn("YandexHUD", "Saved scan: " + ex.Message); }
                        }
                        if (found is { Esp32: true }) Options.Esp32 = true;
                        else if (found is { WiiYii: true }) Options.Esp32 = false;
                        SetStatus("Подключение к HUD…");
                        connection = Options.Esp32 ? new Esp32HudTransport() : new WiiYiiBleTransport();
                        await connection.ConnectAsync(this, Options.Address, attempt.Token, found?.Device);
                        retry = 0; Connected = true;
                        TransportSupportsLocation = connection is IHudLocationTransport { SupportsLocation: true };
                        if (Options.SendLocation && TransportSupportsLocation) StartLocation();
                        else if (LocationActive) StopLocation();
                    }
                    // Read the latest snapshot only after connection/backoff completes.
                    // There is no queue of old turns waiting for Bluetooth.
                    var cycle = System.Diagnostics.Stopwatch.StartNew();
                    long checkStart = Interlocked.Read(ref _arrowCheckStart);
                    var check = checkStart > 0 ? HudArrowCheck.At(SystemClock.ElapsedRealtime() - checkStart) : null;
                    if (connection is IEsp32HudTransport esp32)
                    {
                        await esp32.SendStateAsync(BridgeHub.Current, Options.Lanes, attempt.Token);
                        LastDirection = "ESP32 / " + (BridgeHub.Current.Maneuver ?? "—");
                        SetStatus("ESP32 HUD подключён · данные передаются");
                    }
                    else
                    {
                        var data = check ?? HudProtocol.Map(BridgeHub.Current, Options.Lanes);
                        await connection.SendAsync(data, attempt.Token);
                        LastDirection = (data.NextTurnDirection?.ToString() ?? "—") + " / " + HudProtocol.Direction(data.NextTurnDirection);
                        SetStatus(check is null ? "HUD подключён · данные передаются" : "Проверка стрелок HUD · " + check.NextTurnRoadName);
                    }
                    if (Options.SendLocation && TransportSupportsLocation &&
                        connection is IHudLocationTransport locationTransport)
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (now - _lastLocationSent >= 1000 && _location?.Latest is { } location && location.IsUsable(now))
                        {
                            try
                            {
                                await locationTransport.SendLocationAsync(location, attempt.Token);
                                _lastLocationSent = now;
                                SetLocationStatus("GPS: " + (location.AccuracyMeters is { } meters ? $"{meters:0} м" : "передано"));
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { SetLocationStatus("GPS: " + ex.Message); }
                        }
                    }
                    LastWrite = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    // Do not count GATT discovery/scan as sleep on the next cycle.
                    previousCycle = SystemClock.ElapsedRealtime();
                    delay = Math.Max(50, 250 - (int)cycle.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (OperationCanceledException)
                {
                    connection?.Dispose(); connection = null; Connected = false;
                    TransportSupportsLocation = false; StopLocation();
                    SetStatus("Восстанавливаю соединение с сохранённым HUD…");
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _arrowCheckStart, 0);
                    connection?.Dispose(); connection = null; Connected = false;
                    TransportSupportsLocation = false; StopLocation();
                    int seconds = ReconnectPolicy.RetrySeconds(++retry);
                    SetStatus(ex.Message + $"\nПовтор подключения через {seconds} с.");
                    delay = seconds * 1000;
                }
                finally { lock (_attemptGate) if (_attempt == attempt) _attempt = null; }
                if (delay > 0) await _wake.WaitAsync(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus("Передача: " + ex.Message); }
        finally
        {
            readCancel.Cancel();
            TransportSupportsLocation = false; StopLocation();
            // Best-effort explicit reset, then close even if the peripheral vanished.
            if (connection?.Connected == true)
            {
                using var clear = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await connection.ClearAsync(clear.Token); } catch (Exception) { }
            }
            connection?.Dispose();
            try { await reader; } catch (OperationCanceledException) { }
            _main.Post(() =>
            {
                if (_destroyed) return;
                _worker = null; Connected = false;
                if (_restart && Options.Enabled) { StartWorker(); return; }
                Running = false;
                SetStatus("Передача остановлена");
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            });
        }
    }

    private static async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await BridgeHub.ReadAsync(); }
            catch (Exception ex) { Android.Util.Log.Warn("YandexHUD", "Reader: " + ex.GetType().Name); }
            await Task.Delay(100, token);
        }
    }

    public override void OnDestroy()
    {
        _destroyed = true; _cancel?.Cancel(); Running = false; Connected = false;
        TransportSupportsLocation = false; StopLocation();
        if (_receiver is not null) { try { UnregisterReceiver(_receiver); } catch (Exception) { } }
        base.OnDestroy();
    }

    private sealed class WakeReceiver(HudService service) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == Android.Bluetooth.BluetoothAdapter.ActionStateChanged) service.Reconnect();
            else if (intent?.Action is Intent.ActionScreenOn or Intent.ActionUserPresent) service._wake.Pulse();
        }
    }
}

[BroadcastReceiver(Name = BuildFlavor.ComponentNamespace + ".BootReceiver", Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null && intent?.Action is Intent.ActionBootCompleted or Intent.ActionMyPackageReplaced)
            ConnectionRecovery.TryStart(context);
    }
}
