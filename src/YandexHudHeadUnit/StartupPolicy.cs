namespace YandexHudHeadUnit;

internal static class StartupPolicy
{
    // Opening the app may launch Navigator. Returning from Navigator we started
    // must not reopen it, and explicit debug launches never autostart anything.
    internal static bool ShouldAutoStart(bool enabled, bool returningFromNavigator, bool testLaunch) =>
        enabled && !returningFromNavigator && !testLaunch;
}
