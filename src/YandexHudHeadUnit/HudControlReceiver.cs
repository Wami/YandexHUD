using Android.Content;

namespace YandexHudHeadUnit;

// Background automation entry point: DISCONNECT and EXIT need no UI and are not
// subject to foreground-service start restrictions. CONNECT and OPEN also work
// here, but Android 12+ may refuse to start a service or activity from a
// background broadcast; use HudControlActivity for those.
[BroadcastReceiver(Name = BuildFlavor.ComponentNamespace + ".HudControlReceiver", Exported = true)]
[IntentFilter([
    BuildFlavor.ComponentNamespace + ".CONNECT",
    BuildFlavor.ComponentNamespace + ".DISCONNECT",
    BuildFlavor.ComponentNamespace + ".EXIT",
    BuildFlavor.ComponentNamespace + ".OPEN"
])]
public sealed class HudControlReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var command = HudAutomation.Parse(context.PackageName, intent?.Action);
        if (command != HudCommand.None) HudControl.Execute(context, command);
    }
}
