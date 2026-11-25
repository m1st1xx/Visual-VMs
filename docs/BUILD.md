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

Получается один файл `publish\VisualVMs.exe` (win-x64, со значком, версия 1.0.0). Настройки single-file публикации заданы в `VisualVMs.csproj` и действуют только при `dotnet publish`. Exe не самодостаточный: нужен .NET 8 Desktop Runtime (x64).

После изменений кода обновите готовую сборку в репозитории: скопируйте `publish\VisualVMs.exe` в `dist\` и закоммитьте. Если `publish\VisualVMs.exe` запущен, publish упадёт с «file is being used by another process» — закройте программу.

Значок `src\VisualVMs\app.ico` генерируется скриптом `tools\make-icon.ps1`:
```
powershell -File tools\make-icon.ps1 -Out "$PWD\src\VisualVMs\app.ico"
```
(путь `-Out` указывайте полный).
