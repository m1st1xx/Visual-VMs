using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace VisualVMs;

public record CreateVmRequest(string Name, string GuestOs, string Iso, int RamMb, int Cpus, int DiskGb, Purpose Purpose);

/// <summary>All talking to VMware Workstation: vmrun, vmware.exe, vdiskmanager, preferences.ini, inventory.vmls.</summary>
public static class Vmware
{
    public static readonly string Dir =
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\VMware, Inc.\VMware Workstation", "InstallPath", null) is string { Length: > 0 } p
            ? p : @"C:\Program Files\VMware\VMware Workstation\";
    static string Vmrun => Path.Combine(Dir, "vmrun.exe");
    static string VmwareExe => Path.Combine(Dir, "vmware.exe");
    static string VdiskManager => Path.Combine(Dir, "vmware-vdiskmanager.exe");
    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string PrefsPath => Path.Combine(AppData, "VMware", "preferences.ini");
    static string InventoryPath => Path.Combine(AppData, "VMware", "inventory.vmls");

    public static async Task<(bool Ok, string Out)> Run(string exe, string[] args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(true);
            return (false, $"Превышено время ожидания: {Path.GetFileName(exe)} {string.Join(" ", args)}");
        }
        return (p.ExitCode == 0, ((await stdout) + (await stderr)).Trim());
    }

    static void Check((bool Ok, string Out) r)
    {
        if (!r.Ok) throw new InvalidOperationException(r.Out.Length > 0 ? r.Out : "Команда VMware завершилась с ошибкой");
    }

    static string Full(string path) => Path.GetFullPath(path);

    public static async Task<HashSet<string>> ListRunning()
    {
        var r = await Run(Vmrun, ["-T", "ws", "list"]);
        Check(r);
        return r.Out.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task Start(string vmx) => Check(await Run(Vmrun, ["-T", "ws", "start", vmx, "nogui"], 120000));

    public static async Task StopSoftThenHard(string vmx)
    {
        if (!(await Run(Vmrun, ["-T", "ws", "stop", vmx, "soft"], 60000)).Ok)
            Check(await Run(Vmrun, ["-T", "ws", "stop", vmx, "hard"]));
        await WaitStopped(vmx);
    }

    static async Task WaitStopped(string vmx)
    {
        for (int i = 0; i < 30; i++)
        {
            if (!(await ListRunning()).Contains(Full(vmx))) { await Task.Delay(2000); return; }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException($"ВМ не выключилась за 30 с: {vmx}");
    }

    public static async Task DeleteVm(string vmx)
    {
        if ((await ListRunning()).Contains(Full(vmx)))
        {
            Check(await Run(Vmrun, ["-T", "ws", "stop", vmx, "hard"]));
            await WaitStopped(vmx);
        }
        Check(await Run(Vmrun, ["-T", "ws", "deleteVM", vmx], 120000));
    }

    static void Launch(params string[] args)
    {
        var psi = new ProcessStartInfo(VmwareExe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    public static void OpenConsole(string vmx) => Launch("-x", vmx);

    public static bool GuiRunning => Process.GetProcessesByName("vmware").Length > 0;

    // ---- preferences.ini / inventory ----

    static KvFile? LoadPrefs() => File.Exists(PrefsPath) ? KvFile.Load(PrefsPath) : null;

    public static Dictionary<string, string> Segments() => LoadPrefs()?.Segments() ?? new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultVmPath() =>
        LoadPrefs()?.Get("prefvmx.defaultVMPath") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Virtual Machines");

    static bool prefsBackedUp;

    static bool EditPrefs(Func<KvFile, bool> edit)
    {
        var k = LoadPrefs() ?? new KvFile(".encoding = \"UTF-8\"\n");
        if (!edit(k)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
        if (!prefsBackedUp && File.Exists(PrefsPath)) File.Copy(PrefsPath, PrefsPath + ".vvm.bak", true);
        prefsBackedUp = true;
        k.Save(PrefsPath);
        return true;
    }

    /// <summary>Returns the pvnID of the named LAN segment, creating it in preferences.ini if needed.</summary>
    public static string EnsureSegment(string name)
    {
        string id = "";
        EditPrefs(k => k.EnsureSegment(name, out id));
        return id;
    }

    public static bool EnsureSegment(string name, string pvnId) => EditPrefs(k => k.EnsureSegment(name, pvnId));

    public static List<(string Name, string Vmx)> ReadInventory()
    {
        if (!File.Exists(InventoryPath)) return new();
        var k = KvFile.Load(InventoryPath);
        var list = new List<(string, string)>();
        foreach (var key in k.Keys)
        {
            var m = Regex.Match(key, @"^(vmlist\d+)\.config$", RegexOptions.IgnoreCase);
            if (!m.Success || k.Get(key) is not { Length: > 0 } vmx || !vmx.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase) || !File.Exists(vmx)) continue;
            list.Add((k.Get(m.Groups[1].Value + ".DisplayName") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(vmx), Full(vmx)));
        }
        return list;
    }

    // ---- VM files ----

    public static string? EditBlocker(string vmx, KvFile k)
    {
        if (!string.IsNullOrEmpty(k.Get("checkpoint.vmState")) || Directory.GetFiles(Path.GetDirectoryName(Full(vmx))!, "*.vmss").Length > 0)
            return "ВМ приостановлена — включите и выключите её в VMware";
        if (k.Get("encryption.data") != null) return "ВМ зашифрована — сеть меняйте в VMware";
        if (!k.CanEdit) return "vmx не в UTF-8 — сеть меняйте в VMware";
        return null;
    }

    public static string DisplayName(string vmx, KvFile k) => k.Get("displayName") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(vmx);

    /// <summary>Applies edits to several vmx at once, powering off running VMs first. Returns false if the user cancelled.</summary>
    public static async Task<bool> ApplyAsync(List<(string Vmx, Action<KvFile> Edit)> edits)
    {
        var paths = edits.Select(e => Full(e.Vmx)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vmx in paths)
        {
            var k = KvFile.Load(vmx);
            names[vmx] = DisplayName(vmx, k);
            if (EditBlocker(vmx, k) is { } err) throw new InvalidOperationException($"{names[vmx]}: {err}");
        }
        var running = await ListRunning();
        var on = paths.Where(running.Contains).ToList();
        if (on.Count > 0)
        {
            if (MessageBox.Show($"ВМ включена: {string.Join(", ", on.Select(v => names[v]))}. Выключить (мягко, при неудаче — принудительно) и применить?",
                    "Visual VMs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            foreach (var vmx in on) await StopSoftThenHard(vmx);
        }
        // edit everything in memory first so a failing edit (e.g. 10 NICs) leaves all files untouched
        var loaded = paths.Select(v => (Vmx: v, K: KvFile.Load(v))).ToList();
        foreach (var (vmx, k) in loaded)
            foreach (var e in edits.Where(e => Full(e.Vmx).Equals(vmx, StringComparison.OrdinalIgnoreCase))) e.Edit(k);
        foreach (var (vmx, k) in loaded)
        {
            File.Copy(vmx, vmx + ".vvm.bak", true);
            k.Save(vmx);
        }
        return true;
    }

    public static string? Validate(CreateVmRequest r, string root)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Укажите имя";
        if (r.Name.IndexOfAny(Path.GetInvalidFileNameChars().Append('"').Append('|').ToArray()) >= 0 || r.Name.Trim() != r.Name || r.Name.EndsWith('.'))
            return "Имя содержит недопустимые символы";
        var dir = Path.Combine(root, r.Name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) return $"Папка уже существует и не пуста: {dir}";
        if (!File.Exists(r.Iso)) return "ISO-файл не найден";
        if (r.RamMb < 256 || r.RamMb % 4 != 0) return "ОЗУ: не меньше 256 МБ и кратно 4";
        if (r.Cpus is < 1 or > 64) return "Ядра: от 1 до 64";
        if (r.DiskGb is < 1 or > 2000) return "Диск: от 1 до 2000 ГБ";
        return null;
    }

    /// <summary>Creates folder, disk and vmx, then opens it in Workstation (which registers it). Returns the vmx path.</summary>
    public static async Task<string> CreateVm(CreateVmRequest r)
    {
        var root = DefaultVmPath();
        if (Validate(r, root) is { } err) throw new InvalidOperationException(err);
        var dir = Path.Combine(root, r.Name);
        bool existed = Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        var vmx = Path.Combine(dir, r.Name + ".vmx");
        try
        {
            Check(await Run(VdiskManager, ["-c", "-s", $"{r.DiskGb}GB", "-a", "lsilogic", "-t", "1", Path.Combine(dir, r.Name + ".vmdk")], 120000));
            var k = new KvFile("");
            void S(string key, string value) => k.Set(key, value);
            S(".encoding", "UTF-8");
            S("config.version", "8");
            S("virtualHW.version", "22");
            S("displayName", r.Name);
            S("guestOS", r.GuestOs);
            S("numvcpus", r.Cpus.ToString());
            S("memsize", r.RamMb.ToString());
            S("pciBridge0.present", "TRUE");
            foreach (var b in new[] { 4, 5, 6, 7 })
            {
                S($"pciBridge{b}.present", "TRUE");
                S($"pciBridge{b}.virtualDev", "pcieRootPort");
                S($"pciBridge{b}.functions", "8");
            }
            S("sata0.present", "TRUE");
            S("sata0:0.present", "TRUE");
            S("sata0:0.fileName", r.Name + ".vmdk");
            S("sata0:1.present", "TRUE");
            S("sata0:1.deviceType", "cdrom-image");
            S("sata0:1.fileName", Full(r.Iso));
            S("sata0:1.startConnected", "TRUE");
            S("floppy0.present", "FALSE");
            S("usb.present", "TRUE");
            k.AddNic("nat");
            if (r.GuestOs == "windows11-64")
            {
                S("firmware", "efi");
                S("uefi.secureBoot.enabled", "TRUE");
                S("managedVM.autoAddVTPM", "software");
            }
            k.Save(vmx);
            Launch(vmx);
            return vmx;
        }
        catch
        {
            // the folder was ours and empty before: put it back the way it was
            Directory.Delete(dir, true);
            if (existed) Directory.CreateDirectory(dir);
            throw;
        }
    }
}
