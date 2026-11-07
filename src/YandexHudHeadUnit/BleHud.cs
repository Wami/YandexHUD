using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace YandexHudHeadUnit;

internal static class BluetoothAccess
{
    internal static BluetoothAdapter? Adapter(Context context) => (context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;
    internal static string[] Permissions => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? [Android.Manifest.Permission.BluetoothScan, Android.Manifest.Permission.BluetoothConnect]
        : [Android.Manifest.Permission.AccessFineLocation];
    internal static bool Granted(Context context) => Permissions.All(p => context.CheckSelfPermission(p) == Permission.Granted);
    internal static bool ConnectGranted(Context context) => !OperatingSystem.IsAndroidVersionAtLeast(31) ||
        context.CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Permission.Granted;
    internal static string? Problem(Context context, bool scan = true)
    {
        if (scan ? !Granted(context) : !ConnectGranted(context)) return "Разрешите доступ к Bluetooth в приложении.";
        try
        {
            var adapter = Adapter(context);
            if (adapter is null) return "Android не предоставляет Bluetooth-адаптер этому приложению.";
            if (!adapter.IsEnabled) return "Bluetooth выключен. Включите его в настройках Android.";
            if (scan && adapter.BluetoothLeScanner is null) return "Android не предоставляет поиск Bluetooth LE.";
            if (scan && !OperatingSystem.IsAndroidVersionAtLeast(31) &&
                context.GetSystemService(Context.LocationService) is Android.Locations.LocationManager location &&
                !location.IsProviderEnabled(Android.Locations.LocationManager.GpsProvider) &&
                !location.IsProviderEnabled(Android.Locations.LocationManager.NetworkProvider))
                return "На Android 8–11 для поиска Bluetooth включите геолокацию в настройках.";
            return null;
        }
        catch (Exception ex) { return "Bluetooth: " + ex.Message; }
    }
}

internal sealed record FoundHud(string Address, string Name, BluetoothDevice Device, bool Esp32 = false, bool WiiYii = false);

internal sealed class HudScanner : ScanCallback
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FoundHud> _devices = new();
    private BluetoothLeScanner? _scanner;
    private string? _error;
    private bool _accepting;
    private string? _target;
    private TaskCompletionSource<FoundHud>? _found;
    public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
    {
        if (result?.Device is not { } device) return;
        try
        {
            var name = result.ScanRecord?.DeviceName ?? device.Name ?? "";
            bool wiiyii = result.ScanRecord?.ServiceUuids?.Any(id => string.Equals(id.ToString(), HudProtocol.ServiceUuid, StringComparison.OrdinalIgnoreCase)) == true;
            bool esp32 = result.ScanRecord?.ServiceUuids?.Any(id => string.Equals(id.ToString(), Esp32HudProtocolConstants.ServiceUuid, StringComparison.OrdinalIgnoreCase)) == true;
            var address = device.Address;
            if (address is null) return;
            lock (_gate)
            {
                if (!_accepting) return;
                if (_target is not null ? !address.Equals(_target, StringComparison.OrdinalIgnoreCase) :
                    !wiiyii && !esp32 && !name.Trim().Equals("WYHUD", StringComparison.OrdinalIgnoreCase)) return;
                var found = new FoundHud(address, string.IsNullOrWhiteSpace(name) ? (esp32 ? "ESP32 HUD" : "WiiYii HUD") : name, device, esp32, wiiyii);
                _devices[address] = found;
                _found?.TrySetResult(found);
            }
        }
        catch (Exception) { /* A disappearing advertiser must not abort scanning. */ }
    }
    public override void OnBatchScanResults(IList<ScanResult>? results)
    {
        if (results is not null) foreach (var result in results) OnScanResult(ScanCallbackType.AllMatches, result);
    }
    public override void OnScanFailed(ScanFailure errorCode)
    {
        lock (_gate) { _error = "Поиск Bluetooth: " + errorCode; _found?.TrySetException(new IOException(_error)); }
    }
    internal async Task<FoundHud[]> FindAsync(Context context, CancellationToken token)
    {
        _scanner = BluetoothAccess.Adapter(context)?.BluetoothLeScanner ?? throw new InvalidOperationException("Bluetooth LE недоступен");
        lock (_gate) { _accepting = true; _target = null; _devices.Clear(); _error = null; }
        try
        {
            using var settings = new ScanSettings.Builder().SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)?.Build();
            _scanner.StartScan(null, settings, this);
            await Task.Delay(12000, token);
            lock (_gate)
            {
                if (_error is { } error) throw new InvalidOperationException(error);
                return _devices.Values.OrderBy(d => d.Address).ToArray();
            }
        }
        finally
        {
            lock (_gate) _accepting = false;
            try { _scanner?.StopScan(this); } catch (Exception) { }
            _scanner = null;
        }
    }
    internal async Task<FoundHud?> FindSavedAsync(Context context, string address, CancellationToken token)
    {
        _scanner = BluetoothAccess.Adapter(context)?.BluetoothLeScanner ?? throw new IOException("Поиск BLE недоступен");
        lock (_gate)
        {
            _target = address; _accepting = true;
            _devices.Clear(); _error = null;
            _found = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var settings = new ScanSettings.Builder().SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)?.Build();
            _scanner.StartScan(null, settings, this);
            try { return await _found.Task.WaitAsync(TimeSpan.FromSeconds(8), token); }
            catch (TimeoutException) { return null; }
        }
        finally
        {
            lock (_gate) _accepting = false;
            try { _scanner?.StopScan(this); } catch (Exception) { }
            _scanner = null;
        }
    }
}

// One instance per connection attempt: late callbacks cannot complete a new session.
internal sealed class BleHud : IDisposable
{
    private readonly Callbacks _callbacks = new();
    private BluetoothGatt? _gatt;
    private BluetoothGattCharacteristic? _writer;
    internal bool Connected => _callbacks.Alive && _writer is not null;

    internal async Task ConnectAsync(Context context, string address, CancellationToken token, BluetoothDevice? scanned = null)
    {
        var adapter = BluetoothAccess.Adapter(context) ?? throw new InvalidOperationException("Bluetooth-адаптер недоступен");
        using var saved = scanned is null ? adapter.GetRemoteDevice(address) : null;
        var device = scanned ?? saved ?? throw new InvalidOperationException("Неверный адрес HUD");
        // A freshly advertised device retains the controller's address type.
        // When scanning is unavailable, let Android wait for the saved device.
        _gatt = device.ConnectGatt(context, scanned is null, _callbacks, BluetoothTransports.Le)
            ?? throw new InvalidOperationException("Не удалось начать соединение с HUD");
        await CheckAsync(_callbacks.Connection.Task, scanned is null ? 25 : 15, token, "Подключение к HUD");
        if (!_gatt.DiscoverServices()) throw new IOException("Не удалось запросить службы HUD");
        await CheckAsync(_callbacks.Discovery.Task, 10, token, "Службы HUD");
        using var serviceId = Java.Util.UUID.FromString(HudProtocol.ServiceUuid);
        using var writeId = Java.Util.UUID.FromString(HudProtocol.WriteUuid);
        using var service = _gatt.GetService(serviceId);
        _writer = service?.GetCharacteristic(writeId) ?? throw new IOException("Устройство не предоставляет интерфейс WiiYii HUD");
        if ((_writer.Properties & (GattProperty.Write | GattProperty.WriteNoResponse)) == 0)
            throw new IOException("Канал HUD не разрешает запись");
    }

    internal async Task SendAsync(byte[][] frames, CancellationToken token)
    {
        if (!Connected || _gatt is null || _writer is null) throw new IOException("HUD отключён");
        // The working host uses WRITE_NO_RESPONSE. Prefer it; use acknowledged
        // writes only if the peripheral does not advertise that property.
        bool noResponse = (_writer.Properties & GattProperty.WriteNoResponse) != 0;
        await HudPacketWriter.SendAsync(frames, async chunk =>
            {
                if (!_callbacks.Alive || _callbacks.WriteError is { }) throw new IOException("Связь с HUD прервана");
                var completion = noResponse ? null : new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _callbacks.Write, completion);
                var type = noResponse ? GattWriteType.NoResponse : GattWriteType.Default;
                bool accepted;
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    accepted = _gatt.WriteCharacteristic(_writer, chunk, (int)type) == 0;
                else
                {
                    _writer.WriteType = type;
                    _writer.SetValue(chunk);
                    accepted = _gatt.WriteCharacteristic(_writer);
                }
                if (!accepted) throw new IOException("Bluetooth отклонил запись в HUD");
                if (completion is not null) await CheckAsync(completion.Task, 3, CancellationToken.None, "Запись в HUD");
            }, token);
    }

    private static async Task CheckAsync(Task<int> task, int seconds, CancellationToken token, string operation)
    {
        int result;
        try { result = await task.WaitAsync(TimeSpan.FromSeconds(seconds), token); }
        catch (TimeoutException) { throw new IOException(operation + ": время ожидания истекло"); }
        if (result != 0) throw new IOException(operation + ": код Bluetooth " + result);
    }

    public void Dispose()
    {
        _callbacks.Closed = true;
        _callbacks.Fail(-1);
        var gatt = _gatt;
        _gatt = null;
        _writer?.Dispose(); _writer = null;
        try { gatt?.Disconnect(); } catch (Exception) { }
        try { gatt?.Close(); } catch (Exception) { }
        gatt?.Dispose();
    }

    private sealed class Callbacks : BluetoothGattCallback
    {
        internal volatile bool Alive;
        internal volatile bool Closed;
        internal readonly TaskCompletionSource<int> Connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> Discovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int>? Write;
        internal string? WriteError;
        internal void Fail(int status)
        {
            Alive = false;
            Connection.TrySetResult(status);
            Discovery.TrySetResult(status);
            Volatile.Read(ref Write)?.TrySetResult(status);
        }
        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (Closed) return;
            if (status == GattStatus.Success && newState == ProfileState.Connected)
            {
                Alive = true; Connection.TrySetResult(0);
            }
            else if (status != GattStatus.Success || newState == ProfileState.Disconnected) Fail(status == GattStatus.Success ? -1 : (int)status);
        }
        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status) => Discovery.TrySetResult((int)status);
        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            if (status != GattStatus.Success) Volatile.Write(ref WriteError, status.ToString());
            Volatile.Read(ref Write)?.TrySetResult((int)status);
        }
    }
}
