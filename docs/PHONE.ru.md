# Сборка и проверка для телефона

Та же кодовая база собирает второй APK для обычного телефона. Магнитолочная
сборка при этом не меняется: у неё остаются прежние `ApplicationId`,
компоненты и имя **Яндекс HUD — магнитола**.

| Параметр | Магнитола | Телефон |
| --- | --- | --- |
| `ApplicationId` | `com.avashield.yandexhud.headunit` | `com.avashield.yandexhud.phone` |
| Имя в лаунчере | Яндекс HUD — магнитола | Яндекс HUD — телефон |
| Версия | `1.4` | `1.4-phone` |
| Сборка | по умолчанию | `-p:PhoneBuild=true` |

Обе сборки используют **Яндекс Навигатор** (`ru.yandex.yandexnavi`), а не
Яндекс Карты. Они не устанавливаются друг поверх друга и могут стоять на одном
устройстве одновременно.

## Собрать

Нужны .NET SDK 9, Android workload, Android SDK 35 и JDK, который принимает
установленный workload (обычно 17 или 21; при ошибке `XA0030` установите
указанную версию).

Linux/macOS:

```bash
export ANDROID_HOME="$HOME/Android/Sdk"
export JAVA_HOME="/path/to/jdk-21"
./scripts/build-phone.sh
```

Windows PowerShell:

```powershell
./scripts/build-headunit.ps1 -Phone -AndroidSdk 'C:/Android/Sdk' -JavaSdk 'C:/Java/jdk-21'
```

Оба скрипта сначала запускают автономные проверки, затем собирают и подписывают
APK. Результат:

```text
artifacts/phone/YandexHUD-Phone-Navigator-1.4.apk
artifacts/phone/SHA256SUMS.txt
```

Для проверки без HUD нужен Debug APK с записью навигации:

```bash
./scripts/build-phone.sh --debug
# artifacts/phone-debug/YandexHUD-Phone-Navigator-1.4-debug.apk
```

В PowerShell добавьте `-DebugBuild`; результат попадёт в
`artifacts/phone-debug/`. Debug APK содержит пункт **Записать навигацию 60 с**
и не предназначен для распространения.

Подпись по умолчанию — локальный Android debug-ключ. Для существующего ключа
передайте `--signing-key` (Linux) или `-SigningKey` (PowerShell). Телефонная
сборка не обновляет магнитолочную: идентификаторы разные.

## Установить на телефон

```bash
adb install -r artifacts/phone/YandexHUD-Phone-Navigator-1.4.apk
adb shell am start -n com.avashield.yandexhud.phone/.MainActivity
```

Далее в приложении:

1. Установите **Яндекс Навигатор** на тот же телефон.
2. Нажмите **1. Разрешить чтение навигации** и включите службу
   **Яндекс HUD — телефон: чтение навигации**.
3. Нажмите **2. Доступ к уведомлениям Навигатора** и разрешите доступ.
4. Нажмите **3. Уведомления самого Навигатора** и разрешите уведомления
   Навигатору — без них фоновый режим не отдаёт данные.
5. В настройках питания Android разрешите приложению работу в фоне
   (кнопка **Энергосбережение приложения**).
6. Откройте Навигатор и начните маршрут.

На Android 13+ приложение также попросит разрешение на собственные
уведомления — оно нужно для foreground-службы передачи.

## Что проверять без HUD

Bluetooth-вывод на ESP32 пока не реализован: текущая сборка умеет отправлять
данные только на WiiYii-совместимый HUD. Если HUD нет, всю цепочку
«Навигатор → состояние → кадры» можно проверить встроенной записью.

Debug-сборка пишет 60 секунд в `cache/navigation-probe.jsonl` и дублирует
строки в logcat:

```bash
adb install -r artifacts/phone-debug/YandexHUD-Phone-Navigator-1.4-debug.apk
adb shell am start -n com.avashield.yandexhud.phone/.MainActivity \
    --ez probe_navigation true --ei probe_seconds 300
adb logcat -s YandexHUD YandexCameraTest YandexHeadunitProbe
adb shell run-as com.avashield.yandexhud.phone \
    cat cache/navigation-probe.jsonl > probe.jsonl
```

По умолчанию запись длится 60 секунд; `probe_seconds` задаёт до 600 секунд.

В каждой строке есть состояние источника, манёвр, расстояние, улица, ETA,
камера, ограничение, полосы, `HudDirection` и готовые кадры `HudFrames`.
`HudFrames` должны совпадать с golden-проверками протокола.

### Эмуляция движения по маршруту

Статическая проверка не показывает смену манёвров и уменьшение расстояния. Для
этого в репозитории есть отладочный инструмент [`tools/MockRoute`](../tools/MockRoute/README.ru.md):
он подменяет GPS телефона заранее рассчитанным маршрутом.

```bash
./scripts/mock-route.sh install
./scripts/mock-route.sh start --speed 60
./scripts/mock-route.sh navi
```

`install` собирает и устанавливает MockRoute, выдаёт разрешение на геолокацию и
включает mock location через `appops`. `start` строит маршрут через OSRM,
кладёт его в файлы приложения и запускает воспроизведение. `navi` открывает
Яндекс Навигатор на тот же финиш. Для проверки перестроения добавьте
`--detour 120`. Остановка и отзыв доступа:

```bash
./scripts/mock-route.sh stop
./scripts/mock-route.sh clean
```

Во время движения probe должен показать уменьшение `NextTurnDistanceMeters` и
`RemainingDistanceMeters`, смену `Maneuver`, обновление `HudFrames`, а при
выключенном экране — `Source=notification`.

## Матрица проверки на телефоне

| Сценарий | Ожидание |
| --- | --- |
| Маршрут, Навигатор на экране | 20–30 сэмплов, манёвр, расстояние, улица, ETA, `Source=screen` |
| Маршрут, Навигатор в фоне | данные из уведомления, `Source=notification` |
| Экран выключен / телефон заблокирован | фоновые данные не пропадают, падений нет |
| Обзор дороги без маршрута | ограничение, камера, события, без ложного манёвра |
| Конец маршрута | состояние сбрасывается, старые данные не остаются |
| Отзыв разрешений | понятный статус, без падения, после возврата — восстановление |
| Стрелка без текста | `probe_image_only`, 20+ распознаваний на Android 11+ |
| Поворот экрана, крупный шрифт, вырез | интерфейс не уходит под системные панели |
| Без активного Навигатора | нет постоянного опроса accessibility 2 раза в секунду |
| Перезагрузка | без сохранённого HUD служба не стартует; с HUD — по настройке |

Проверить, что быстрый опрос действительно останавливается:

```bash
adb shell dumpsys activity service com.avashield.yandexhud.phone/.YandexScreenService
adb shell dumpsys activity services com.avashield.yandexhud.phone
```

## Bluetooth и ESP32

Текущий `HudService` работает через интерфейс `IHudTransport`; реализация
`WiiYiiBleTransport` сохраняет прежний протокол. Для ESP32-HUD из
`WARMW00D/HUD-Audi-A4-B9-MLB-Evo-ESP32-S3-SuperMini-LCD-2.79-NV3007-BLE`
потребуется отдельная реализация транспорта, новая GATT-характеристика в
прошивке и сопряжение с PIN. В этой версии протокол ESP32 не реализован.
