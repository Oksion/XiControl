# Архитектура

Цель: **максимально просто.** Трей + всплывающее меню. Служба — только если без неё реально нельзя.

> 📌 **Документ этапа планирования / запись решения.** Главный вопрос — «нужна ли фоновая служба» —
> закрыт: **не нужна** (см. ниже). Актуальная карта модулей и правил — в [CLAUDE.md](../CLAUDE.md)
> «Архитектура», статус фич — в [ROADMAP.md](../ROADMAP.md). Ниже — как решали и как в итоге вышло.

## ✅ Стек зафиксирован

- **Язык/платформа:** C# / .NET 8
- **UI:** Windows App SDK / WinUI 3; tray transport — `Shell_NotifyIcon`, popup-menu — WinUI `Window`
- **WMI:** `System.Management` (`ManagementObjectSearcher` / `InvokeMethod`, события — `ManagementEventWatcher`)
- **Питание/сон:** `Microsoft.Win32.SystemEvents.PowerModeChanged`
- **Сборка/раздача:** unpackaged WinUI self-contained single-file → один .exe (~110 МБ), без установки runtime
- **Лицензия:** GPLv3
- **Разрядность:** x64, манифест `requireAdministrator`

---

## ✅ РЕШЕНО: служба НЕ нужна (План А)

Пробы на TM2424 (журнал — в локальном `reference/`, в репозиторий не входит) закрыли обе причины для службы:
- **Привилегии:** обычного admin достаточно для SET/GET (заряд и режимы переключаются). ✓
- **События:** `HID_EVENT20` ловятся из пользовательской admin-сессии (`ManagementEventWatcher`). ✓

⇒ **Одно unpackaged WinUI 3 трей-приложение** (admin, автозапуск при логине). Никакой службы, пайпов, protobuf.

---

## (архив) Развилка: нужна ли фоновая служба?

Референс `MIControl` сделан как **GUI (user) + служба (SYSTEM) + named pipe + protobuf**. Это тяжёлая схема. Служба там нужна ради трёх вещей:

1. **Подписка на WMI-события** (`HID_EVENT20`) — нужен постоянно живущий COM-sink.
2. **Пере-применение защиты заряда** при выходе из сна и смене питания (EC сбрасывает состояние).
3. **Привилегии** — WMI `Set`, возможно, требует SYSTEM/elevation.

Но для наших задач всё это можно решить **без отдельной службы** — резидентным трей-приложением, запускаемым с правами администратора при логине.

### Проверить эмпирически (см. `05-open-questions.md`)
- Работает ли `MiInterface` Set из обычного admin-процесса, или нужен именно SYSTEM? → определяет, нужна ли служба ради привилегий.
- Приходят ли `HID_EVENT20` в COM-sink из пользовательской сессии? → определяет, нужна ли служба ради событий.

---

## Рекомендуемая схема: один трей-резидент (без службы)

```
┌─────────────────────────────────────────────┐
│  xi_control.exe  (tray, admin, autostart)     │
│                                               │
│  ├─ WmiClient        — обёртка MiInterface     │
│  │    Set/Get(cmd, arg, arg2) → OutData        │
│  ├─ TrayUI           — иконка + popup-меню     │
│  ├─ OsdOverlay       — всплывашка при смене    │
│  ├─ ChargeGuard      — re-arm заряда на        │
│  │    resume / power-change (см. ниже)         │
│  ├─ EventSink (v0.3) — подписка HID_EVENT20    │
│  └─ Config           — ini/reg, автозапуск     │
└─────────────────────────────────────────────┘
```

**Автозапуск и права:** Task Scheduler, `/sc onlogon /rl highest`, задержка ~5 c (как уже сделано в CoreCharge — паттерн рабочий). `requireAdministrator` в манифесте.

**ChargeGuard (важно, взять идею из MIControl):**
- `RegisterSuspendResumeNotification` → при resume пере-применить лимит.
- Подписка `SELECT * FROM Win32_PowerManagementEvent` (`ROOT\CIMv2`) или `WM_POWERBROADCAST` → при смене AC/DC пере-применить.
- Причина: прошивка/EC сбрасывает защиту заряда после сна/переподключения БП. Без этого лимит «слетает». (В текущем CoreCharge этого нет — вероятный баг.)

**EventSink (только v0.3):** WMI temporary event consumer на `HID_EVENT20`. Если из user-сессии не заводится — тогда (и только тогда) выносим слушатель в минимальную службу и шлём в трей простым сообщением (можно без protobuf — хватит `WM_COPYDATA` или пайпа с сырыми байтами).

---

## Если служба всё же понадобится (план Б)

Держать её **минимальной**: только WMI (Set + event sink), без GUI. Общение с треем — самое простое:
- `WM_COPYDATA` или именованный пайп с **сырым 32-байтным буфером** (не тащить protobuf — это оверинжиниринг MIControl).
- Служба ставится/снимается из трея (как `CSvcInstall` у референса, но проще).

---

## Технологический стек (зафиксирован — см. верх файла)

| Что | Вариант | Почему |
|-----|---------|--------|
| Язык | C# / .NET 8 | WMI из коробки, комфортно после TS, отличный тулинг |
| WMI вызовы | `System.Management` → `ManagementObject.InvokeMethod("MiInterface", …)` | вместо C++ SafeArray/VARIANT — 5 строк |
| WMI события | `ManagementEventWatcher` на `SELECT * FROM HID_EVENT20` | простая подписка |
| UI окон | Windows App SDK / WinUI 3 (`Window`, `NavigationView`, WinUI controls) | единый современный UI-стек Windows 10/11 |
| UI трея | Win32 `Shell_NotifyIcon` transport + WinUI `TrayMenuWindow` | у WinUI 3 нет собственного tray API, но всё видимое меню остаётся WinUI |
| OSD | borderless topmost WinUI `Window` через `AppWindow` | одна XAML/composition-модель с остальными окнами |
| Питание | `SystemEvents.PowerModeChanged` + `SessionEnding`, маршалинг через `DispatcherQueue` | для ChargeGuard; `RegisterSuspendResumeNotification` не понадобился |
| Сборка | unpackaged Windows App SDK self-contained + `PublishSingleFile` | один exe, без установленного .NET/Windows App SDK runtime |

> Native AOT пока **не** закладываем: `System.Management` использует COM-interop/reflection и с AOT капризен. Self-contained single-file — надёжный вариант.

### Раскладка проекта — как вышло

Планировали компактно; разрослось (панель, «Монитор», окно настроек, гарды, тачпад/экран,
здоровье батареи), но структура та же: WMI-обёртка + UI + системная интеграция, **без службы**.
В 2026-07 проведён рефакторинг (ветка `refactor`): DI-контейнер, командный слой `AppController`,
god-классы разобраны по швам, добавлены юнит-тесты и жёсткие анализаторы
(`TreatWarningsAsErrors`). В 2026-08 продуктовый UI полностью переведён на WinUI 3.
Полное описание модулей — в CLAUDE.md
«Архитектура»; коротко:

```
xi_control/
 ├─ XiControl.sln           — src + tests + tools; сборка: dotnet build XiControl.sln -c Release
 ├─ src/
 │   ├─ App.xaml[.cs]       — WinUI entry point: mutex → DI (MS.DI, все синглтоны) → TrayApp.Start
 │   ├─ Config/             — AppConfig, IConfigStore/JsonConfigStore, AppPaths (портативный
 │   │                         режим XIC-34: данные рядом с exe по метке .portable/portable.txt)
 │   ├─ Wmi/                — Mifs.cs (константы протокола), IMifsClient/MifsClient,
 │   │                         IKeyEventSource/MifsEventWatcher
 │   ├─ Input/              — MiButtonGesture (жесты Mi-кнопки), KeyRouter (клавиша → действие)
 │   ├─ Ui/                 — AppController, SettingsActions, ModeUi, UiNav, tray icon logic
 │   ├─ Ui/WinUI/           — TrayApp, NativeTrayIcon/TrayMenuWindow, QuickPanelWindow,
 │   │                         OsdWindow/OemOsdWindow, MonitorWindow, SettingsWindow,
 │   │                         FlyoutWindow, SettingsBuilder, WinUI theme helpers
 │   ├─ SystemIntegration/  — ChargeGuard, RefreshRate(+Guard), PowerProfileGuard,
 │   │                         BrightnessCapGuard (лимит яркости, XIC-29),
 │   │                         AutoBrightnessGuard + BrightnessCurve/MedianWindow +
 │   │                         AlsSensor (авто-яркость по датчику, XIC-30 — см. docs/13),
 │   │                         TravelChargeMonitor, IPowerEvents+IDisplayEvents/SystemEventsSource,
 │   │                         IAppTimer/UiTimer/WorkerTimer, Brightness (+Ramp/Own/
 │   │                         AdaptiveBrightness), TouchpadControl/TouchscreenControl
 │   │                         (общий HidNodeToggle), TouchpadDeadZone, AwakeMode, MicControl,
 │   │                         KeyActions, AutoStart, UpdateCheck, Sound, BatteryInfo, PowerDraw,
 │   │                         GpuTelemetry,
 │   │                         HttpApi/ApiRouter/ApiSettings/ApiFirewall (opt-in HTTP API, XIC-13)
 │   ├─ Config/             — AppConfig (POCO + миграции), IConfigStore/JsonConfigStore,
 │   │                         LegacyLanguageConverter
 │   └─ Localization/       — lang/{ru,en,zh}.json (переводы, встроены в exe) + Loc.cs (загрузчик,
 │                             Loc.T) и шов ILocalizer
 └─ tests/XiControl.Tests/  — юнит-тесты чистой логики на фейках (Fakes.cs), без железа
```

---

## Чего избегаем (уроки из двух проектов)

- ❌ WinRing0 и любой kernel-driver → цель проекта.
- ❌ protobuf + named pipe + отдельный GUI-процесс (переусложнение MIControl).
- ❌ Один файл на 1700 строк (`main_clean.cpp` у CoreCharge) → сразу бить на модули.
- ❌ `schtasks`/PowerShell на каждый `SaveConfig` (баг CoreCharge) → трогать планировщик только по кнопке.
- ❌ Синхронные EC/WMI-вызовы в UI-потоке с длинными таймаутами → выносить в воркер.
