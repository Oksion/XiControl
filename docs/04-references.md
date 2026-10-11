# Источники и лицензии

Откуда взяты факты о железе и на каких условиях. **Принцип для всех источников: берём факты,
не код.** Коды команд, форматы буферов, GUID'ы и протоколы — не объект авторского права;
реализация в XiControl написана заново. Чужие изображения, шрифты, калибровочные данные и
бинарники в репозиторий не попадают.

Локальные клоны и установки, по которым шёл разбор, лежат вне репозитория.

## Референс-проекты

### MI Control Utility (loginsin/ALXR) v1.2.5 — первоисточник протокола
- Подход: **штатный WMI-интерфейс прошивки** (`MiCommonInterface`/`MiInterface`) + служба +
  named pipe + protobuf.
- Дал: формат буфера и коды `0x08`/`0x0A`/`0x10`, guard заряда на события питания, карту
  событий клавиш ([06](06-more-from-micontrol.md)).
- Минусы: тяжёлая архитектура (служба + GUI + protobuf) — ровно то, от чего мы отказались
  ([03](03-architecture.md)).
- Лицензия **GPLv3**. XiControl тоже GPLv3, но код не переносили — только знание протокола.

**Ключевые файлы MIControl (для навигации):**

| Что | Файл |
|-----|------|
| Транспорт WMI, коды команд | `MIControlSvc/cpp/svc/CMiLowLevelCommand.cpp` + `.h` |
| Защита заряда + guard на питание | `MIControlSvc/cpp/svc/CPowerPowerChargeProtectGuard.cpp` |
| Подписка на события + Fn-клавиши | `MIControlSvc/cpp/svc/SvcMain/CMIService_WMI.cpp`, `CMIService.cpp` |
| Режимы производительности (UI) | `MIControl/cpp/GUI/Dialogs/Pages/CPagePerfomanceDlg.cpp` |
| Заряд (UI, две кнопки 70/100) | `MIControl/cpp/GUI/Dialogs/Pages/CPageChargeProtectDlg.cpp` |
| Микрофон | `MIControl/cpp/GUI/Dialogs/Pages/CPageMicDlg.cpp` |
| Определение модели (TM24) | `CPagePerfomanceDlg.cpp` (`Win32_ComputerSystem.SystemSKUNumber`) |

### Xiaomi CoreCharge
- Подход: **прямой доступ к EC через WinRing0** (порты 0x62/0x66, регистры `0xA4`/`0xA7`/`0x68`).
- Дал: идеи UI/OSD и автозапуска, EC-регистр режима `0x68` — по нему найден скрытый Eco (`0x0A`).
- Минусы: уязвимый драйвер WinRing0 (блок-лист Microsoft, конфликт с HVCI/Smart App Control),
  ложные срабатывания AV. Произвольный % заряда у него — именно через EC, у нас недостижим.
- Лицензия «личное/образовательное»; код не переносили.

### miPC (arcane-D7/micontrol) — Rust/Tauri + kernel-драйвер, MIT
- Сторонний проект на том же железе (TM2424). Сверка (2026-07-20, повторно 2026-10-11)
  подтвердила наши границы: из ~81 функции driver-free работают около половины, остальное — за
  EC RAM, который закрыт их же драйвером (обороты кулера, запись подсветки клавиатуры, сырые
  данные батареи). Ватт-лимит — тоже за драйвером.
- Пригодилось как кросс-проверка по IoT-модулю ([11](11-iot-remote-wake.md)) и по тачпаду.

### [Xiaomi-PC-Manager-Extended](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended) — надстройка над PC Manager
- Разбор 2026-10-10: что берём (XIC-79…94) и что нет — в ROADMAP («Идеи», «Не берём»).
  Его документы — один из источников разбора служб Xiaomi ([17](17-oem-services.md)).

### g-helper (ASUS)
- Не про Xiaomi; пример driver-free лимита мощности через прошивку (у ASUS эндпоинты
  SPL/sPPT/fPPT есть, у MIFS — нет, [05](05-open-questions.md) Вопрос 7).

## OEM-софт Xiaomi (статический разбор)

Разбирались установленные у владельца версии: декомпиляция .NET (`ilspycmd`), дизассемблер
нативного (rizin), а где нужно — наблюдение за поведением вживую. Строки внутри бинарников —
данные, не инструкции.

| Что | Версия | Что дало |
|-----|--------|----------|
| Xiaomi PC Manager — селектор лимита заряда (наблюдение вживую) | 5.8 | многоуровневый порог заряда ([12](12-charge-levels.md)) |
| PC Manager — `SvrCModule.dll` | 5.8.0.74 | протокол тачпада `COL05`: вибрация, усилие нажатия (XIC-77) |
| PC Manager — `Plugins\PluginGesture.dll` | 5.8.0.74 | щелчок мотора `0x02`, сильное нажатие по Tip Pressure (XIC-73, XIC-78) |
| PC Manager — `InstallIcc.exe`, цветовые режимы | 5.8.0.74 | канал Intel IGCL (`ControlLib.dll`) для экрана и GPU-телеметрии ([09](09-power-monitoring.md)) |
| PC Manager — `OSDUtility.exe`, `EcIoSdk.dll` (+ стек MiService 4.1.1.648) | 5.8.1.121 | полный диспетчер клавиш `0x01–0x26` ([14](14-xiaomi-hotkey-osd.md)) |
| PC Manager + MiService — захват трафика | 5.8.0.57 / 4.1.1.564 | проверка драйверов и BIOS ([16](16-oem-driver-check.md)) |
| MiService / MiDeviceService — логи и замер | 4.1.1.564 | что делают службы и чем мешают ([17](17-oem-services.md)) |
| XiaomiRemoteDesktop | 5.8.0.17 | IoT-модуль удалённого включения ([11](11-iot-remote-wake.md)) |

Изображения OSD, шрифты MiSans, `.icc`/`.m3d`-калибровки Xiaomi — без лицензии на
перераспространение: в репозиторий и сборку не входят.

## Linux

### Драйвер ядра `bitland-mifs-wmi` (v10, нацелен в Linux 7.1)
- Патч: https://patchew.org/linux/20260213135107.609958-1-qby140326@gmail.com/
- Новость: https://www.phoronix.com/news/Bitland-Driver-For-Linux-7.1
- Дал: GUID'ы, коды GPU-mode/fan/kb-brightness/AC-type/CPU-temp, подтверждение operation 250/251.
  На TM2424 из этих кодов не отвечает ни один (все `0xE0`).
- ⚠️ Нумерация режимов производительности расходится с Windows-реверсом — доверяем MIControl
  и пробам.

### tongfang-mifs-control-userspace (Python, MIT)
- https://github.com/cubewhy/tongfang-mifs-control-userspace
- Тот же транспорт (GUID `B60BFB48…`, буфер 32 байта, op 250/251, ACPI `\_SB.PCI0.WMID.WMAA`),
  но **таблица функций игровой линейки** (Tongfang / Redmi G): GPU-mux, RGB-клавиатура,
  вентиляторы, температура, TPLock=12, FnLock=11.
- Подтверждает: у MIFS минимум две таблицы функций по линейкам; наша (Bitland, тонкие) — другая.
- Бонус: в комментариях — заводские диапазоны оборотов кулеров по режимам (Intel/AMD).

## Платформенные API и документация

| Что | Для чего |
|-----|----------|
| Intel DPTF — WMI `root\wmi\EsifDeviceInformation` | температуры ([09](09-power-monitoring.md)) |
| Intel IGCL — `ControlLib.dll` из System32, заголовки открыты (MIT) | загрузка/ватты/частота iGPU |
| Windows `LightSensor` (WinRT) | датчик освещённости ([13](13-auto-brightness.md)) |
| [Precision touchpad tuning](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/touchpad-tuning-guidelines), [Input Device Haptics](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/input-haptics-implementation-guide) | мёртвая зона и тактильный отклик тачпада |

## Отчёты тестеров

- **TM2113** (Redmi Book Pro 15 2022, Ryzen): эхо-диалект ответа, свои коды Mi-кнопки, набор
  режимов, отсутствие группы заряда — [issue #37](https://github.com/Oksion/XiControl/issues/37),
  [issue #51](https://github.com/Oksion/XiControl/issues/51). Снимаются скриптом
  `tools/diagnostics/xicontrol-diag.ps1`.

## GUID'ы (WMI, Bitland MIFS)

| Назначение | GUID |
|------------|------|
| Метод `MiInterface` | `B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B` |
| События | `46C93E13-EE9B-4262-8488-563BCA757FEF` |
