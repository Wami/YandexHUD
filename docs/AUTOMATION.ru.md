# Автоматизация: Tasker, Macrodroid, ADB

Приложение принимает команды от внешних средств автоматизации через стандартные Android-intent'ы. Отдельный плагин не нужен.

## Пакеты

| Сборка | Пакет | Компоненты |
| --- | --- | --- |
| Телефон | `com.avashield.yandexhud.phone` | `.HudControlActivity`, `.HudControlReceiver` |
| Магнитола | `com.avashield.yandexhud.headunit` | `.HudControlActivity`, `.HudControlReceiver` |

Дальше используется пакет телефона. Для магнитолы замените `phone` на `headunit`.

## Действия

| Action | Что делает |
| --- | --- |
| `com.avashield.yandexhud.phone.CONNECT` | Включает передачу и подключается к сохранённому HUD. Снимает состояние «остановлено». |
| `com.avashield.yandexhud.phone.DISCONNECT` | Останавливает передачу и службу. Приложение остаётся открытым. |
| `com.avashield.yandexhud.phone.EXIT` | То же, что `DISCONNECT`, и дополнительно закрывает окно приложения. |
| `com.avashield.yandexhud.phone.OPEN` | Открывает приложение. |

Семантика остановки: после `DISCONNECT` или `EXIT` передача не запускается сама от событий Навигатора. Она снова включается при открытии приложения, загрузке Android или командой `CONNECT`. Галка «Автоматически подключаться к HUD» при этом остаётся включённой. Кнопка «Стоп» в уведомлении приложения работает так же, как `EXIT`, и закрывает окно приложения.

## Activity или Broadcast

- `CONNECT` и `OPEN` отправляйте в **Activity** `HudControlActivity`: Android 12+ не разрешает фоновым приложениям запускать foreground-службу и activity из broadcast, а видимая activity такого ограничения не имеет.
- `DISCONNECT` и `EXIT` можно отправлять в **Broadcast Receiver** `HudControlReceiver` — он работает из фона и не показывает интерфейс.
- Оба компонента принимают все четыре действия, поэтому ошибка адресатом ничего не ломает, но поведение из фона может быть ограничено системой.

## ADB

```bash
# Подключиться
adb shell am start -n com.avashield.yandexhud.phone/.HudControlActivity \
    -a com.avashield.yandexhud.phone.CONNECT

# Открыть приложение
adb shell am start -n com.avashield.yandexhud.phone/.HudControlActivity \
    -a com.avashield.yandexhud.phone.OPEN

# Остановить передачу
adb shell am broadcast -n com.avashield.yandexhud.phone/.HudControlReceiver \
    -a com.avashield.yandexhud.phone.DISCONNECT

# Остановить и закрыть приложение
adb shell am broadcast -n com.avashield.yandexhud.phone/.HudControlReceiver \
    -a com.avashield.yandexhud.phone.EXIT
```

## Tasker

1. Создайте задачу и добавьте действие **Send Intent**.
2. Для подключения:
   - Action: `com.avashield.yandexhud.phone.CONNECT`
   - Target: **Activity**
   - Package: `com.avashield.yandexhud.phone`
   - Class: `com.avashield.yandexhud.phone.HudControlActivity`
3. Для остановки или выхода:
   - Action: `com.avashield.yandexhud.phone.DISCONNECT` или `...EXIT`
   - Target: **Broadcast Receiver**
   - Package: `com.avashield.yandexhud.phone`
   - Class: `com.avashield.yandexhud.phone.HudControlReceiver`
4. Для запуска приложения можно использовать штатное действие Tasker **Launch App** → «Яндекс HUD — телефон», либо `OPEN` из шага 2.

Если профиль Tasker срабатывает в фоне (экран выключен, приложение не открыто) и `CONNECT` не подключается, разрешите приложению «Поверх других приложений» в настройках Android: это снимает ограничение на фоновый запуск службы. Остановка и выход работают и без этого разрешения.

## Безопасность

Компоненты экспортируются без разрешения, иначе Tasker не смог бы ими управлять. Любое приложение на устройстве может отправить эти команды. Ничего секретного они не раскрывают: можно только включить передачу на уже сохранённый HUD, остановить её или открыть приложение.
