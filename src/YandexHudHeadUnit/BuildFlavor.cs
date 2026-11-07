namespace YandexHudHeadUnit;

// One codebase produces two APKs. The head-unit build keeps its previous
// component names so an installed copy can update in place. The phone build
// gets its own namespace, application id and user-facing title.
internal static class BuildFlavor
{
#if PHONE
    internal const bool IsPhone = true;
    internal const string ComponentNamespace = "com.avashield.yandexhud.phone";
    internal const string Title = "Яндекс HUD — телефон";
    internal const string Subtitle = "Навигатор · 1.4 · телефон";
    internal const string DeviceName = "телефон";
    internal const string AppLabel = "Яндекс HUD — телефон";
#else
    internal const bool IsPhone = false;
    internal const string ComponentNamespace = "com.avashield.yandexhud.headunit";
    internal const string Title = "Яндекс HUD — магнитола";
    internal const string Subtitle = "Навигатор · 1.4 · магнитола";
    internal const string DeviceName = "магнитола";
    internal const string AppLabel = "Яндекс HUD — магнитола";
#endif

    internal const string ScreenServiceLabel = AppLabel + ": чтение навигации";
    internal const string NotificationServiceLabel = AppLabel;
}
