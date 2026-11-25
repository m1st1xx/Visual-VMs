using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualVMs;

/// <summary>Line-preserving editor for VMware key = "value" files (.vmx, preferences.ini, inventory.vmls).</summary>
public class KvFile
{
    static readonly Regex KeyRx = new(@"^\s*([^=]+?)\s*=\s*""(.*)""\s*$");
    readonly List<string> lines;
    readonly string nl;
    readonly bool bom;

    public KvFile(string text, bool bom = false)
    {
        nl = text.Contains("\r\n") ? "\r\n" : "\n";
        lines = text.Split(nl).ToList();
        this.bom = bom;
    }

    public static KvFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new KvFile(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public override string ToString() => string.Join(nl, lines);

    /// <summary>Writes to path.tmp, then moves over path so a crash never leaves a half-written file.</summary>
    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", ToString(), new UTF8Encoding(bom));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>False when .encoding is set to something other than UTF-8: we would corrupt non-ASCII text.</summary>
    public bool CanEdit => Get(".encoding") is not { } e || e.Equals("UTF-8", StringComparison.OrdinalIgnoreCase);

    int Find(string key)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var m = KeyRx.Match(lines[i]);
            if (m.Success && m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public IEnumerable<string> Keys => lines.Select(l => KeyRx.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value);

    public string? Get(string key)
    {
        int i = Find(key);
        return i < 0 ? null : KeyRx.Match(lines[i]).Groups[2].Value;
    }

    void CheckEdit()
    {
        if (!CanEdit) throw new InvalidOperationException($"Файл в кодировке {Get(".encoding")} — изменение не поддерживается");
    }

    public void Set(string key, string value)
    {
        CheckEdit();
        int i = Find(key);
        if (i >= 0) { lines[i] = $"{KeyRx.Match(lines[i]).Groups[1].Value} = \"{value}\""; return; }
        // keep a trailing newline at the end of the file
        int at = lines.Count > 0 && lines[^1] == "" ? lines.Count - 1 : lines.Count;
        lines.Insert(at, $"{key} = \"{value}\"");
    }

    public void RemoveWhere(string keyPrefix)
    {
        CheckEdit();
        lines.RemoveAll(l => KeyRx.Match(l) is { Success: true } m && m.Groups[1].Value.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase));
    }

    // ---- NICs (.vmx) ----

    public List<Nic> Nics()
    {
        var list = new List<Nic>();
        for (int n = 0; n < 10; n++)
            if (string.Equals(Get($"ethernet{n}.present"), "TRUE", StringComparison.OrdinalIgnoreCase))
                list.Add(new Nic(n, (Get($"ethernet{n}.connectionType") ?? "bridged").ToLowerInvariant(), Get($"ethernet{n}.pvnID")));
        return list;
    }

    public int AddNic(string type, string? pvnId = null)
    {
        var existing = Nics();
        int n = Enumerable.Range(0, 10).Where(i => existing.All(x => x.Index != i)).DefaultIfEmpty(-1).First();
        if (n < 0) throw new InvalidOperationException("Максимум 10 адаптеров");
        string dev = existing.Select(x => Get($"ethernet{x.Index}.virtualDev")).FirstOrDefault(d => !string.IsNullOrEmpty(d))
            ?? ((Get("guestOS") ?? "").StartsWith("windows", StringComparison.OrdinalIgnoreCase) ? "e1000e" : "e1000");
        RemoveWhere($"ethernet{n}.");
        Set($"ethernet{n}.present", "TRUE");
        Set($"ethernet{n}.connectionType", type);
        if (type == "pvn") Set($"ethernet{n}.pvnID", pvnId ?? throw new ArgumentNullException(nameof(pvnId)));
        Set($"ethernet{n}.virtualDev", dev);
        Set($"ethernet{n}.addressType", "generated");
        return n;
    }

    // trailing dot so ethernet1. does not hit ethernet10.
    public void RemoveNic(int n) => RemoveWhere($"ethernet{n}.");

    public static string NewPvnId()
    {
        var b = RandomNumberGenerator.GetBytes(16).Select(x => x.ToString("x2")).ToArray();
        return string.Join(" ", b[..8]) + "-" + string.Join(" ", b[8..]);
    }

    // ---- LAN segments (preferences.ini) ----

    int SegCount => int.TryParse(Get("pref.namedPVNs.count"), out var c) ? c : 0;

    /// <summary>pvnID -> display name.</summary>
    public Dictionary<string, string> Segments()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Math.Max(SegCount, 64); i++)
            if (Get($"pref.namedPVNs{i}.name") is { Length: > 0 } name && Get($"pref.namedPVNs{i}.pvnID") is { Length: > 0 } id)
                map.TryAdd(id, name);
        return map;
    }

    /// <summary>Finds a segment by name or appends a new one. Returns whether the file changed.</summary>
    public bool EnsureSegment(string name, out string pvnId)
    {
        foreach (var (id, n) in Segments())
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) { pvnId = id; return false; }
        pvnId = NewPvnId();
        Append(name, pvnId);
        return true;
    }

    /// <summary>Re-adds a known name/id pair if its id is missing (scheme self-heal).</summary>
    public bool EnsureSegment(string name, string pvnId)
    {
        if (Segments().ContainsKey(pvnId)) return false;
        Append(name, pvnId);
        return true;
    }

    void Append(string name, string pvnId)
    {
        int highest = -1;
        for (int k = 0; k < Math.Max(SegCount, 64); k++)
            if (Get($"pref.namedPVNs{k}.name") != null || Get($"pref.namedPVNs{k}.pvnID") != null) highest = k;
        int i = Math.Max(SegCount, highest + 1);
        Set($"pref.namedPVNs{i}.name", name);
        Set($"pref.namedPVNs{i}.pvnID", pvnId);
        Set("pref.namedPVNs.count", (i + 1).ToString());
    }
}
