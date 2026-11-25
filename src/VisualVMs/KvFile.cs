using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualVMs;

/// <summary>Line-preserving editor for VMware key = "value" files (.vmx, preferences.ini, inventory.vmls).</summary>
public class KvFile
{
    static readonly Regex KeyRx = new(@"^\s*([^=]+?)\s*=\s*""(.*)""\s*$");
    static readonly Regex PvnRx = new(@"^([0-9a-f]{2} ){7}[0-9a-f]{2}-([0-9a-f]{2} ){7}[0-9a-f]{2}$", RegexOptions.IgnoreCase);
    // split on '\n' only; each line keeps its own trailing '\r', so mixed CRLF/LF files round-trip and keys are never merged
    readonly List<string> lines;
    readonly bool crlf, bom;
    readonly string? notUtf8;

    public KvFile(string text, bool bom = false) : this(text, bom, null) { }

    KvFile(string text, bool bom, string? notUtf8)
    {
        crlf = text.Contains("\r\n");
        lines = text.Split('\n').ToList();
        this.bom = bom;
        this.notUtf8 = notUtf8;
    }

    public static KvFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static KvFile Parse(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int skip = bom ? 3 : 0;
        try { return new KvFile(new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip), bom, null); }
        catch (DecoderFallbackException)
        {
            // still readable for ASCII keys, but saving would replace the invalid bytes with U+FFFD
            return new KvFile(Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip), bom, "Файл не в UTF-8 — меняйте сеть в VMware");
        }
    }

    public static bool IsPvnId(string? s) => s != null && PvnRx.IsMatch(s);

    public static bool IsSafeValue(string? s) => s != null && s.IndexOfAny(new[] { '"', '\r', '\n' }) < 0;

    public override string ToString() => string.Join("\n", lines);

    /// <summary>Writes to path.tmp, then moves over path so a crash never leaves a half-written file.</summary>
    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", ToString(), new UTF8Encoding(bom));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>False when the bytes are not valid UTF-8 or .encoding is something else: we would corrupt non-ASCII text.</summary>
    public bool CanEdit => notUtf8 == null && (Get(".encoding") is not { } e || e.Equals("UTF-8", StringComparison.OrdinalIgnoreCase));

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
        if (notUtf8 != null) throw new InvalidOperationException(notUtf8);
        if (!CanEdit) throw new InvalidOperationException($"Файл в кодировке {Get(".encoding")} — изменение не поддерживается");
    }

    public void Set(string key, string value)
    {
        CheckEdit();
        // a quote or newline would let an untrusted value inject other keys
        if (!IsSafeValue(value)) throw new ArgumentException($"Недопустимое значение {key}: кавычки и переводы строк запрещены", nameof(value));
        int i = Find(key);
        if (i >= 0) { lines[i] = $"{KeyRx.Match(lines[i]).Groups[1].Value} = \"{value}\"" + (lines[i].EndsWith('\r') ? "\r" : ""); return; }
        var line = $"{key} = \"{value}\"";
        // keep a trailing newline at the end of the file
        if (lines.Count > 0 && lines[^1] == "") { lines.Insert(lines.Count - 1, line + (crlf ? "\r" : "")); return; }
        if (crlf && lines.Count > 0 && !lines[^1].EndsWith('\r')) lines[^1] += "\r";
        lines.Add(line);
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

    // ---- library (inventory.vmls) ----

    /// <summary>Removes all vmlistN.* keys of entries whose vmlistN.config is <paramref name="fullVmx"/>. Returns whether the file changed.</summary>
    public bool RemoveVmListEntry(string fullVmx)
    {
        // shortcut: compares the stored path as text (VMware stores it normalized); upgrade to GetFullPath if entries are missed
        var prefixes = Keys.Select(key => Regex.Match(key, @"^(vmlist\d+)\.config$", RegexOptions.IgnoreCase))
                           .Where(m => m.Success && string.Equals(Get(m.Value), fullVmx, StringComparison.OrdinalIgnoreCase))
                           .Select(m => m.Groups[1].Value + ".").ToList();
        foreach (var p in prefixes) RemoveWhere(p); // trailing dot: vmlist1. does not hit vmlist10.
        return prefixes.Count > 0;
    }

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
