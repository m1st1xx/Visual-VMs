using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualVMs;

public enum Purpose { Router, Server, Client }

public record Pt(double X, double Y);

public record Nic(int Index, string Type, string? PvnId);

/// <summary>A VM on the canvas. X/Y is the node center.</summary>
public class VmNode
{
    public string Vmx { get; set; } = "";
    public Purpose Purpose { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    [JsonIgnore] public string Name { get; set; } = "";
    [JsonIgnore] public List<Nic> Nics { get; set; } = new();
    [JsonIgnore] public bool Missing { get; set; }
}

public class HubNode
{
    public string Name { get; set; } = "";
    public string PvnId { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public class Scheme
{
    public int Version { get; set; } = 1;
    public Pt Internet { get; set; } = new(80, 80);
    public List<VmNode> Vms { get; set; } = new();
    public List<HubNode> Hubs { get; set; } = new();

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes to path.tmp, then moves over path so a crash never leaves a half-written file.</summary>
    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Opts));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The file is untrusted: entries that would break the app or inject keys into vmx/preferences.ini are dropped.</summary>
    public static Scheme Load(string path)
    {
        var s = JsonSerializer.Deserialize<Scheme>(File.ReadAllText(path), Opts) ?? throw new InvalidDataException("Пустой файл схемы");
        s.Internet ??= new(80, 80);
        s.Vms = (s.Vms ?? new()).Where(v => v != null && IsLocalVmx(v.Vmx)).ToList();
        s.Hubs = (s.Hubs ?? new()).Where(h => h != null && !string.IsNullOrEmpty(h.Name) && KvFile.IsSafeValue(h.Name) && KvFile.IsPvnId(h.PvnId)).ToList();
        return s;
    }

    // a leading \ or / covers UNC (\\server, //server) and device paths (\\?\, \??\)
    static bool IsLocalVmx(string? p) =>
        !string.IsNullOrEmpty(p) && p.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(p)
        && p[0] is not ('\\' or '/') && p.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase);
}

public enum EdgeKind { Direct, Hub, Nat }

/// <summary>Ends = the NICs to remove when the edge is deleted.</summary>
public record Edge(EdgeKind Kind, string? PvnId, List<(VmNode Vm, int Nic)> Ends, HubNode? Hub);

public static class Topology
{
    public static string SegmentName(string pvnId, IReadOnlyDictionary<string, string> segments) =>
        segments.TryGetValue(pvnId, out var n) ? n : "Сегмент " + pvnId[..Math.Min(5, pvnId.Length)];

    /// <summary><paramref name="name"/>, or name-2, name-3… — the first one no existing segment uses (it may belong to another lab).</summary>
    public static string FreeSegmentName(string name, IReadOnlyDictionary<string, string> segments)
    {
        var used = segments.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = name;
        for (int i = 2; used.Contains(free); i++) free = $"{name}-{i}";
        return free;
    }

    /// <summary>Builds edges from the VMs' NICs. Adds auto-created hubs to <paramref name="hubs"/>.</summary>
    public static List<Edge> Infer(List<VmNode> vms, List<HubNode> hubs, IReadOnlyDictionary<string, string> segments)
    {
        var edges = new List<Edge>();
        foreach (var vm in vms)
            foreach (var nic in vm.Nics.Where(n => n.Type == "nat"))
                edges.Add(new Edge(EdgeKind.Nat, null, new() { (vm, nic.Index) }, null));

        var groups = vms.SelectMany(vm => vm.Nics.Where(n => n.Type == "pvn" && !string.IsNullOrEmpty(n.PvnId)).Select(n => (Vm: vm, Nic: n.Index, Pvn: n.PvnId!)))
                        .GroupBy(x => x.Pvn, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var ends = g.ToList();
            var hub = hubs.FirstOrDefault(h => h.PvnId.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (hub == null && ends.Count == 2 && ends[0].Vm != ends[1].Vm)
            {
                edges.Add(new Edge(EdgeKind.Direct, g.Key, ends.Select(e => (e.Vm, e.Nic)).ToList(), null));
                continue;
            }
            if (hub == null)
            {
                var members = ends.Select(e => e.Vm).Distinct().ToList();
                hub = new HubNode { Name = SegmentName(g.Key, segments), PvnId = g.Key, X = members.Average(m => m.X) + 60, Y = members.Average(m => m.Y) + 60 };
                hubs.Add(hub);
            }
            foreach (var e in ends) edges.Add(new Edge(EdgeKind.Hub, g.Key, new() { (e.Vm, e.Nic) }, hub));
        }
        return edges;
    }
}
