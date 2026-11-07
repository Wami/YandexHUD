namespace YandexHudHeadUnit;

internal static class StartupPolicy
{
    // Auto-start only when the app was opened normally. Rotation, returning from
    // the background and explicit debug launches must not open Navigator again.
    internal static bool ShouldAutoStart(bool enabled, bool firstCreate, bool testLaunch) =>
        enabled && firstCreate && !testLaunch;
}
