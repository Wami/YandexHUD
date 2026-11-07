using Android.Bluetooth;
using Android.Content;
using WiiYiiHudNavigator.Common.Models;

namespace YandexHudHeadUnit;

// Output seam for the navigation stream. The current build uses the WiiYii
// BLE protocol. A later ESP32 HUD transport can implement the same interface
// without touching the reader or the service loop.
internal interface IHudTransport : IDisposable
{
    bool Connected { get; }
    Task ConnectAsync(Context context, string address, CancellationToken token, BluetoothDevice? scanned = null);
    Task SendAsync(HudNavigationData data, CancellationToken token);
    Task ClearAsync(CancellationToken token);
}

internal sealed class WiiYiiBleTransport : IHudTransport
{
    private readonly BleHud _hud = new();
    public bool Connected => _hud.Connected;
    public Task ConnectAsync(Context context, string address, CancellationToken token, BluetoothDevice? scanned = null) =>
        _hud.ConnectAsync(context, address, token, scanned);
    public Task SendAsync(HudNavigationData data, CancellationToken token) =>
        _hud.SendAsync(HudProtocol.Frames(data), token);
    public Task ClearAsync(CancellationToken token) =>
        _hud.SendAsync(HudProtocol.Frames(HudProtocol.Clear()), token);
    public void Dispose() => _hud.Dispose();
}
