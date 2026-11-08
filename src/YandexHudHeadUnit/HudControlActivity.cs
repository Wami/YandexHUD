using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace YandexHudHeadUnit;

// Invisible automation UI: CONNECT and OPEN need a visible activity so Android
// allows the foreground service / activity start. The activity finishes before
// it can draw anything.
[Activity(Name = BuildFlavor.ComponentNamespace + ".HudControlActivity", Exported = true,
    Theme = "@android:style/Theme.NoDisplay", LaunchMode = LaunchMode.SingleTask,
    ExcludeFromRecents = true, NoHistory = true)]
[IntentFilter([
    BuildFlavor.ComponentNamespace + ".CONNECT",
    BuildFlavor.ComponentNamespace + ".DISCONNECT",
    BuildFlavor.ComponentNamespace + ".EXIT",
    BuildFlavor.ComponentNamespace + ".OPEN"
])]
public sealed class HudControlActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var command = HudAutomation.Parse(PackageName, Intent?.Action);
        if (command != HudCommand.None) HudControl.Execute(this, command);
        Finish();
    }
}
