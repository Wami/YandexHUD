# AGENTS.md

## Проект

- Один Android-проект (`src/YandexHudHeadUnit`, .NET 9, `net9.0-android`, API 26+) собирает два APK: магнитолу (по умолчанию) и телефон (`PhoneBuild=true`). APK один на обе ABI (`android-arm64;android-arm`, без package-per-ABI).
- Источник навигации — только Яндекс Навигатор `ru.yandex.yandexnavi`. Яндекс Карты источником не являются.
- Читаются уведомление и экран (accessibility); распознавание стрелки по пикселям — опция, Android 11+.
- BLE-вывод: `IHudTransport` / `WiiYiiBleTransport` (WiiYii C1) и `Esp32HudTransport` (протокол v1, `IEsp32HudTransport`). Транспорт выбирается `Options.Esp32` (тумблер «ESP32 HUD»); `HudScanner` находит оба по Service UUID. Нормативный контракт — `../protocol/spec.md` (v1), схема — `../protocol/schema/protocol.json`, фикстуры — `../protocol/vectors/golden.json`. Кодировщик — `src/YandexHudHeadUnit/Esp32HudProtocol.cs`. `docs/ESP32-HUD-PROTOCOL.ru.md` и согласование — исторические.
- Release APK не должен получать разрешение `INTERNET`: это заявлено в документации и является осознанным ограничением (поэтому канал HUD — только BLE).

## Команды

Проверки — два консольных проекта; `dotnet test` и фильтры не работают, проект выполняется целиком:

```bash
dotnet run --project tests/WiiYiiHudNavigator.Yandex.Tests -c Release
dotnet run --project tests/YandexHudHeadUnit.Tests -c Release
```

Сборка:

```bash
./scripts/build-phone.sh                  # artifacts/phone/
./scripts/build-phone.sh --debug          # artifacts/phone-debug/, с probe
./scripts/build-headunit.ps1              # PowerShell 7, artifacts/release/
./scripts/build-headunit.ps1 -DebugBuild  # artifacts/headunit-debug/
./scripts/build-headunit.ps1 -Phone       # artifacts/phone/
```

Скрипты сначала запускают обе группы проверок, затем собирают APK и пишут `SHA256SUMS.txt`. Нужны SDK из `global.json` (9.0.200, rollForward latestFeature), Android workload, Android SDK с платформой 35 и JDK 17 (`ANDROID_HOME`, `JAVA_HOME`). Workload 35.0.7 не принимает JDK 21 — при `XA0030` используйте JDK 17.

Подготовленный тулчейн в `/tmp/opencode`: `dotnet` 9.0.320 + android workload 35.0.7, `android-sdk` (platform 35 / build-tools 35.0.0), `jdk17`, `pwsh`. Перед сборкой:

```bash
export DOTNET_ROOT=/tmp/opencode/dotnet
export PATH=/tmp/opencode/dotnet:/tmp/opencode/jdk17/bin:$PATH
export DOTNET_CLI_HOME=/tmp/opencode/dotnet-home
export ANDROID_HOME=/tmp/opencode/android-sdk
export ANDROID_SDK_ROOT=/tmp/opencode/android-sdk
export JAVA_HOME=/tmp/opencode/jdk17
export NUGET_PACKAGES=/tmp/opencode/dotnet-home/.nuget/packages
./scripts/build-phone.sh
/tmp/opencode/pwsh/pwsh -NoProfile -File scripts/build-headunit.ps1
```

## Две сборки

- `HEADUNIT` определён всегда, `PHONE` — только при `PhoneBuild=true`.
- Магнитолочная сборка должна оставаться совместимой для обновления поверх: не меняйте её `ApplicationId`, имена компонентов и поведение по умолчанию.
- `ApplicationId`: `com.avashield.yandexhud.headunit` и `com.avashield.yandexhud.phone`. Пространство имён компонентов берите из `BuildFlavor.ComponentNamespace`, подписи — из `BuildFlavor`; в манифесте label общий.
- `#if !HEADUNIT` есть только в `BridgeHub.cs` — наследие отдельного моста; в APK этот код не попадает.

## Устройство исходников

- `.sln` нет; собирайте конкретный `.csproj`. В тестовых проектах включён `TreatWarningsAsErrors`, файлы перечислены вручную: новый общий файл добавьте в `.csproj` теста, иначе он проверяться не будет.
- `src/YandexHudHeadUnit/Esp32HudProtocol.Generated.cs` и `tests/YandexHudHeadUnit.Tests/Esp32HudVectors.Generated.cs` генерируются `python3 ../protocol/tools/gen.py`; не редактировать вручную.
- Основной `.csproj` подключает весь `src/YandexNotificationBridge/*.cs` (кроме `MainActivity.cs` и отсутствующего `BridgeServer.cs`), но из `Plugin.Yandex` — только `BridgeState.cs` и `HudText.cs`, из `Common` — три модели (`HudNavigationData`, `DirectionInstruction`, `LaneGuidance`).
- `YandexNotificationParser.cs` и `HudOutputPump.cs` компилируются **только в тестах** — это легаси-парсер, в APK его нет. В рантайме работают `NotificationReader`, `ScreenNavigationReader`, `SourceSelector`.
- Два разных типа `BridgeState`: `YandexNotificationBridge.BridgeState` (сырое чтение) и `WiiYiiHudNavigator.Plugin.Yandex.BridgeState` (конвертация в `HudNavigationData`); переход — `HudProtocol.Map`.
- Манифест лежит в `src/YandexHudHeadUnit/Properties/AndroidManifest.xml`, не в корне проекта; `INTERNET` там отсутствует намеренно.
- CI (`.github/workflows/tests.yml`) запускает только две группы проверок; APK в CI не собирается.

## Проверка без HUD

- Probes есть только в Debug (`#if DEBUG`), в Release их нет: `probe_navigation` → `cache/navigation-probe.jsonl`, `probe_location` → `cache/location-probe.jsonl`, `probe_image_only`, `reader_self_test`.

```bash
adb install -r artifacts/phone-debug/YandexHUD-Phone-Navigator-1.4-debug.apk
adb shell am start -n com.avashield.yandexhud.phone/.MainActivity \
    --ez probe_navigation true --ei probe_seconds 300
adb shell run-as com.avashield.yandexhud.phone \
    cat cache/navigation-probe.jsonl > probe.jsonl
```

- `am start -S` отключает службу доступности; после него её нужно включить заново.
- `probe_image_only`, `reader_self_test` и фикстуры стрелок требуют установленного Яндекс Навигатора с подходящими ресурсами; это on-device код и в CI не покрыто.
- Движение проверяется через `tools/MockRoute` и `scripts/mock-route.sh` (`install`, `start --speed 60`, `stop`, `navi`, `clean`). Нужны Python 3, `adb` и сеть для публичного OSRM; mock location выдаётся через `appops`. После теста вызывайте `clean`.
- `tools/MockRoute` — отдельный dev-APK, в обычные сборки не входит.

## Соглашения

- Документация и UI — по-русски, код и комментарии — по-английски; сообщения коммитов в истории английские.
- `.gitattributes` содержит `* -text`: не нормализуйте переводы строк и не запускайте массовые форматтеры.
- `artifacts/`, `bin/`, `obj/` в `.gitignore`; не коммитьте APK, ключи и локальные настройки.
- Лицензия AGPL-3.0; сохраняйте уведомления об авторстве в `NOTICE.md`.
- Подробности: `docs/BUILDING.ru.md`, `docs/PHONE.ru.md`, `docs/TROUBLESHOOTING.ru.md`, `docs/PRIVACY.ru.md`, `tools/MockRoute/README.ru.md`.
