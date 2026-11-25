using System.IO;
using System.Text.RegularExpressions;

namespace VisualVMs.Tests;

public class KvFileTests
{
    const string Vmx = ".encoding = \"UTF-8\"\r\n# comment line\r\ndisplayName = \"Router 1\"\r\n  guestOS   =   \"debian13-64\"  \r\n\r\nweird line without quotes\r\n";

    [Fact]
    public void RoundTripIsIdentical()
    {
        Assert.Equal(Vmx, new KvFile(Vmx).ToString());
        Assert.Equal("a = \"1\"\nb = \"2\"", new KvFile("a = \"1\"\nb = \"2\"").ToString());
    }

    [Fact]
    public void SetReplacesInPlaceOrAppends()
    {
        var f = new KvFile(Vmx);
        f.Set("DISPLAYNAME", "R2");
        f.Set("memsize", "2048");
        Assert.Equal(Vmx.Replace("displayName = \"Router 1\"", "displayName = \"R2\"") + "memsize = \"2048\"\r\n", f.ToString());
        Assert.Equal("debian13-64", f.Get("GUESTOS"));
        Assert.Null(f.Get("missing"));
    }

    [Fact]
    public void AppendKeepsLfEndings()
    {
        var f = new KvFile("a = \"1\"\n");
        f.Set("b", "2");
        Assert.Equal("a = \"1\"\nb = \"2\"\n", f.ToString());
    }

    [Fact]
    public void RemoveWhereUsesPrefix()
    {
        var f = new KvFile("ethernet1.present = \"TRUE\"\nethernet10.present = \"TRUE\"\nEthernet1.pvnID = \"x\"\n");
        f.RemoveWhere("ethernet1.");
        Assert.Equal("ethernet10.present = \"TRUE\"\n", f.ToString());
    }

    [Fact]
    public void NonUtf8EncodingRefusesEdits()
    {
        var f = new KvFile(".encoding = \"windows-1252\"\na = \"1\"\n");
        Assert.False(f.CanEdit);
        Assert.Equal("1", f.Get("a"));
        Assert.Throws<InvalidOperationException>(() => f.Set("a", "2"));
        Assert.Throws<InvalidOperationException>(() => f.RemoveWhere("a"));
        Assert.True(new KvFile(".encoding = \"utf-8\"\n").CanEdit);
        Assert.True(new KvFile("").CanEdit);
    }

    [Fact]
    public void NicsReadsPresentOnly()
    {
        var f = new KvFile(
            "ethernet0.present = \"TRUE\"\nethernet0.connectionType = \"NAT\"\n" +
            "ethernet1.present = \"FALSE\"\nethernet1.connectionType = \"pvn\"\n" +
            "ethernet2.present = \"true\"\nethernet2.connectionType = \"pvn\"\nethernet2.pvnID = \"aa\"\n" +
            "ethernet3.present = \"TRUE\"\n");
        Assert.Equal(new[] { new Nic(0, "nat", null), new Nic(2, "pvn", "aa"), new Nic(3, "bridged", null) }, f.Nics());
    }

    [Fact]
    public void AddNicFillsGapAndCopiesVirtualDev()
    {
        var f = new KvFile("guestOS = \"windows11-64\"\nethernet0.present = \"TRUE\"\nethernet0.virtualDev = \"vmxnet3\"\n" +
                           "ethernet1.present = \"FALSE\"\nethernet1.pciSlotNumber = \"33\"\nethernet2.present = \"TRUE\"\n");
        Assert.Equal(1, f.AddNic("pvn", "11 22"));
        Assert.Null(f.Get("ethernet1.pciSlotNumber"));
        Assert.Equal("TRUE", f.Get("ethernet1.present"));
        Assert.Equal("pvn", f.Get("ethernet1.connectionType"));
        Assert.Equal("11 22", f.Get("ethernet1.pvnID"));
        Assert.Equal("vmxnet3", f.Get("ethernet1.virtualDev"));
        Assert.Equal("generated", f.Get("ethernet1.addressType"));
        Assert.Equal(3, f.AddNic("nat"));
        Assert.Null(f.Get("ethernet3.pvnID"));
    }

    [Fact]
    public void AddNicDefaultsVirtualDevByGuest()
    {
        var w = new KvFile("guestOS = \"windows9-64\"\n");
        w.AddNic("nat");
        Assert.Equal("e1000e", w.Get("ethernet0.virtualDev"));
        var l = new KvFile("guestOS = \"ubuntu-64\"\n");
        l.AddNic("nat");
        Assert.Equal("e1000", l.Get("ethernet0.virtualDev"));
    }

    [Fact]
    public void AddNicThrowsAtTen()
    {
        var f = new KvFile("");
        for (int i = 0; i < 10; i++) f.AddNic("nat");
        Assert.Throws<InvalidOperationException>(() => f.AddNic("nat"));
        f.RemoveNic(4);
        Assert.Equal(4, f.AddNic("nat"));
    }

    [Fact]
    public void NewPvnIdFormat()
    {
        var id = KvFile.NewPvnId();
        Assert.Matches(new Regex("^([0-9a-f]{2} ){7}[0-9a-f]{2}-([0-9a-f]{2} ){7}[0-9a-f]{2}$"), id);
        Assert.NotEqual(id, KvFile.NewPvnId());
    }

    const string Prefs = "pref.namedPVNs0.name = \"lan1\"\npref.namedPVNs0.pvnID = \"id1\"\npref.namedPVNs.count = \"1\"\n";

    [Fact]
    public void EnsureSegmentExistingReturnsId()
    {
        var f = new KvFile(Prefs);
        Assert.False(f.EnsureSegment("LAN1", out var id));
        Assert.Equal("id1", id);
        Assert.Equal(Prefs, f.ToString());
        Assert.False(f.EnsureSegment("other", "id1"));
    }

    [Fact]
    public void EnsureSegmentNewAppendsAtCount()
    {
        var f = new KvFile(Prefs);
        Assert.True(f.EnsureSegment("lan2", out var id));
        Assert.Equal("lan2", f.Get("pref.namedPVNs1.name"));
        Assert.Equal(id, f.Get("pref.namedPVNs1.pvnID"));
        Assert.Equal("2", f.Get("pref.namedPVNs.count"));
        Assert.Equal("lan2", f.Segments()[id]);
    }

    [Fact]
    public void EnsureSegmentMissingCountStartsAtZero()
    {
        var f = new KvFile("");
        Assert.True(f.EnsureSegment("a", out var id));
        Assert.Equal(id, f.Get("pref.namedPVNs0.pvnID"));
        Assert.Equal("1", f.Get("pref.namedPVNs.count"));
    }

    [Fact]
    public void EnsureSegmentNonContiguousIndices()
    {
        var f = new KvFile(Prefs + "pref.namedPVNs3.name = \"lan3\"\npref.namedPVNs3.pvnID = \"id3\"\n");
        Assert.Equal(2, f.Segments().Count);
        Assert.True(f.EnsureSegment("known", "id9"));
        Assert.Equal("id9", f.Get("pref.namedPVNs4.pvnID"));
        Assert.Equal("5", f.Get("pref.namedPVNs.count"));
    }

    // QA: real-file round trip with a Cyrillic VM name (as created in the sandbox: "тест2")
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveLoadKeepsBomAndCyrillic(bool bom)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vmx");
        try
        {
            File.WriteAllText(path, ".encoding = \"UTF-8\"\r\ndisplayName = \"тест2\"\r\n", new System.Text.UTF8Encoding(bom));
            var f = KvFile.Load(path);
            Assert.Equal("тест2", f.Get("displayName"));
            f.AddNic("pvn", "aa");
            f.Save(path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(bom, bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("тест2", KvFile.Load(path).Get("displayName"));
            Assert.Equal("aa", KvFile.Load(path).Get("ethernet0.pvnID"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    // QA: after a power cycle VMware appends ethernetN.pciSlotNumber / generatedAddress* far from the NIC block
    [Fact]
    public void RemoveNicDropsKeysVmwareAddedAtRuntime()
    {
        var f = new KvFile(
            "ethernet1.present = \"TRUE\"\r\nethernet1.connectionType = \"pvn\"\r\nethernet1.pvnID = \"p\"\r\n" +
            "usb.present = \"TRUE\"\r\n" +
            "ethernet1.pciSlotNumber = \"34\"\r\nethernet1.generatedAddress = \"00:0c:29:e5:d1:7a\"\r\nethernet1.generatedAddressOffset = \"10\"\r\n" +
            "ethernet10.present = \"TRUE\"\r\n");
        f.RemoveNic(1);
        Assert.Equal("usb.present = \"TRUE\"\r\nethernet10.present = \"TRUE\"\r\n", f.ToString());
    }

    // QA: preferences.ini as left by self-heal in the sandbox: gap at index 3, Cyrillic name at 2
    [Fact]
    public void SegmentsReadsGapsAndCyrillic()
    {
        var f = new KvFile(
            "pref.namedPVNs0.name = \"pre-direct\"\r\npref.namedPVNs0.pvnID = \"p0\"\r\npref.namedPVNs.count = \"5\"\r\n" +
            "pref.namedPVNs2.name = \"qa1-тест2\"\r\npref.namedPVNs2.pvnID = \"p2\"\r\n" +
            "pref.namedPVNs4.name = \"lan1\"\r\npref.namedPVNs4.pvnID = \"p4\"\r\n");
        var s = f.Segments();
        Assert.Equal(3, s.Count);
        Assert.Equal("qa1-тест2", s["P2"]);
        Assert.False(f.EnsureSegment("QA1-ТЕСТ2", out var id));
        Assert.Equal("p2", id);
        Assert.True(f.EnsureSegment("new", out _));
        Assert.Equal("new", f.Get("pref.namedPVNs5.name"));
        Assert.Equal("6", f.Get("pref.namedPVNs.count"));
    }
}

public class TopologyTests
{
    static readonly Dictionary<string, string> Segs = new() { ["p1"] = "lan1" };

    static VmNode Vm(string name, params Nic[] nics) => new() { Vmx = name + ".vmx", Name = name, X = 100, Y = 100, Nics = nics.ToList() };

    [Fact]
    public void TwoVmsOneSegmentIsDirect()
    {
        var hubs = new List<HubNode>();
        var e = Topology.Infer(new() { Vm("a", new Nic(1, "pvn", "p1")), Vm("b", new Nic(0, "pvn", "P1")) }, hubs, Segs);
        var d = Assert.Single(e);
        Assert.Equal(EdgeKind.Direct, d.Kind);
        Assert.Equal(2, d.Ends.Count);
        Assert.Empty(hubs);
    }

    [Fact]
    public void ThreeVmsMakeAutoHub()
    {
        var hubs = new List<HubNode>();
        var e = Topology.Infer(new() { Vm("a", new Nic(0, "pvn", "p1")), Vm("b", new Nic(0, "pvn", "p1")), Vm("c", new Nic(0, "pvn", "p1")) }, hubs, Segs);
        var h = Assert.Single(hubs);
        Assert.Equal("lan1", h.Name);
        Assert.Equal(160, h.X);
        Assert.Equal(3, e.Count);
        Assert.All(e, x => { Assert.Equal(EdgeKind.Hub, x.Kind); Assert.Same(h, x.Hub); });
    }

    [Fact]
    public void SingleNicMakesHubWithFallbackName()
    {
        var hubs = new List<HubNode>();
        var e = Topology.Infer(new() { Vm("a", new Nic(0, "pvn", "abcdef 12")) }, hubs, Segs);
        Assert.Equal("Сегмент abcde", Assert.Single(hubs).Name);
        Assert.Equal(EdgeKind.Hub, Assert.Single(e).Kind);
    }

    [Fact]
    public void TwoNicsOnSameVmMakeHub()
    {
        var hubs = new List<HubNode>();
        var e = Topology.Infer(new() { Vm("a", new Nic(0, "pvn", "p1"), new Nic(1, "pvn", "p1")) }, hubs, Segs);
        Assert.Single(hubs);
        Assert.Equal(2, e.Count(x => x.Kind == EdgeKind.Hub));
    }

    [Fact]
    public void SchemeHubBeatsDirect()
    {
        var hubs = new List<HubNode> { new() { Name = "sw", PvnId = "p1" } };
        var e = Topology.Infer(new() { Vm("a", new Nic(0, "pvn", "p1")), Vm("b", new Nic(0, "pvn", "p1")) }, hubs, Segs);
        Assert.Equal(2, e.Count);
        Assert.All(e, x => Assert.Same(hubs[0], x.Hub));
        Assert.Single(hubs);
    }

    [Fact]
    public void NatNicIsNatEdgeAndBridgedIgnored()
    {
        var vm = Vm("a", new Nic(0, "nat", null), new Nic(1, "bridged", null));
        var e = Assert.Single(Topology.Infer(new() { vm }, new(), Segs));
        Assert.Equal(EdgeKind.Nat, e.Kind);
        Assert.Equal((vm, 0), Assert.Single(e.Ends));
    }

    [Fact]
    public void VmNotOnCanvasIgnored()
    {
        // only canvas VMs are passed in: the other end of a direct link is simply absent → single NIC → hub
        var hubs = new List<HubNode>();
        var e = Topology.Infer(new() { Vm("a", new Nic(0, "pvn", "p1")) }, hubs, Segs);
        Assert.Single(e);
        Assert.DoesNotContain(e, x => x.Kind == EdgeKind.Direct);
    }

    [Fact]
    public void HubWithoutNicsSurvives()
    {
        var hubs = new List<HubNode> { new() { Name = "empty", PvnId = "zz" } };
        Assert.Empty(Topology.Infer(new() { Vm("a") }, hubs, Segs));
        Assert.Single(hubs);
    }

    [Fact]
    public void SchemeRoundTrip()
    {
        var s = new Scheme { Internet = new(10, 20) };
        s.Vms.Add(new VmNode { Vmx = @"C:\vm\a.vmx", Purpose = Purpose.Router, X = 1.5, Y = 2, Name = "ignored" });
        s.Hubs.Add(new HubNode { Name = "lan", PvnId = "p1", X = 3, Y = 4 });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vvm");
        try
        {
            s.Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"purpose\": \"Router\"", json);
            Assert.DoesNotContain("ignored", json);
            var l = Scheme.Load(path);
            Assert.Equal(1, l.Version);
            Assert.Equal(new Pt(10, 20), l.Internet);
            var v = Assert.Single(l.Vms);
            Assert.Equal((@"C:\vm\a.vmx", Purpose.Router, 1.5, 2.0), (v.Vmx, v.Purpose, v.X, v.Y));
            var h = Assert.Single(l.Hubs);
            Assert.Equal(("lan", "p1", 3.0, 4.0), (h.Name, h.PvnId, h.X, h.Y));
        }
        finally { File.Delete(path); }
    }

    // QA: hand-written scheme with only "vms" gets defaults; garbage JSON throws (the app shows it in a message box)
    [Fact]
    public void SchemeLoadMinimalAndInvalid()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vvm");
        try
        {
            File.WriteAllText(path, "{\"vms\":[{\"vmx\":\"E:\\\\VMs\\\\ghost\\\\ghost.vmx\",\"purpose\":\"router\",\"x\":300,\"y\":300}]}");
            var s = Scheme.Load(path);
            Assert.Equal(new Pt(80, 80), s.Internet);
            Assert.Empty(s.Hubs);
            Assert.Equal(Purpose.Router, Assert.Single(s.Vms).Purpose);

            File.WriteAllText(path, "{ not json");
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Scheme.Load(path));
            File.WriteAllText(path, "null");
            Assert.Throws<InvalidDataException>(() => Scheme.Load(path));
        }
        finally { File.Delete(path); }
    }
}

public class VmwareTests
{
    [Fact]
    public void ValidateCreateRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(root, "busy"));
        File.WriteAllText(Path.Combine(root, "busy", "x"), "");
        var iso = Path.Combine(root, "a.iso");
        File.WriteAllText(iso, "");
        try
        {
            var ok = new CreateVmRequest("r1", "debian13-64", iso, 2048, 2, 20, Purpose.Router);
            Assert.Null(Vmware.Validate(ok, root));
            Assert.NotNull(Vmware.Validate(ok with { Name = "" }, root));
            Assert.NotNull(Vmware.Validate(ok with { Name = "a|b" }, root));
            Assert.NotNull(Vmware.Validate(ok with { Name = "a\"b" }, root));
            Assert.NotNull(Vmware.Validate(ok with { Name = "a/b" }, root));
            Assert.NotNull(Vmware.Validate(ok with { Name = "busy" }, root));
            Assert.NotNull(Vmware.Validate(ok with { Iso = iso + "x" }, root));
            Assert.NotNull(Vmware.Validate(ok with { RamMb = 255 }, root));
            Assert.NotNull(Vmware.Validate(ok with { RamMb = 1026 }, root));
            Assert.NotNull(Vmware.Validate(ok with { Cpus = 0 }, root));
            Assert.NotNull(Vmware.Validate(ok with { Cpus = 65 }, root));
            Assert.NotNull(Vmware.Validate(ok with { DiskGb = 2001 }, root));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class UiSmokeTests
{
    [Fact]
    public void MainWindowXamlAndIconsLoad()
    {
        Exception? error = null;
        // constructed only, never shown: Loaded (which talks to VMware) does not fire
        var t = new Thread(() =>
        {
            try
            {
                var w = new MainWindow();
                foreach (var key in new[] { "Router", "Server", "Client", "Cloud" })
                    Assert.IsAssignableFrom<System.Windows.Media.Geometry>(w.FindResource(key));
                new CreateVmWindow();
                new AddVmsWindow(new() { ("a", @"C:\a\a.vmx") });
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
