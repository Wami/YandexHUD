using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using WiiYiiHudNavigator.Common.Models;
using SourceState = YandexNotificationBridge.BridgeState;

namespace YandexHudHeadUnit;

// Optional capability: transports that encode the raw source snapshot directly
// instead of the legacy flattened HudNavigationData.
internal interface IEsp32HudTransport
{
    Task SendStateAsync(SourceState state, bool lanes, CancellationToken token);
}

// BLE central for the ESP32 HUD (protocol v1). Kept separate from WiiYiiBleTransport
// so the working C1 path is not disturbed.
internal sealed class Esp32HudTransport : IHudTransport, IHudLocationTransport, IEsp32HudTransport
{
    private readonly Esp32HudBle _ble = new();
    private readonly Esp32HudEncoder _encoder = new();

    public bool Connected => _ble.Connected;
    public bool SupportsLocation => true;

    public Task ConnectAsync(Context context, string address, CancellationToken token, BluetoothDevice? scanned = null) =>
        _ble.ConnectAsync(context, address, token, scanned);

    public Task SendAsync(HudNavigationData data, CancellationToken token) =>
        Task.FromException(new NotSupportedException("ESP32 HUD uses SendStateAsync"));

    public async Task SendStateAsync(SourceState state, bool lanes, CancellationToken token)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _ble.SendAsync(_encoder.BuildState(state, lanes, now), token);
    }

    public async Task SendLocationAsync(HudLocation location, CancellationToken token) =>
        await _ble.SendAsync(_encoder.BuildPosition(location), token);

    public async Task ClearAsync(CancellationToken token) =>
        await _ble.SendAsync(_encoder.BuildIdle(), token);

    public void Dispose() => _ble.Dispose();
}

// One instance per connection attempt: late callbacks cannot complete a new session.
internal sealed class Esp32HudBle : IDisposable
{
    private readonly Callbacks _callbacks = new();
    private BluetoothGatt? _gatt;
    private BluetoothGattCharacteristic? _writer;

    internal int Mtu { get; private set; } = 23;
    internal bool Connected => _callbacks.Alive && _writer is not null;

    internal async Task ConnectAsync(Context context, string address, CancellationToken token, BluetoothDevice? scanned = null)
    {
        var adapter = BluetoothAccess.Adapter(context) ?? throw new InvalidOperationException("Bluetooth-адаптер недоступен");
        using var saved = scanned is null ? adapter.GetRemoteDevice(address) : null;
        var device = scanned ?? saved ?? throw new InvalidOperationException("Неверный адрес HUD");
        _gatt = device.ConnectGatt(context, scanned is null, _callbacks, BluetoothTransports.Le)
            ?? throw new InvalidOperationException("Не удалось начать соединение с HUD");
        await CheckAsync(_callbacks.Connection.Task, scanned is null ? 25 : 15, token, "Подключение к HUD");
        if (!_gatt.DiscoverServices()) throw new IOException("Не удалось запросить службы HUD");
        await CheckAsync(_callbacks.Discovery.Task, 10, token, "Службы HUD");

        using var serviceId = Java.Util.UUID.FromString(Esp32HudProtocolConstants.ServiceUuid);
        using var writeId = Java.Util.UUID.FromString(Esp32HudProtocolConstants.NavCharUuid);
        using var service = _gatt.GetService(serviceId);
        _writer = service?.GetCharacteristic(writeId)
            ?? throw new IOException("Устройство не предоставляет канал ESP32 HUD");
        if ((_writer.Properties & (GattProperty.Write | GattProperty.WriteNoResponse)) == 0)
            throw new IOException("Канал ESP32 HUD не разрешает запись");

        Mtu = 23;
        if (_gatt.RequestMtu(Esp32HudProtocolConstants.MtuRequest))
        {
            try { Mtu = await _callbacks.Mtu.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (TimeoutException) { Mtu = 23; }
        }
        if (Mtu < 23) Mtu = 23;
    }

    internal async Task SendAsync(byte[] message, CancellationToken token)
    {
        if (!Connected || _gatt is null || _writer is null) throw new IOException("HUD отключён");
        var chunks = Esp32HudProtocol.Fragment(message, Mtu);
        bool noResponse = (_writer.Properties & GattProperty.WriteNoResponse) != 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!_callbacks.Alive || _callbacks.WriteError is { }) throw new IOException("Связь с HUD прервана");
            var completion = noResponse ? null : new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _callbacks.Write, completion);
            var type = noResponse ? GattWriteType.NoResponse : GattWriteType.Default;
            bool accepted;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                accepted = _gatt.WriteCharacteristic(_writer, chunks[i], (int)type) == 0;
            else
            {
                _writer.WriteType = type;
                _writer.SetValue(chunks[i]);
                accepted = _gatt.WriteCharacteristic(_writer);
            }
            if (!accepted) throw new IOException("Bluetooth отклонил запись в HUD");
            if (completion is not null) await CheckAsync(completion.Task, 3, CancellationToken.None, "Запись в HUD");
            if (i + 1 < chunks.Count)
                await Task.Delay(Esp32HudProtocolConstants.FragmentGapMs, token);
        }
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
        internal readonly TaskCompletionSource<int> Mtu = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int>? Write;
        internal string? WriteError;

        internal void Fail(int status)
        {
            Alive = false;
            Connection.TrySetResult(status);
            Discovery.TrySetResult(status);
            Mtu.TrySetResult(23);
            Volatile.Read(ref Write)?.TrySetResult(status);
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (Closed) return;
            if (status == GattStatus.Success && newState == ProfileState.Connected)
            {
                Alive = true; Connection.TrySetResult(0);
            }
            else if (status != GattStatus.Success || newState == ProfileState.Disconnected)
                Fail(status == GattStatus.Success ? -1 : (int)status);
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status) =>
            Discovery.TrySetResult((int)status);

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status) =>
            Mtu.TrySetResult(status == GattStatus.Success ? mtu : 23);

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status)
        {
            if (status != GattStatus.Success) Volatile.Write(ref WriteError, status.ToString());
            Volatile.Read(ref Write)?.TrySetResult((int)status);
        }
    }
}
