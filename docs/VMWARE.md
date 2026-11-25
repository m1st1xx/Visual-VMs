# Взаимодействие с VMware Workstation

Папка установки: реестр `HKLM\SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation` → `InstallPath`, иначе `C:\Program Files\VMware\VMware Workstation\`.

Все файлы читаются/пишутся через `KvFile`: строгое UTF-8 (неверные байты → файл только для чтения), BOM и окончания строк сохраняются, значения с `"`, `\r`, `\n` запрещены, запись — `файл.tmp` → `File.Move(overwrite)`.

## .vmx

Читается:

| Ключ | Зачем |
|------|-------|
| `displayName` | имя на схеме (иначе имя файла) |
| `guestOS` | `windows*` → `virtualDev = e1000e`, иначе `e1000` (если у ВМ ещё нет адаптеров) |
| `.encoding` | не `UTF-8` → правка запрещена |
| `checkpoint.vmState` (непусто) или `*.vmss` в папке | ВМ приостановлена → правка запрещена |
| `encryption.data` | ВМ зашифрована → правка запрещена |
| `ethernetN.present` (`N` = 0..9) | адаптер существует, если `TRUE` |
| `ethernetN.connectionType` | `nat` / `pvn` / `bridged` (по умолчанию) / `hostonly` / `custom` |
| `ethernetN.pvnID` | id LAN-сегмента для `pvn` |
| `ethernetN.virtualDev` | копируется в новый адаптер |

Пишется при добавлении адаптера (`AddNic`, первый свободный `N`; сначала удаляются все старые `ethernetN.*`):

```
ethernetN.present = "TRUE"
ethernetN.connectionType = "pvn"        # или "nat"
ethernetN.pvnID = "xx xx xx xx xx xx xx xx-xx xx xx xx xx xx xx xx"   # только для pvn
ethernetN.virtualDev = "e1000"          # как у других адаптеров ВМ
ethernetN.addressType = "generated"
```

Удаление адаптера (`RemoveNic`) — удаляются все ключи с префиксом `ethernetN.` (с точкой: `ethernet1.` не задевает `ethernet10.`), включая добавленные VMware `pciSlotNumber`, `generatedAddress*`.

Новая ВМ (`CreateVm`): `.encoding`, `config.version = 8`, `virtualHW.version = 22`, `displayName`, `guestOS`, `numvcpus`, `memsize`, `pciBridge0.present`, `pciBridge4..7.{present,virtualDev=pcieRootPort,functions=8}`, `sata0.present`, `sata0:0.{present,fileName=<имя>.vmdk}`, `sata0:1.{present,deviceType=cdrom-image,fileName=<iso>,startConnected}`, `floppy0.present = FALSE`, `usb.present`, NAT-адаптер `ethernet0`; для `windows11-64` ещё `firmware = efi`, `uefi.secureBoot.enabled`, `managedVM.autoAddVTPM = software`.

## %APPDATA%\VMware\preferences.ini

| Ключ | Чтение/запись |
|------|---------------|
| `pref.namedPVNsN.name`, `pref.namedPVNsN.pvnID` | LAN-сегменты (просматриваются индексы до `max(count, 64)`, пропуски допустимы). Новый сегмент дописывается в индекс `max(count, последний занятый + 1)`. |
| `pref.namedPVNs.count` | обновляется до `индекс + 1` |
| `prefvmx.defaultVMPath` | папка для новых ВМ (иначе `%USERPROFILE%\Documents\Virtual Machines`) |

Если файла нет, он создаётся с `.encoding = "UTF-8"`. Открытый VMware GUI держит настройки в памяти и может перезаписать файл — тогда имя сегмента пропадёт (pvnID в vmx остаётся, сеть работает). Самовосстановление: при открытии схемы, если VMware закрыт, хабы схемы дописываются обратно (`EnsureSegment(name, pvnId)`).

## %APPDATA%\VMware\inventory.vmls (библиотека)

- Чтение («Добавить вм»): `vmlistN.config` (путь к .vmx, только существующие файлы) и `vmlistN.DisplayName`.
- После `deleteVM`, если VMware GUI не запущен: удаляются все ключи `vmlistN.*` той записи, у которой `vmlistN.config` совпадает с полным путём удалённого vmx (без учёта регистра). Ключи `index*` не трогаются. Если GUI запущен — файл не трогается, в строке состояния просьба удалить ВМ из библиотеки вручную.

## Команды

| Действие | Команда | Таймаут |
|----------|---------|---------|
| Статус (каждые 3 с) | `vmrun -T ws list` | 30 с |
| Включить | `vmrun -T ws start <vmx> nogui` | 120 с |
| Выключить | `vmrun -T ws checkToolsState <vmx>`; если `running` — `stop <vmx> soft`; если Tools не запущены или soft не удался — после согласия пользователя `... hard` | 10 с / 60 с / 30 с |
| Ожидание выключения | `vmrun list` раз в 1 с, до 30 раз, затем пауза 2 с | — |
| Удалить с диска | (если включена) `stop hard` → `vmrun -T ws deleteVM <vmx>` | 120 с |
| Открыть консоль | `vmware.exe <vmx>` — открывает вкладку, **не включает** (ключ `-x` включил бы ВМ) | — |
| Создать диск | `vmware-vdiskmanager -c -s <N>GB -a lsilogic -t 1 <имя>.vmdk` | 120 с |
| Зарегистрировать новую ВМ | `vmware.exe <vmx>` | — |
| GUI запущен? | есть процесс `vmware` | — |

По таймауту убивается только сам запущенный процесс (`Process.Kill()` без дерева). Если он к этому моменту уже завершился, а каналы вывода держит дочерний процесс VMware, считается его код выхода (не таймаут).

## Резервные копии

Перед первой записью файла рядом создаётся `<файл>.vvm.bak` (`<вм>.vmx.vvm.bak`, `preferences.ini.vvm.bak`, `inventory.vmls.vvm.bak`). Если копия уже есть — не перезаписывается (там оригинал до первого вмешательства программы). После «Удалить с диска» `<вм>.vmx.vvm.bak` удаляется вместе с опустевшей папкой ВМ (не рекурсивно: чужие файлы остаются; папка удаляется, только если её имя совпадает с именем vmx без расширения).
