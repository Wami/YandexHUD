# Mock Route

Отладочный Android-инструмент для проверки Яндекс HUD на реальном движении.
Он подменяет GPS телефона заранее рассчитанным маршрутом. Приложение не входит
в APK Яндекс HUD и не используется в обычной эксплуатации.

Требования: Android 12+ (API 31), JDK/Android SDK из инструкции сборки,
`adb` и Python 3 на компьютере.

## Сборка и установка

```bash
export ANDROID_HOME="$HOME/Android/Sdk"
export JAVA_HOME="/path/to/jdk"
dotnet build tools/MockRoute/MockRoute.csproj -c Debug -t:SignAndroidPackage \
    -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME"
adb install -r tools/MockRoute/bin/Debug/net9.0-android/com.avashield.mockroute-Signed.apk
adb shell pm grant com.avashield.mockroute android.permission.ACCESS_FINE_LOCATION
adb shell pm grant com.avashield.mockroute android.permission.ACCESS_COARSE_LOCATION
adb shell appops set com.avashield.mockroute android:mock_location allow
```

`appops allow` обязателен: без него Android не разрешит `addTestProvider`.
Приложение можно также выбрать вручную: **Настройки → Для разработчиков →
Выбор приложения для имитации местоположения**.

## Маршрут

Маршрут строится через публичный OSRM и разбивается на точки с шагом 1 секунда:

```bash
mkdir -p artifacts/mockroute
python3 tools/MockRoute/make_route.py \
    --from 55.748519,38.003984 \
    --to 55.735000,38.030000 \
    --speed-kmh 60 \
    --out artifacts/mockroute/route.json
adb push artifacts/mockroute/route.json \
    /sdcard/Android/data/com.avashield.mockroute/files/route.json
```

Параметры:

- `--from lat,lon` — старт; задавайте реальную текущую точку телефона;
- `--to lat,lon` — финиш;
- `--speed-kmh` — скорость, по умолчанию 60;
- `--detour 120` — сместить участок 30–38 % маршрута на 120 м в сторону,
  чтобы проверить перестроение маршрута.

Формат `route.json`:

```json
{
  "name": "тестовый маршрут",
  "points": [
    { "lat": 55.7485, "lon": 38.0039, "speed": 16.667, "bearing": 177.8 }
  ]
}
```

## Запуск

```bash
adb shell am start-foreground-service \
    -n com.avashield.mockroute/.MockRouteService -a mockroute.START
adb shell am startservice \
    -n com.avashield.mockroute/.MockRouteService -a mockroute.STOP
```

То же самое доступно кнопками в приложении **Mock Route**. Пока сервис
работает, в системном уведомлении отображается «Симуляция местоположения».

Открыть маршрут в Яндекс Навигаторе:

```bash
adb shell "am start -a android.intent.action.VIEW \
    -d 'yandexnavi://build_route_on_map?lat_to=55.735000&lon_to=38.030000'"
```

Навигатор начнёт ведение сразу. После этого запустите mock GPS. Если запустить
mock раньше, Навигатор построит маршрут уже из подменённой точки.

## Проверка HUD

Debug-сборка Яндекс HUD умеет писать probe заданное число секунд:

```bash
adb shell am start -S -n com.avashield.yandexhud.phone/.MainActivity \
    --ez probe_navigation true --ei probe_seconds 300
```

После `-S` служба специальных возможностей отключается. Включите её снова:

```bash
cur=$(adb shell settings get secure enabled_accessibility_services | tr -d '\r')
adb shell settings put secure enabled_accessibility_services \
    "$cur:com.avashield.yandexhud.phone/com.avashield.yandexhud.phone.YandexScreenService"
adb shell settings put secure accessibility_enabled 1
```

Снимать результат:

```bash
adb shell run-as com.avashield.yandexhud.phone \
    cat cache/navigation-probe.jsonl > probe.jsonl
```

Ожидаемое поведение во время движения:

- `NextTurnDistanceMeters` уменьшается, затем меняется `Maneuver`;
- `RemainingDistanceMeters` и `RemainingTimeSeconds` уменьшаются;
- `CurrentRoad`, `Road`, `SpeedLimitKph` соответствуют экрану Навигатора;
- `HudFrames` меняются вместе с манёвром и расстоянием;
- при выключенном экране `Source` переключается на `notification`;
- после финиша манёвр исчезает, появляется «Конец маршрута».

## Отключение

```bash
adb shell am force-stop com.avashield.mockroute
adb shell appops set com.avashield.mockroute android:mock_location deny
```

После остановки Android возвращает реальный GPS. Для полного удаления:

```bash
adb uninstall com.avashield.mockroute
```

Инструмент экспортирует сервис, чтобы им можно было управлять через ADB.
Не устанавливайте этот APK на устройства, где имитация местоположения
недопустима.
