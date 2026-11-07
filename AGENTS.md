# AGENTS.md

## Проект

- Один Android-проект (`src/YandexHudHeadUnit`, .NET 9, `net9.0-android`, API 26+) собирает два APK: магнитолу (по умолчанию) и телефон (`PhoneBuild=true`).
- Источник навигации — только Яндекс Навигатор `ru.yandex.yandexnavi`. Яндекс Карты источником не являются.
- BLE-вывод идёт через `IHudTransport` / `WiiYiiBleTransport` (WiiYii C1). Транспорт для ESP32 не реализован; GPS-координаты подготовлены через `IHudLocationTransport`, черновик протокола — `docs/ESP32-HUD-PROTOCOL.ru.md`.
- Release APK не должен получать разрешение `INTERNET`: это заявлено в документации и является осознанным ограничением.

## Команды

Проверки (Android workload не нужен; это консольные приложения, не `dotnet test`):

```bash
dotnet run --project tests/WiiYiiHudNavigator.Yandex.Tests -c Release
dotnet run --project tests/YandexHudHeadUnit.Tests -c Release
```

Сборка:

```bash
./scripts/build-phone.sh                 # artifacts/phone/
./scripts/build-phone.sh --debug         # artifacts/phone-debug/, с probe
./scripts/build-headunit.ps1             # PowerShell 7, artifacts/release/
./scripts/build-headunit.ps1 -Phone -DebugBuild
```

Скрипты сначала запускают обе группы проверок, затем собирают APK и пишут `SHA256SUMS.txt`.

Нужны .NET SDK 9.0.200+, Android workload, Android SDK с платформой 35 и JDK 17/21 (`ANDROID_HOME`, `JAVA_HOME`). При ошибке `XA0030` установите JDK из текста ошибки.

## Две сборки

- `HEADUNIT` определён всегда, `PHONE` — только при `PhoneBuild=true`.
- Магнитолочная сборка должна оставаться совместимой для обновления поверх: не меняйте её `ApplicationId`, имена компонентов и поведение по умолчанию.
- `ApplicationId`: `com.avashield.yandexhud.headunit` и `com.avashield.yandexhud.phone`. Пространство имён компонентов берите из `BuildFlavor.ComponentNamespace`, подписи — из `BuildFlavor`; в манифесте label общий.
- `#if !HEADUNIT` в `src/YandexNotificationBridge` — наследие отдельного моста; в APK этот код не попадает.

## Устройство исходников

- Основной `.csproj` подключает общие `.cs` из `YandexNotificationBridge`, `WiiYiiHudNavigator.Plugin.Yandex` и `WiiYiiHudNavigator.Common` через `<Compile Include>`. Это не отдельные собираемые проекты.
- Тестовые проекты тоже перечисляют файлы вручную. Новый общий файл, нужный тестам, добавьте в `.csproj` теста, иначе он проверяться не будет.
- `.sln` нет; собирайте конкретный `.csproj`. В тестовых проектах включён `TreatWarningsAsErrors`.
- CI (`.github/workflows/tests.yml`) запускает только две группы проверок; APK в CI не собирается.

## Проверка без HUD

- Debug-сборка содержит `probe_navigation` и `probe_image_only`; в Release их нет.

```bash
adb install -r artifacts/phone-debug/YandexHUD-Phone-Navigator-1.4-debug.apk
adb shell am start -n com.avashield.yandexhud.phone/.MainActivity \
    --ez probe_navigation true --ei probe_seconds 300
adb shell run-as com.avashield.yandexhud.phone \
    cat cache/navigation-probe.jsonl > probe.jsonl
```

- `am start -S` отключает службу доступности; после него её нужно включить заново.
- `probe_image_only` и `reader_self_test` требуют установленного Яндекс Навигатора с подходящими ресурсами.
- Движение проверяется через `tools/MockRoute` и `scripts/mock-route.sh` (`install`, `start --speed 60`, `navi`, `clean`). Нужны Python 3, `adb` и сеть для публичного OSRM; mock location выдаётся через `appops`. После теста вызывайте `clean`.
- `tools/MockRoute` — отдельный dev-APK, в обычные сборки не входит.

## Соглашения

- Документация и UI — по-русски, код и комментарии — по-английски; сообщения коммитов в истории английские.
- `.gitattributes` содержит `* -text`: не нормализуйте переводы строк и не запускайте массовые форматтеры.
- `artifacts/`, `bin/`, `obj/` в `.gitignore`; не коммитьте APK, ключи и локальные настройки.
- Лицензия AGPL-3.0; сохраняйте уведомления об авторстве в `NOTICE.md`.
- Подробности: `docs/BUILDING.ru.md`, `docs/PHONE.ru.md`, `docs/TROUBLESHOOTING.ru.md`, `tools/MockRoute/README.ru.md`.
