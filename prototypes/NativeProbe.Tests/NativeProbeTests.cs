using System.Text.Json;
using Xunit;

namespace WholeCellNative.Tests;

public class NativeProbeTests
{
    // Shape of the game's rules: a nullable ID<T> struct built from a string.
    readonly record struct FakeId(string Name);
    sealed class FakeRules
    {
        FakeId? RouteEndpoint { get; set; }
        public FakeId? Endpoint => RouteEndpoint;
    }

    [Fact]
    public void RouteSetsNonPublicNullableIdFromName()
    {
        var rules = new FakeRules();
        NativeProbe.Route(rules, "RouteEndpoint", "CrewlessLogisticsSource");
        Assert.Equal(new FakeId("CrewlessLogisticsSource"), rules.Endpoint);
    }

    [Fact]
    public void CatalogJsonCamelCasesPropertiesButKeepsComponentKeys()
    {
        var component = new NativeProbe.CatalogComponent { Id = "Battery", ResourceType = "battery" };
        component.References["ResourceStorages"] = new[] { "BatteryStorage" };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(component, NativeProbe.CatalogJson));
        var root = json.RootElement;
        Assert.Equal("Battery", root.GetProperty("id").GetString());
        Assert.Equal("battery", root.GetProperty("resourceType").GetString());
        // Component field names are game rule paths and must stay verbatim (docs/device-catalog.json).
        Assert.Equal("BatteryStorage", root.GetProperty("references").GetProperty("ResourceStorages")[0].GetString());
        Assert.Contains('\n', JsonSerializer.Serialize(component, NativeProbe.CatalogJson));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PerimeterSocketsCoverEachEdgeCellOnce(int size)
    {
        var sockets = NativeProbe.PerimeterSockets(size);
        Assert.Equal(4 * size, sockets.Distinct().Count());
        Assert.Contains(NativeProbe.Socket("Up", size - 1, 0), sockets);
        Assert.Contains(NativeProbe.Socket("Down", 0, size - 1), sockets);
        Assert.Contains(NativeProbe.Socket("Left", 0, size - 1), sockets);
        Assert.Contains(NativeProbe.Socket("Right", size - 1, 0), sockets);
    }

    [Fact]
    public void SmallThermalPortsMapOntoEvery2x2Socket()
    {
        Assert.Equal(NativeProbe.PerimeterSockets(2).Order(), NativeProbe.SmallSockets.Select(s => s.Electric).Order());
        Assert.Equal(8, NativeProbe.SmallSockets.Select(s => s.Thermal).Distinct().Count());
    }

    [Theory]
    [InlineData("morgott.crewless_logistics", true)]
    [InlineData("morgott.powergrid", true)]
    [InlineData("morgott.crewless_logistics.cable", false)]
    [InlineData("MORGOTT.POWERGRID", false)]
    public void SelfModIdsIncludeLegacyPackageOnly(string id, bool expected) => Assert.Equal(expected, NativeProbe.IsSelfModId(id));

    [Fact]
    public void PortablePathHidesProfileAndSteamId()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Path.Combine(profile, "Saved Games", "Cosmoteer", "76561198000000000", "Mods", "CrewlessLogistics");
        Assert.Equal(@"%USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Mods\CrewlessLogistics", NativeProbe.PortablePath(local));
        Assert.Equal(@"D:\Cosmoteer\Bin", NativeProbe.PortablePath(@"D:\Cosmoteer\Bin"));
    }
}
