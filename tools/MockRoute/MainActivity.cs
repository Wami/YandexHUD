using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace MockRoute;

[Activity(Name = "com.avashield.mockroute.MainActivity", Label = "Mock Route",
    MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private TextView _status = null!;
    private TextView _route = null!;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private static readonly Color Background = Color.Rgb(15, 25, 38);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetPadding(Dp(20), Dp(24), Dp(20), Dp(20));
        page.SetBackgroundColor(Background);
        page.AddView(Text("Mock Route", 26));
        page.AddView(Text("Подменный GPS для проверки Яндекс Навигатора", 14, Color.Rgb(155, 178, 196)));
        _route = Text("", 15);
        _route.SetPadding(0, Dp(20), 0, 0);
        page.AddView(_route);
        _status = Text("", 20, Color.Rgb(102, 235, 206));
        _status.SetPadding(0, Dp(20), 0, Dp(20));
        page.AddView(_status);
        Button(page, "Старт", Start);
        Button(page, "Стоп", Stop);
        page.AddView(Text(
            "Маршрут: Android/data/com.avashield.mockroute/files/route.json\n" +
            "Перед стартом разрешите геолокацию и включите приложение как mock location.",
            13, Color.Rgb(155, 178, 196)));
        var scroll = new ScrollView(this);
        scroll.AddView(page);
        SetContentView(scroll);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent?.Action == MockRouteService.ActionStart) Start();
        else if (intent?.Action == MockRouteService.ActionStop) Stop();
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshRoute();
        Tick();
    }

    protected override void OnPause()
    {
        _handler.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private void RefreshRoute()
    {
        var (route, source) = RouteStore.Load(this);
        _route.Text = route is null
            ? "Маршрут не загружен."
            : $"Маршрут: {route.Name ?? source}\nТочек: {route.Points.Count}";
    }

    private void Tick()
    {
        _status.Text = MockRouteService.Status;
        _handler.PostDelayed(Tick, 1000);
    }

    private void Start()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.AccessFineLocation,
                Android.Manifest.Permission.AccessCoarseLocation], 1);
            return;
        }
        try
        {
            StartForegroundService(new Intent(this, typeof(MockRouteService)).SetAction(MockRouteService.ActionStart));
            _status.Text = "Запуск…";
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("MockRoute", "Start failed: " + ex);
            _status.Text = "Ошибка запуска: " + ex.Message;
        }
    }

    private void Stop()
    {
        try { StartService(new Intent(this, typeof(MockRouteService)).SetAction(MockRouteService.ActionStop)); }
        catch (Exception ex) { _status.Text = "Ошибка остановки: " + ex.Message; }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1 && CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted)
            Start();
        else if (requestCode == 1) _status.Text = "Без разрешения геолокации mock GPS не работает.";
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);

    private TextView Text(string text, float size, Color? color = null)
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(color ?? Color.White);
        return view;
    }

    private void Button(LinearLayout parent, string label, Action action)
    {
        var button = new Button(this) { Text = label, TextSize = 16 };
        button.SetAllCaps(false);
        button.SetTextColor(Color.White);
        button.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Color.Rgb(34, 65, 87));
        parent.AddView(button, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });
        button.Click += (_, _) => action();
    }
}
