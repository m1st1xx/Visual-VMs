# Архитектура

WPF, .NET 8 (`net8.0-windows`), без внешних NuGet-пакетов в приложении. Тесты — xunit.

## Файлы

| Файл | Ответственность |
|------|-----------------|
| `src/VisualVMs/KvFile.cs` | Построчный редактор файлов VMware `key = "value"` (.vmx, preferences.ini, inventory.vmls). Сохраняет порядок строк, комментарии, BOM, окончания строк (CRLF/LF по каждой строке). Хелперы: NIC (`Nics`, `AddNic`, `RemoveNic`), LAN-сегменты (`Segments`, `EnsureSegment`), библиотека (`RemoveVmListEntry`), `NewPvnId`, `IsPvnId`, `IsSafeValue`. |
| `src/VisualVMs/Model.cs` | Модель схемы (`Scheme`, `VmNode`, `HubNode`, `Nic`, `Edge`), загрузка/сохранение `.vvm` (JSON) с валидацией, `Topology.Infer` (провода из NIC), `Topology.FreeSegmentName`. |
| `src/VisualVMs/Vmware.cs` | Всё общение с VMware: запуск `vmrun`/`vmware.exe`/`vmware-vdiskmanager` (`Run` с таймаутом), включение/выключение, удаление ВМ, чтение библиотеки, правка preferences.ini и inventory.vmls с резервной копией, `ApplyAsync` (транзакционная правка нескольких .vmx), `CreateVm`. |
| `src/VisualVMs/MainWindow.xaml(.cs)` | Холст, значки, провода, контекстные меню, режимы мыши, опрос статуса, файлы схем, `last.txt`. |
| `src/VisualVMs/CreateVmWindow.xaml(.cs)` | Форма «Создать вм» → `CreateVmRequest` (проверка `Vmware.Validate`). |
| `src/VisualVMs/AddVmsWindow.xaml(.cs)` | Список ВМ из библиотеки с галочками и предназначением. |
| `src/VisualVMs/App.xaml(.cs)` | Запуск; `DispatcherUnhandledException` → MessageBox, приложение не падает (если главное окно ещё не загружено — выход). |
| `src/VisualVMs/app.ico`, `tools/make-icon.ps1` | Значок exe и окон (ICO с PNG 16–256 px) и скрипт, который его рисует. |
| `src/VisualVMs/VisualVMs.csproj` | Версия, значок, настройки single-file publish (только при `dotnet publish`). |
| `tests/VisualVMs.Tests/Tests.cs` | Тесты KvFile, Topology, Scheme, Vmware.Validate/Run/SoftStopFailure, смоук-тест XAML и значка. |

## Поток данных

```
.vmx (ethernetN.*)  ──┐
preferences.ini ──────┼─► Resync(): KvFile.Load → vm.Nics, segments
scheme.vvm (позиции) ─┘          │
                                 ▼
                     Topology.Infer(vms, hubs, segments) → edges (+ авто-хабы)
                                 │
                                 ▼
                              Redraw()
```

- **Источник истины для сети — .vmx.** Схема `.vvm` хранит только путь к vmx, предназначение и координаты, плюс хабы (имя + pvnID). Провода никогда не сохраняются — они выводятся.
- `Topology.Infer`: NIC `nat` → провод к «Интернету». NIC `pvn` группируются по pvnID: ровно 2 NIC на 2 разных ВМ и нет хаба с этим pvnID → прямой провод; иначе — хаб (из схемы или авто-хаб с именем сегмента из preferences.ini). `bridged`/`hostonly` и др. не рисуются.
- После каждой операции вызывается `Resync()` — перечитываются все vmx.

## Изменение сети: `Vmware.ApplyAsync(edits)`

1. Для каждой ВМ: загрузить vmx, проверить `EditBlocker` (suspended / зашифрована / не UTF-8) → исключение, ничего не меняется.
2. `vmrun list`: включённые ВМ → вопрос «Выключить?». Для каждой `StopSoftThenHard`: `checkToolsState` (10 с); если не `running` — сразу вопрос о принудительном выключении, иначе `stop soft` (60 с) и при неудаче — тот же вопрос с причиной (таймаут / вывод vmrun); кнопка по умолчанию «Нет»; «Нет» отменяет всю операцию (`false`).
3. Перечитать байты всех vmx, применить правки в памяти (ошибка правки, напр. 10 адаптеров, → файлы не тронуты).
4. Сохранить каждую (резервная копия `.vvm.bak` один раз, запись через `.tmp` + `File.Move`). Если запись одной ВМ упала — уже записанные восстанавливаются из исходных байт (всё или ничего), оставшиеся `<vmx>.tmp` удаляются; пути, которые вернуть не удалось, дописываются в текст ошибки.
5. `MainWindow.Apply` после этого делает `Resync()` и, если был открыт VMware GUI, дописывает предупреждение в строку состояния (не затирая ошибку `Resync`).

Прямое соединение: `Topology.FreeSegmentName("A-B", Vmware.Segments())` → свободное имя → `EnsureSegment` дописывает сегмент в preferences.ini → `ApplyAsync` добавляет NIC `pvn` обеим ВМ.

## Формат схемы `.vvm`

JSON, camelCase, enum строкой:

```json
{
  "version": 1,
  "internet": { "x": 80, "y": 80 },
  "vms": [
    { "vmx": "E:\\VMs\\r1\\r1.vmx", "purpose": "Router", "x": 300, "y": 200 },
    { "vmx": "E:\\VMs\\pc1\\pc1.vmx", "purpose": "Client", "x": 500, "y": 200 }
  ],
  "hubs": [
    { "name": "lan1", "pvnId": "52 ab 01 02 03 04 05 06-07 08 09 0a 0b 0c 0d 0e", "x": 400, "y": 350 }
  ]
}
```

`purpose`: `Router` | `Server` | `Client`. Файл недоверенный: при загрузке отбрасываются `null`-элементы, ВМ без `vmx`, с недопустимыми символами пути, с не полным локальным путём (относительный, `C:a.vmx`, UNC `\\…`/`//…`, `\\?\`) или не на `.vmx`, хабы без имени, с `"`/переводом строки в имени или с pvnID не по формату `^([0-9a-f]{2} ){7}[0-9a-f]{2}-([0-9a-f]{2} ){7}[0-9a-f]{2}$`. Сохранение — через `.tmp` + `File.Move`. Путь последней схемы — `%APPDATA%\VisualVMs\last.txt`. Если схема не загружается (при запуске или через «Открыть»), показывается ошибка и открывается новая схема (с облаком «Интернет»); при запуске `last.txt` удаляется, чтобы ошибка не повторялась.

## Режимы мыши (машина состояний `MainWindow.Mode`)

```
Idle ──"Соединить напрямую"──► PickSource ──щелчок по устройству──► Cable ──щелчок по 2-му──► Connect() ──► Idle
Idle ──щелчок по хабу (без перетаскивания)──► Cable
Idle ──"Удалить подключение"──► PickConnection ──щелчок по проводу/хабу──► RemoveEdge/RemoveHub ──► Idle
Idle ──"Удалить вм"──► PickDelete ──отпускание над ВМ──► меню «Убрать со схемы / Удалить с диска» ──► Idle
любой режим ──Esc / ПКМ──► Idle
```

В `Idle`: перетаскивание значков (порог 4 px), двойной щелчок по ВМ — консоль, ПКМ — контекстное меню. Флаг `busy` (`Busy(...)`) блокирует меню и холст на время операции и не даёт закрыть окно; опрос статуса в это время пропускается.

## Обработка ошибок

- Ошибки операций → MessageBox из `Busy`.
- Ошибки чтения vmx/preferences.ini при `Resync` → строка состояния.
- `Vmware.Run`: таймаут покрывает и ожидание выхода, и чтение stdout/stderr (дочерние процессы VMware могут унаследовать каналы); если к таймауту сам процесс уже завершился (каналы держит дочерний), результат — его код выхода и прочитанный вывод; иначе убивается только сам процесс (`Kill()`, не дерево — в дереве может быть процесс ВМ).
- Любое необработанное исключение UI-потока → MessageBox, приложение продолжает работу; если главное окно ещё не загружено — после сообщения `Shutdown()`.
