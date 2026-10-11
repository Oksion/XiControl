# Проверка драйверов и BIOS у OEM

Как Xiaomi проверяет обновления драйверов и прошивок для ноутбука — протокол, разобранный
до состояния «можно повторить без их софта». Нужен, если мы захотим показывать такую проверку
в XiControl: канал driver-free, обычный HTTPS плюс WMI, ни драйверов, ни служб Xiaomi не требуется.

Разбор снят 2026-10-10 на Xiaomi Book Pro 14 (TM2424): Xiaomi PC Manager 5.8.0.57 +
служба MiService 4.1.1.564. Сырые данные захвата — в `ota-capture/` (вне репозитория,
см. [«Где лежат данные»](#5-где-лежат-данные)).

---

## 1. Кто за что отвечает

| Компонент | Роль |
|---|---|
| Xiaomi PC Manager (`SvrCModule.dll`) | **только UI**. `sc::DriverManagerModule` / `DriverClientManager` гонят protobuf `xsa.*` (`StartScanDriver`, `GetDriversDetail`, `InstallDriver`) по named pipe. Сам в сеть за драйверами не ходит |
| **MiService / MiDeviceService** (`MiDeviceServiceEntry.dll`) | **бэкенд**: `driver::ScanTask` (включая `ScanFirmware` и «scan bios»), `driver::GetDriverInfoFromServerOTA`, `OTA::UpdateStrategyManagerImpl::ConstructPostData`, `firmware::GetFirmwareInfos`. Живёт и после удаления PC Manager |
| `FirmwareScreenTool.exe` | версия прошивки панели локально (IGCL/HX), пишет её в реестр |

То есть сетевая часть целиком в службе. PC Manager нужен лишь чтобы нажать «проверить».
Что ещё делают эти службы, чем мешают XiControl и как их отключить — [17-oem-services.md](17-oem-services.md).

## 2. Запрос

Один запрос на **один** модуль; полный скан = 26 запросов.

```
POST https://whippet.bsp.xiaomi.com/api/v2/nb/checkUpdate
Content-Type: application/x-www-form-urlencoded
User-Agent: mi-service

data="<base64(root)>"
```

```jsonc
// root
{"body":{…},"header":{"appid":"nb-DBRUc9Fdui","sign":"<MD5(appid+body+key), ВЕРХНИЙ регистр>"}}

// body — ключи строго по алфавиту, компактный JSON без пробелов
{"category":"driver","lan":"ru_RU","modules":[{"moduleName":"NPU"}],
 "product":"TM2424","region":"RU","sn":"77079/6d4fe817…","winBuildNumber":"26200"}
```

Порядок ключей не случайный: тело собирается `nlohmann::json` на `std::map`, то есть всегда
по алфавиту. Любое отступление ломает подпись.

### Поля

| Поле | Откуда берётся | Примечание |
|---|---|---|
| `category` | `"driver"` | **для всех модулей, включая прошивки** (`FWTP`, `FWSC`). Отдельно идёт один запрос с `"bios"` |
| `modules` | `[{"moduleName":"<код>"}]` | **без `version`**: спрашивается «какая последняя», установленная версия серверу не сообщается. Исключение — `category: bios` |
| `product` | реестр `HKLM\SOFTWARE\MI\MiDeviceService\ProductModel` (= `Win32_BaseBoard.Product`) | `TM2424`. **Не** SKU `TM2424-77079` и не `77079` |
| `sn` | `<часть1>/<md5_lower(весь IdentifyingNumber)>` | `IdentifyingNumber` = `77079/26S400093` → `77079/6d4fe817…`. **Настоящий серийник не передаётся**, только хеш. Поле необязательное: без него ответ тот же |
| `lan` / `region` | системная локаль (`GetUserDefaultUILanguage` + `GetLocaleInfoEx`) | на `ru_RU` / `RU` сервер работает нормально — региональной блокировки нет |
| `winBuildNumber` | билд Windows | `26200` |

Подпись: `MD5(appid + body + key)`, верхний регистр. `appid` = `nb-DBRUc9Fdui`,
ключ = `OH3QAmMrso1DhOm4` — оба зашиты в `MiDeviceServiceEntry.dll`.
Есть и моки для отладки: `HKLM\SOFTWARE\MI\Driver` → `MockUrl` / `MockAppId` / `MockAppkey` / `MockProduct`
(на штатной машине пусто). Staging-адрес в бинаре: `matrix.whippet-updater.staging.mi.com`.

### Коды модулей

Драйверы: `WLC WC IGC IDC SC BT FR TBT ISH GNA HID CG ME SIO HR RST NPU MEP PMT HDR NFC OTS`.
Прошивки: `BIOS`, `FWTP` (тачпад), `FWSC` (экран).

### Отдельная ветка BIOS

Единственный запрос, который несёт версию:

```jsonc
{"category":"bios","modules":[{"moduleName":"BIOS","version":"1.0.0.2570"}], …}
```

Версия кодируется хитро: `1.0.0.` + **десятичное от последних четырёх hex-символов**
`Win32_BIOS.SMBIOSBIOSVersion`. Формат `1.0.0.%04d` зашит в бинаре.

```
XMAPT4B0P0A0A → "0A0A" → 0x0A0A = 2570 → "1.0.0.2570"
```

## 3. Ответ

```jsonc
{"code":0,"message":"ok","traceId":"…","modules":[{
  "product":"TM2424","category":"driver","moduleName":"NPU",
  "latestVer":{
    "version":"NS","versionSerial":1788165356520,"displayVersion":"N.S","logInfo":{},
    "packages":[{"type":"FULL_PACKAGE","md5":"9e6fc14c…","fileSize":35632547,
                 "fileName":"MO3n_08.Intel_PTL_NPU_32.0.100.4841.zip",
                 "mirrors":["https://cdn.cnbj1.fds.api.mi-img.com/ota-packages/…zip"]}],
    "strategy":{"upgradeChannel":"MISERVICE","powerRequireSw":0,"manualUpgrade":0,"newInstall":0,
                "hardwareIds":[{"id":"PCI\\VEN_8086&DEV_B03E","releaseTime":"2026-07-24",
                                "version":"32.0.100.4841"}],
                "installScripts":[{"pt":"win11","path":"08.Intel_…/install.bat"}],
                "autoRestart":0,"autoExceptionCheck":0,"autoUpdate":0}}}]}
```

Важные грабли:

- **Настоящая версия — в `strategy.hardwareIds[].version`.** Поле `latestVer.version` всегда
  заглушка `"NS"`, `displayVersion` — `"N.S"`.
- **Один пакет — много версий.** Внутри `hardwareIds` версии **разные**: у аудио (`SC`) 125
  hardware-id и 10 различных версий (архив везёт Intel SST, Realtek и Dolby, у каждого своя),
  у графики — 59 id и 6 версий. Поэтому «версии пакета» не существует: сравнивать можно только
  попарно, наш hardware-id → его запись в каталоге.
- `modules: []` означает «для этого типа на эту модель пакета нет», а **не** «нет обновлений».
  На захваченном скане пакет пришёл в 17 ответах из 26.
- Ошибки сервера: неверная подпись → `{"code":500,"message":"签名校验失败"}`;
  пустой `data` → `{"code":500,"message":"服务未注册"}`.
- **`category:"application"` не существует.** С ней сервер отвечает `code:0 ok` и пустым
  `modules` — молча, без ошибки. На эту ловушку легко потратить часы.

### Как из ответов получается каталог

Служба склеивает `hardwareIds` × пакет в плоскую карту `{hardware_id → пакет}` и кладёт в
`…\MiService\<ver>\MiDeviceService\driver_cache.dat`. Отсюда 1259 hardware-id при 18 пакетах:
один zip закрывает десятки ревизий и конфигураций одной модели.

## 4. Что из этого можно сделать у нас

> **Сделано в XiControl (XIC-95):** вкладка «Настройки → Драйверы». Запрос — только по кнопке,
> без `sn`, со своим User-Agent `XiControl-driver-check` (сервер его принимает, выдавать себя за
> `mi-service` не нужно); 24 модуля + BIOS параллельно по 4, полный скан на TM2424 — ~4 с. Код —
> `src/SystemIntegration/OemDrivers.cs`: `OemProtocol` (тело, подпись, разбор — подпись сверена с
> захватом службы до символа), `DriverMatch` (сверка попарно по hardware-id), `OemDriverCheck`.
> Показываем сводку по модулям и подробно — только обновления, со ссылкой на пакет (второе нажатие
> открывает её в браузере). У BIOS — тоже ссылка, но под отдельным предупреждением «на свой страх
> и риск» (решение владельца: иначе родная утилита оставалась бы нужна ради одного BIOS). Прошивки `FWTP`/`FWSC` с версией драйвера
> не сравниваются: строка «в каталоге есть прошивка X». Ничего не скачиваем и не ставим.

Сверка «что установлено против того, что есть у OEM» делается без сети и без Xiaomi-софта:

1. Берём каталог — из ответа сервера (запрос выше) или из `driver_cache.dat`, если служба стоит.
2. Перечисляем установленное через `Win32_PnPSignedDriver` (`HardwareID` + `CompatID`).
3. Сопоставляем: **hardware-id каталога — префикс установленного id**
   (`ACPI\INTC105F` ⊂ `ACPI\INTC105F&…`), и версию берём **из записи этого же hardware-id**,
   а не «пакета» (см. выше: в одном пакете версии разные).
4. Текущий BIOS — `Win32_BIOS.SMBIOSBIOSVersion`, сравнивать строкой.

Проверено на железе: живой запрос по модулям WLC/IGC/SC/CG вернул каталог, сверка дала осмысленную
картину — у аудио и чипсета всё совпало до версии, по графике и Wi-Fi установлено новее каталога.

**Загрузка работает** (проверено на самом маленьком пакете, 27 326 байт): прямой GET по
`mirrors[0]`, без авторизации и токенов, размер и `md5` из каталога совпали. Зеркало ровно одно.

Установка — сознательно **не делаем**: это запуск их `install.bat` от администратора. Любопытно,
что сам батник — лишь обёртка (`DeviceCheck.exe` по hardware-id, затем
`pnputil /add-driver <inf> /install`), то есть при желании обошлись бы штатным механизмом Windows
без их скрипта. Но проверен ровно один, простейший пакет; аудио и графика почти наверняка гонят
вендорский `setup.exe`. BIOS тем более не ставим сами — только ссылка с предупреждением.

**Ограничения:**

- каталог живёт у них на сервере и устаревает в кэше: полагаться только на локальный
  `driver_cache.dat` нельзя — он даст «всё актуально» там, где это уже не так;
- метаданные местами мусорные (у чипсета `releaseTime` = `1968-07-18`) — даты показывать с оглядкой;
- **текущие версии прошивок тачпада (`FWTP`) и экрана (`FWSC`) читать пока нечем.** Служба берёт
  их через `firmware::GetFirmwareVersionById` — реестр плюс `FirmwareScreenTool.exe`, то есть части
  MiService. Без неё скажем «есть пакет версии X», но не «у вас старее». Возможный путь для
  тачпада — наш канал `COL05` (в `SvrCModule.dll` есть `sc::TouchSettingManager::ReadFirmware`),
  не проверено.

## 5. Где лежат данные

Захват не в репозитории (сырые ответы содержат хеш серийника машины) — он в рабочем каталоге
проекта, `ota-capture/`:

| Файл | Что это |
|---|---|
| `driver_cache.dat` | каталог от сервера, как его хранит служба: 1259 hwid / 18 пакетов |
| `ota_requests.jsonl` | 26 реальных запросов (тело до base64) |
| `ota_responses.jsonl` | 26 ответов на них |
| `scan_log_2026-10-10.txt` | срез лога службы за этот скан |
| `README.md` | тот же протокол кратко, рядом с данными |

Рядом же `ota-check.ps1` — рабочий скрипт-прототип (PowerShell 5.1 и 7, админ не нужен):

| Запуск | Что делает |
|---|---|
| `-Diag` | диагностика: как определилась модель, выведенная версия BIOS, запрос по 4 модулям + BIOS, сверка. Вывод можно целиком прислать разработчику — `sn` усечён |
| (без аргументов) | полная проверка: все 25 модулей + BIOS, сверка, ссылки на то, что новее |
| `-Offline` | без сети, сверка по `driver_cache.dat` (нужна установленная служба) |
| `-Module WLC,IGC` | опросить только эти модули |

**`-Diag` — штатный способ проверить другую модель:** человек с другим ноутбуком запускает его у
себя и присылает вывод. Самое интересное в нём — вывелась ли версия BIOS (правило «последние 4 hex»
снято с одной машины) и вернул ли сервер каталог для этого `product`.

## 6. Как снять заново

Если понадобится свежий каталог или проверка на другой модели:

1. Поставить Xiaomi PC Manager (китайский, при желании с англификатором — он только переводит UI).
2. Открыть «Управление драйверами» → «Проверить».
3. Забрать из `C:\ProgramData\MI\MiDeviceService\log\MiDeviceService_log.txt` строки
   `[OTA] Post json val =` (запрос) и `[OTA] Post response val =` (ответ).
4. PC Manager можно удалять: служба `MiService` остаётся и `driver_cache.dat` вместе с ней.

Подпись проверяется локально: взять `body` из лога, посчитать `MD5(appid+body+key)` и
сверить с `sign` того же запроса — так алгоритм и подтвердился.

---

Связанное: [04-references.md](04-references.md) — источники и лицензии;
[05-open-questions.md](05-open-questions.md) — открытые и закрытые driver-free вопросы.
