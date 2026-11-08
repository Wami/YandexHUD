using Android.Content;

namespace YandexHudHeadUnit;

// Shared implementation for the in-app Stop button and the automation entry
// points. Keeping it in one place makes "stop", "disconnect" and "exit" behave
// identically everywhere.
internal static class HudControl
{
    internal static void Execute(Context context, HudCommand command)
    {
        switch (command)
        {
            case HudCommand.Connect:
                Connect(context);
                break;
            case HudCommand.Disconnect:
                Stop(context, close: false);
                break;
            case HudCommand.Exit:
                Stop(context, close: true);
                break;
            case HudCommand.Open:
                context.StartActivity(new Intent(context, typeof(MainActivity))
                    .AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop));
                break;
        }
    }

    private static void Connect(Context context)
    {
        Options.Enabled = true;
        if (HudService.Running)
            context.StartService(new Intent(context, typeof(HudService)).SetAction(HudService.ReconnectAction));
        else
            ConnectionRecovery.TryStart(context, Activation.Automation);
    }

    internal static void Stop(Context context, bool close)
    {
        Options.Enabled = false;
        if (HudService.Running)
            context.StartService(new Intent(context, typeof(HudService))
                .SetAction(close ? HudService.ExitAction : HudService.StopAction));
        if (close) MainActivity.CloseFromAutomation();
    }
}
