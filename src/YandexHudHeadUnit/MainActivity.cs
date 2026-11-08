using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using YandexNotificationBridge;
using OperationCanceledException = System.OperationCanceledException;

namespace YandexHudHeadUnit;

[Activity(Name = BuildFlavor.ComponentNamespace + ".MainActivity", Label = BuildFlavor.AppLabel, MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private TextView _connection = null!, _navigation = null!, _permissions = null!, _selected = null!;
    private Button _scanButton = null!, _startButton = null!, _stopButton = null!, _testButton = null!;
    private Switch _esp32Switch = null!;
    private static MainActivity? _current;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private bool _visible, _reading;
    private bool _navigatorReturnPending;
    private int _generation;
    private CancellationTokenSource? _scanCancel;
    private static readonly HudScanner HudScan = new();
    private string? _uiMessage;
    private string? _pendingDiagnostic;
    private static readonly Color Background = Color.Rgb(15, 25, 38);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _pendingDiagnostic = savedInstanceState?.GetString("pending_diagnostic");
        Window?.SetStatusBarColor(Background);
        Window?.SetNavigationBarColor(Background);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) Window?.SetDecorFitsSystemWindows(false);
        BuildUi();
        _current = this;
#if DEBUG
        // Explicit local test launch only. Fixtures neither post notifications nor transmit to HUD.
        if (Intent?.GetBooleanExtra("reader_self_test", false) == true) CameraReaderSelfTest.Run(this);
        if (Intent?.GetBooleanExtra("probe_navigation", false) == true)
        {
            int seconds = Intent.GetIntExtra("probe_seconds", 60);
            _ = NavigationProbe.RunAsync(this, seconds);
        }
        if (Intent?.GetBooleanExtra("probe_location", false) == true)
        {
            int seconds = Intent.GetIntExtra("probe_seconds", 15);
            _ = LocationProbe.RunAsync(this, seconds);
        }
        YandexScreenService.IgnoreDescriptionsForProbe = Intent?.GetBooleanExtra("probe_image_only", false) == true;
#endif
    }

    private bool IsTestLaunch() => Intent?.GetBooleanExtra("probe_navigation", false) == true ||
        Intent?.GetBooleanExtra("probe_location", false) == true ||
        Intent?.GetBooleanExtra("probe_image_only", false) == true ||
        Intent?.GetBooleanExtra("reader_self_test", false) == true;

    private void TryAutoStartNavigator() =>
        _handler.PostDelayed(() =>
        {
            if (_visible && !IsFinishing && !IsDestroyed) OpenNavigator();
        }, 400);

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);
    private LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };
    private void BuildUi()
    {
        var page = Column(); page.SetPadding(Dp(20), Dp(16), Dp(20), Dp(20)); page.SetBackgroundColor(Background);
        page.AddView(Text(BuildFlavor.Title, 27));
        page.AddView(Text(BuildFlavor.Subtitle, 14, Color.Rgb(155, 178, 196)));
        bool wide = Resources!.Configuration!.ScreenWidthDp >= 700;
        var body = new LinearLayout(this) { Orientation = wide ? Orientation.Horizontal : Orientation.Vertical };
        var state = Column(); var actions = Column();
        state.SetPadding(0, Dp(14), Dp(wide ? 24 : 0), Dp(14)); actions.SetPadding(0, Dp(14), 0, 0);
        if (wide)
        {
            body.AddView(state, new LinearLayout.LayoutParams(0, -2, 1));
            body.AddView(actions, new LinearLayout.LayoutParams(0, -2, 1));
        }
        else { body.AddView(state); body.AddView(actions); }
        _connection = Text("", 18, Color.Rgb(102, 235, 206)); state.AddView(_connection);
        _navigation = Text("", 23); _navigation.SetPadding(0, Dp(18), 0, Dp(18)); state.AddView(_navigation);
        _permissions = Text("", 15); state.AddView(_permissions);
        _selected = Text("", 13, Color.Rgb(155, 178, 196)); state.AddView(_selected);
        Button(actions, "Открыть Яндекс Навигатор", OpenNavigator);
        Section(actions, "HUD");
        _scanButton = Button(actions, "Найти HUD", RequestScan);
        _startButton = Button(actions, "Подключить сохранённый HUD", StartHud);
        _stopButton = Button(actions, "Отключить HUD", StopHud);
        Button(actions, "Стоп — закрыть приложение", StopAndClose);
        _testButton = Button(actions, "Проверить стрелки HUD", CheckArrows);
        _esp32Switch = Toggle(actions, "ESP32 HUD (протокол v1)", Options.Esp32, value => Options.Esp32 = value);
        Section(actions, "Разрешения Навигатора");
        Button(actions, "1. Разрешить чтение навигации", () => OpenSettings(Settings.ActionAccessibilitySettings));
        Button(actions, "2. Доступ к уведомлениям Навигатора", () => OpenSettings(Settings.ActionNotificationListenerSettings));
        Button(actions, "3. Уведомления самого Навигатора", () =>
            StartActivity(new Intent(Settings.ActionAppNotificationSettings).PutExtra(Settings.ExtraAppPackage, NavigationSource.PackageName)));
        Button(actions, "Стрелки с экрана Навигатора", ConfigureVisualArrows);
        Section(actions, "Настройки");
        Toggle(actions, "Показывать доступные полосы", Options.Lanes, value => Options.Lanes = value);
        Toggle(actions, "Автоматически подключаться к HUD", Options.Boot, value =>
        { Options.Boot = value; if (value) ConnectionRecovery.TryStart(this, Activation.App); });
        Toggle(actions, "Запускать Навигатор при открытии приложения", Options.AutoStartNavigator, value => Options.AutoStartNavigator = value);
        Toggle(actions, "Передавать местоположение на HUD (ESP32)", Options.SendLocation, SetLocationTransfer);
        Button(actions, "Энергосбережение приложения", OpenBatterySettings);
        Section(actions, "Диагностика");
#if DEBUG
        Button(actions, "Записать навигацию 60 с", ProbeNavigation);
#endif
        Button(actions, "Скопировать диагностику", CopyDiagnostics);
        Button(actions, "Сохранить диагностику в файл", SaveDiagnostics);
        Button(actions, "Как установить и пользоваться", Help);
        page.AddView(body);
        var scroll = new ScrollView(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            scroll.SetFitsSystemWindows(false);
            ApplyInsets(scroll);
        }
        else scroll.SetFitsSystemWindows(true);
        scroll.AddView(page); SetContentView(scroll);
    }

    private void Section(LinearLayout parent, string title)
    {
        var view = Text(title, 15, Color.Rgb(155, 178, 196));
        view.SetPadding(0, Dp(18), 0, 0);
        parent.AddView(view);
    }

    private void ApplyInsets(View view)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;
        view.SetOnApplyWindowInsetsListener(new SystemBarInsets());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private sealed class SystemBarInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }

    private TextView Text(string text, float size, Color? color = null)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(color ?? Color.White); return view;
    }
    private Button Button(LinearLayout parent, string label, Action action)
    {
        var button = new Button(this) { Text = label, TextSize = 15 };
        button.SetAllCaps(false); button.SetTextColor(Color.White); button.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Color.Rgb(34, 65, 87));
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(6) });
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Message(ex.Message); } };
        return button;
    }
    private Switch Toggle(LinearLayout parent, string label, bool initial, Action<bool> update)
    {
        var toggle = new Switch(this) { Text = label, Checked = initial, TextSize = 15 };
        toggle.SetTextColor(Color.White); toggle.SetPadding(0, Dp(14), 0, Dp(14));
        toggle.CheckedChange += (_, e) => update(e.IsChecked); parent.AddView(toggle);
        return toggle;
    }
    private void Message(string message)
    {
        _uiMessage = message;
        if (_connection is not null) _connection.Text = message;
    }
    private void OpenSettings(string action)
    {
        try { StartActivity(new Intent(action)); }
        catch (ActivityNotFoundException) { StartActivity(new Intent(Settings.ActionSettings)); }
    }
    private void OpenNavigator()
    {
        _navigatorReturnPending = true;
        try
        {
            var launch = PackageManager?.GetLaunchIntentForPackage(NavigationSource.PackageName);
            if (launch is not null) StartActivity(launch);
            else new AlertDialog.Builder(this).SetTitle("Нужен Яндекс Навигатор")!.SetMessage("Установите отдельное приложение «Яндекс Навигатор» на " + BuildFlavor.DeviceName + " из магазина приложений. Эта сборка получает данные из Навигатора.")!.SetPositiveButton("Понятно", (_, _) => { })!.Show();
        }
        catch (Exception ex) { Message("Не удалось открыть Навигатор: " + ex.Message); }
    }

    private void OpenBatterySettings()
    {
        // Prefer the system battery-optimization list. Some OEM builds do not
        // expose it; fall back to the app card, where battery settings live.
        try { StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings)); }
        catch (ActivityNotFoundException)
        {
            try
            {
                StartActivity(new Intent(Settings.ActionApplicationDetailsSettings)
                    .SetData(Android.Net.Uri.Parse("package:" + PackageName)));
            }
            catch (ActivityNotFoundException) { OpenSettings(Settings.ActionSettings); }
        }
    }

    private bool HasLocationPermission() =>
        CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted ||
        CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted;

    private void SetLocationTransfer(bool value)
    {
        Options.SendLocation = value;
        if (!value)
        {
            if (HudService.Running) StartService(new Intent(this, typeof(HudService)).SetAction(HudService.LocationAction));
            return;
        }
        if (!HasLocationPermission())
        {
            RequestPermissions([Android.Manifest.Permission.AccessFineLocation,
                Android.Manifest.Permission.AccessCoarseLocation], 14);
            return;
        }
        if (HudService.Running) StartService(new Intent(this, typeof(HudService)).SetAction(HudService.LocationAction));
        if (!HudService.TransportSupportsLocation)
            Toast.MakeText(this, "Координаты будут передаваться после подключения ESP32 HUD.", ToastLength.Short)?.Show();
    }

#if DEBUG
    private void ProbeNavigation()
    {
        _uiMessage = "Запись навигации 60 секунд…";
        _ = NavigationProbe.RunAsync(this, 60);
    }
#endif
    private void RequestScan()
    {
        if (_scanCancel is not null) return;
        if (HudService.Running) { Message("Сначала остановите передачу, чтобы выбрать другой HUD."); return; }
        if (!BluetoothAccess.Granted(this)) { RequestPermissions(BluetoothAccess.Permissions, 11); return; }
        if (BluetoothAccess.Problem(this) is { } problem) { Message(problem); return; }
        _ = FindHudAsync();
    }
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 11)
        {
            if (BluetoothAccess.Granted(this)) RequestScan();
            else Message("Доступ к Bluetooth не разрешён. Его можно включить в настройках приложения.");
        }
        if (requestCode == 12) StartHud();
        if (requestCode == 13)
        {
            if (BluetoothAccess.ConnectGranted(this)) StartHud();
            else Message("Доступ к Bluetooth не разрешён. Его можно включить в настройках приложения.");
        }
        if (requestCode == 14)
        {
            if (HasLocationPermission())
            {
                if (HudService.Running) StartService(new Intent(this, typeof(HudService)).SetAction(HudService.LocationAction));
                if (!HudService.TransportSupportsLocation)
                    Toast.MakeText(this, "Координаты будут передаваться после подключения ESP32 HUD.", ToastLength.Short)?.Show();
            }
            else
            {
                Options.SendLocation = false;
                BuildUi();
                Message("Доступ к геолокации не разрешён. Передача местоположения выключена.");
            }
        }
    }
    private async Task FindHudAsync()
    {
        _scanCancel = new CancellationTokenSource(); _scanButton.Enabled = false;
        var scan = HudScan;
        try
        {
            Message("Поиск HUD — 12 секунд…");
            var found = await scan.FindAsync(this, _scanCancel.Token);
            if (!_visible) return;
            if (found.Length == 0) { Message("HUD не найден. Включите HUD и отключите его от другого приложения или устройства, затем повторите поиск."); return; }
            Message($"Найдено HUD: {found.Length}. Выберите своё устройство.");
            new AlertDialog.Builder(this).SetTitle("Подключить HUD")!
                .SetItems(found.Select(h => h.Name + " · " + h.Address).ToArray(), (_, e) =>
                {
                    Options.Address = found[e.Which].Address;
                    Options.Esp32 = found[e.Which].Esp32;
                    if (_esp32Switch is not null) _esp32Switch.Checked = Options.Esp32;
                    StartHud();
                })!.SetNegativeButton("Отмена", (_, _) => { })!.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message(ex.Message); }
        finally { _scanCancel.Dispose(); _scanCancel = null; _scanButton.Enabled = true; }
    }
    private bool _askedNotifications;
    private void StartHud()
    {
        if (string.IsNullOrEmpty(Options.Address)) { RequestScan(); return; }
        if (!BluetoothAccess.ConnectGranted(this)) { RequestPermissions(BluetoothAccess.Permissions, 13); return; }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && !_askedNotifications && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            _askedNotifications = true;
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 12); return;
        }
        Options.Enabled = true; _uiMessage = null;
        // Keep retrying if the adapter/HUD has not powered up yet. This button
        // also interrupts a stuck attempt rather than remaining disabled forever.
        StartForegroundService(new Intent(this, typeof(HudService)).SetAction(HudService.ReconnectAction));
    }
    private void StopHud()
    {
        _uiMessage = null;
        HudControl.Stop(this, close: false);
    }
    private void StopAndClose() => HudControl.Stop(this, close: true);
    internal static void CloseFromAutomation()
    {
        var activity = _current;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed) return;
        activity.RunOnUiThread(() =>
        {
            try
            {
                // A task may hold more than one instance after launcher/adb
                // launches, so finish the whole affinity and then the task.
                activity.FinishAffinity();
                activity.FinishAndRemoveTask();
            }
            catch (Exception) { activity.Finish(); }
        });
    }
    protected override void OnDestroy()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnDestroy();
    }
    private void CheckArrows()
    {
        if (!HudService.Connected) { Message("Сначала подключите HUD."); return; }
        new AlertDialog.Builder(this).SetTitle("Проверка на стоящей машине")!
            .SetMessage("На 12 секунд на проекции появятся тестовые стрелки: налево, направо и два разворота. Затем вернутся актуальные данные Навигатора. Запускайте проверку только во время стоянки.")!
            .SetPositiveButton("Проверить", (_, _) =>
            {
                _uiMessage = null;
                StartForegroundService(new Intent(this, typeof(HudService)).SetAction(HudService.ArrowCheckAction));
            })!.SetNegativeButton("Отмена", (_, _) => { })!.Show();
    }

    private void ConfigureVisualArrows()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        { Message("Распознавание экранного значка требует Android 11+. Чтение описаний и уведомлений продолжает работать."); return; }
        new AlertDialog.Builder(this).SetTitle("Стрелки при открытом Навигаторе")!
            .SetMessage("Если Навигатор рисует стрелку без текстового описания, приложение может распознавать её изображение. Android предоставляет временный кадр открытого Навигатора; из него анализируется только значок текущего поворота. Кадры не сохраняются и никуда не отправляются. При переключении на другое приложение чтение изображения прекращается.\n\nПосле обновления APK выключите и снова включите нашу службу чтения навигации в специальных возможностях, чтобы Android применил новый доступ.\n\nСейчас: " + (Options.VisualArrows ? "включено" : "выключено"))!
            .SetPositiveButton("Включить", (_, _) =>
            {
                Options.VisualArrows = true;
                ForegroundArrowReader.Report("Включено; ожидаю значок без описания");
                OpenSettings(Settings.ActionAccessibilitySettings);
            })!.SetNeutralButton("Выключить", (_, _) =>
            { Options.VisualArrows = false; ForegroundArrowReader.Report("Выключено"); })!
            .SetNegativeButton("Закрыть", (_, _) => { })!.Show();
    }

    protected override void OnResume()
    {
        base.OnResume(); _visible = true; _generation++;
        ConnectionRecovery.TryStart(this, Activation.App);
        // Autostart works both on a cold start and when the system only resumes
        // an existing task (launcher tap, return from the background).
        bool returningFromNavigator = _navigatorReturnPending;
        _navigatorReturnPending = false;
        if (StartupPolicy.ShouldAutoStart(Options.AutoStartNavigator, returningFromNavigator, IsTestLaunch()))
            TryAutoStartNavigator();
        Tick();
    }
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
        Tick();
    }
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig); BuildUi();
    }
    protected override void OnPause()
    {
        _visible = false; _generation++; _scanCancel?.Cancel(); _handler.RemoveCallbacksAndMessages(null); base.OnPause();
    }
    private async void Tick()
    {
        if (!_visible) return;
        int generation = _generation;
        if (!HudService.Running && !_reading)
        {
            _reading = true;
            try { await BridgeHub.ReadAsync(); } catch (Exception) { }
            finally { _reading = false; }
        }
        if (!_visible || generation != _generation) return;
        _connection.Text = _uiMessage ?? HudService.Status;
        var s = BridgeHub.Current;
        var mapped = HudProtocol.Map(s, Options.Lanes);
        string turn = s.Active ? Maneuver(s.Maneuver) + (s.NextTurnDistanceMeters > 0 ? $" · {s.NextTurnDistanceMeters:0} м" : "") : s.Mode == "free" ? "Обзор дороги без маршрута" : "Ожидание навигации";
        _navigation.Text = turn + ((s.Road ?? s.CurrentRoad) is { } road ? "\n" + road : "") +
            (s.Active && s.Maneuver is null ? "\nНаправление поворота пока не прочитано" : "") +
            (s.Active ? $"\nОсталось {s.RemainingDistanceMeters / 1000:0.#} км · {Math.Ceiling(s.RemainingTimeSeconds / 60):0} мин" : "") +
            (mapped.CameraDistance > 0 ? $"\nКамера через {mapped.CameraDistance:0} м" : "") +
            (s.SpeedLimitKph > 0 ? $"\nОграничение {s.SpeedLimitKph} км/ч" : "") +
            (s.RoadEvent is { } warning ? "\n" + warning : "") +
            (s.Lanes is { } lanes && mapped.LaneGuidance?.Lanes.Any(l => l.IsRecommended) == true ? "\nПолосы: " + LaneText(lanes) : "");
        _permissions.Text = (YandexScreenService.Connected ? "✓ Чтение экрана Навигатора подключено" : "○ Включите чтение навигации") +
            (Options.VisualArrows ? "\nРаспознавание экранной стрелки включено" : "\nЭкранная стрелка без описания: включите «Стрелки с экрана Навигатора»") +
            (YandexBridgeNotificationListenerService.Connected ? "\n✓ Доступ к уведомлениям подключён" : "\n○ Разрешите доступ к уведомлениям") +
            (NavigatorCanNotify() ? "" : "\n○ Разрешите уведомления самому Навигатору для работы в фоне") +
            (Options.SendLocation ? "\n" + (!HasLocationPermission() ? "GPS: нет разрешения" :
                HudService.TransportSupportsLocation ? HudService.LocationStatus : "GPS: ожидает ESP32 HUD") : "") +
            (s.Mode != "idle" ? "\nИсточник: " + (s.Source == "screen" ? "экран Навигатора" : "уведомление Навигатора") : "");
        _selected.Text = string.IsNullOrEmpty(Options.Address) ? "\nHUD ещё не выбран" : "\nСохранённый HUD: " + Options.Address;
        _scanButton.Enabled = _scanCancel is null && !HudService.Running;
        _startButton.Enabled = _scanCancel is null;
        _startButton.Text = HudService.Running ? "Переподключить сохранённый HUD" : "Подключить сохранённый HUD";
        _stopButton.Enabled = HudService.Running;
        _testButton.Enabled = HudService.Connected;
        _handler.PostDelayed(Tick, 1000);
    }
    private static string Maneuver(string? value) => value switch
    {
        "Left" or "LeftSharp" => "← Налево", "Right" or "RightSharp" => "→ Направо",
        "LeftWide" or "LeftFork" => "↖ Левее", "RightWide" or "RightFork" => "↗ Правее",
        "UTurnLeft" => "↶ Разворот налево", "UTurnRight" => "↷ Разворот направо", "Straight" => "↑ Прямо",
        "Finish" => "Финиш", "Rerouting" => "Перестроение маршрута", _ => "Маршрут"
    };
    private bool NavigatorCanNotify() => !OperatingSystem.IsAndroidVersionAtLeast(33) ||
        PackageManager?.CheckPermission(Android.Manifest.Permission.PostNotifications, NavigationSource.PackageName) == Permission.Granted;
    private static string LaneText(string lanes) => string.Join("  ", lanes.Split(',').Select(item =>
    {
        string arrow = item.TrimEnd('!') switch { "L" => "←", "R" => "→", "S" => "↑", "U" => "↶", "SR" => "↗", _ => "?" };
        return item.EndsWith('!') ? "[" + arrow + "]" : arrow;
    }));
    private void Help() => new AlertDialog.Builder(this).SetTitle(BuildFlavor.Title)!
        .SetMessage("1. Установите Яндекс Навигатор на " + BuildFlavor.DeviceName + ".\n\n2. Разрешите чтение навигации и доступ к уведомлениям для «" + BuildFlavor.Title + "». Если Android блокирует пункт: Настройки → Приложения → " + BuildFlavor.Title + " → ⋮ → Разрешить ограниченные настройки.\n\n3. Включите HUD, при необходимости отключите его от другого приложения, нажмите «Найти HUD» и выберите своё устройство.\n\n4. Откройте Навигатор: используйте обзор дороги или начните маршрут. Для фона разрешите уведомления самому Навигатору кнопкой «3». В Навигаторе: Настройки → Навигация → Фоновая навигация и Подсказки в фоновом режиме.\n\nКамера появляется, когда Навигатор показывает её предупреждение с расстоянием. Отдельного порога в метрах нет. Звук — из Навигатора. Ограничение на HUD остаётся текстом; улицы передаются латиницей.\n\nПолосы доступны только из свежих распознанных данных Навигатора. Сложные или недоступные полосы скрываются.\n\nРазрешите фоновую работу приложения в настройках питания Android. Автоподключение возобновляет ранее включённую передачу при открытии приложения, запуске Навигатора и загрузке Android. После сна соединение восстанавливается. Сохранённый HUD ищется автоматически; повторно выбирать его не нужно. Кнопка «Отключить HUD» останавливает передачу, но автоподключение остаётся включённым: при следующем открытии приложения или загрузке Android передача возобновится, а Навигатор сам её не перезапускает. Кнопка «Стоп» и действие «Стоп» в уведомлении дополнительно закрывают приложение. Для Tasker и других средств автоматизации доступны действия CONNECT, DISCONNECT, EXIT и OPEN — см. docs/AUTOMATION.ru.md.\n\nАвтозапуск Навигатора включается в «Настройках». Передача GPS-координат предназначена только для ESP32; для WiiYii она не используется.\n\nТребуется Android 8+ и доступный приложениям Bluetooth LE. Для чтения стрелки с экрана нужен Android 11+.")!
        .SetPositiveButton("Понятно", (_, _) => { })!.Show();

    private string DiagnosticText()
    {
        string maps;
        try { maps = PackageManager!.GetPackageInfo(NavigationSource.PackageName, PackageInfoFlags.MetaData)!.VersionName ?? "?"; }
        catch (Exception) { maps = "не найдены"; }
        string description = YandexScreenService.ManeuverDescription ?? "нет";
        if (description.Length > 120) description = description[..120];
        string text = $"{BuildFlavor.Title} 1.4\n{Build.Manufacturer} {Build.Model}\nAndroid {Build.VERSION.Release}, API {(int)Build.VERSION.SdkInt}\nABI: {string.Join(", ", Build.SupportedAbis ?? [])}\nЯндекс Навигатор: {maps}; уведомления разрешены: {NavigatorCanNotify()}\nBLE feature: {PackageManager?.HasSystemFeature(PackageManager.FeatureBluetoothLe)}\nBluetooth: {BluetoothAccess.Problem(this, scan: false) ?? "доступен"}; поиск: {BluetoothAccess.Problem(this) ?? "доступен"}\nHUD сохранён: {!string.IsNullOrEmpty(Options.Address)}; автоподключение: {Options.Boot}; передача разрешена: {Options.Enabled}\nАвтозапуск Навигатора: {Options.AutoStartNavigator}; GPS: {Options.SendLocation}; поддержка GPS: {HudService.TransportSupportsLocation}; {HudService.LocationStatus}\nЭкран: {YandexScreenService.Connected}; уведомления: {YandexBridgeNotificationListenerService.Connected}\nСлужба: {HudService.Running}; соединение: {HudService.Connected}; {HudService.Status}\nИсточник: {BridgeHub.Current.Source}; режим: {BridgeHub.Current.Mode}\nОписание стрелки: {description}; прочитано: {YandexScreenService.ManeuverDescriptionAt}\nРаспознанный поворот: {BridgeHub.Current.Maneuver ?? "нет"}; команда HUD: {HudProtocol.Direction(HudProtocol.Map(BridgeHub.Current, Options.Lanes).NextTurnDirection)}\nПоследняя отправленная стрелка: {HudService.LastDirection}\nПоследняя запись: {HudService.LastWrite}";
        return text + $"\nЭкранные изображения разрешены: {Options.VisualArrows}; {ForegroundArrowReader.Status}" + "\n\n" + NavigationDiagnostics.Screen + "\n\n" + NavigationDiagnostics.Notification;
    }
    private void CopyDiagnostics()
    {
        string text = DiagnosticText();
        var clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText("Диагностика Яндекс HUD", text);
        Toast.MakeText(this, "Диагностика скопирована", ToastLength.Short)?.Show();
    }
    private void SaveDiagnostics()
    {
        _pendingDiagnostic = DiagnosticText();
        try
        {
            var create = new Intent(Intent.ActionCreateDocument).AddCategory(Intent.CategoryOpenable)!
                .SetType("text/plain")!.PutExtra(Intent.ExtraTitle, "YandexHUD-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            StartActivityForResult(create, 31);
        }
        catch (ActivityNotFoundException)
        {
            _pendingDiagnostic = null;
            Message("В прошивке нет выбора файла. Используйте «Скопировать диагностику».");
        }
    }
    protected override void OnSaveInstanceState(Bundle outState)
    {
        if (_pendingDiagnostic is not null) outState.PutString("pending_diagnostic", _pendingDiagnostic);
        base.OnSaveInstanceState(outState);
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != 31) return;
        var text = _pendingDiagnostic; _pendingDiagnostic = null;
        if (resultCode != Result.Ok || data?.Data is not { } uri || text is null) return;
        try
        {
            using var output = ContentResolver!.OpenOutputStream(uri, "wt") ?? throw new IOException("Файл не открылся для записи");
            using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false));
            writer.Write(text);
            Toast.MakeText(this, "Диагностика сохранена. Файл можно передать по Bluetooth.", ToastLength.Long)?.Show();
        }
        catch (Exception ex) { Message("Не удалось сохранить диагностику: " + ex.Message); }
    }
}



