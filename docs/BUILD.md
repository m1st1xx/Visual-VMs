# Сборка и тесты (для разработчика)

Нужен .NET 8 SDK. Пути VMware (`vmrun.exe`, `vmware.exe`, `vmware-vdiskmanager.exe`) программа берёт из реестра `HKLM\SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation\InstallPath`; новые ВМ создаются с `virtualHW.version = "22"` (Workstation 17.5+).

```
dotnet build VisualVMs.sln
dotnet test VisualVMs.sln
dotnet run --project src\VisualVMs
```

Тесты (xunit) работают только со строками в памяти и временными файлами; VMware и `%APPDATA%\VMware` не трогают (кроме `cmd.exe`/`ping` для проверки таймаутов запуска процессов).

## Сборка exe

```
dotnet publish src\VisualVMs -c Release -o publish
```

Получается один portable-файл `publish\VisualVMs.exe` (~68 МБ, win-x64, со значком, версия 1.0.0): self-contained single-file со сжатием — .NET runtime внутри, на целевой машине ничего ставить не нужно, права администратора не нужны (манифеста с повышением нет, программа пишет только в профиль пользователя и файлы ВМ). Настройки заданы в `VisualVMs.csproj` и действуют только при `dotnet publish`; обычные `build`/`test` остаются framework-dependent.

Проверено в песочнице (Windows 10): папка `C:\Program Files\dotnet` временно переименована, exe запущен через `runas /trustlevel:0x20000` (токен без прав администратора) — окно открывается, «Добавить вм» читает библиотеку VMware.

После изменений кода обновите готовую сборку в репозитории: скопируйте `publish\VisualVMs.exe` в `dist\` и закоммитьте. Если `publish\VisualVMs.exe` запущен, publish упадёт с «file is being used by another process» — закройте программу.

Значок `src\VisualVMs\app.ico` генерируется скриптом `tools\make-icon.ps1`:
```
powershell -File tools\make-icon.ps1 -Out "$PWD\src\VisualVMs\app.ico"
```
(путь `-Out` указывайте полный).
