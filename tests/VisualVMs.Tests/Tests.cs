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
}
