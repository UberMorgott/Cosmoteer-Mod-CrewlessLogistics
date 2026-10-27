using System.Reflection;
using System.Text.Json;

sealed partial class NativeProbe
{
    void HeatElectricCrossingProbe()
    {
        object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
        var source = Open(Path.Combine(projectRoot, "mod", "cable_heat_crossing.rules"));
        var nativeCross = Open(Path.Combine(gameRoot, "Data", "ships", "terran", "heat_pipe_crossing", "heat_pipe_crossing.rules"));
        object Read(string name) => Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", source);
        Require((string)ReadRuleObject(source, "Part/ID", typeof(string)) == "morgott.crewless_logistics.cable_heat_crossing", "Wrong heat/electric crossing ID");
        Require((bool)ReadRuleObject(source, "Part/IsRotateable", typeof(bool)) && (float)ReadRuleObject(source, "Part/CrewSpeedFactor", typeof(float)) == 0f && ((int[][])ReadRuleObject(source, "Part/AllowedDoorLocations", typeof(int[][]))).Length == 0, "Mixed crossing must rotate while retaining armored crew blocking");
        Require((int)ReadRuleObject(source, "Part/MaxHealth", typeof(int)) == 1000, "Mixed crossing must have vanilla heat pipe crossing durability");
        var spacing = Read("OverlaySpacingNode");
        Require(Flag(spacing, "HasBlueprintComponent") && !Flag(spacing, "HasPhysicalComponent"), "Thermal overlay spacing must not add a physical node or resource path");
        foreach (string name in new[] { "Port_Up", "Port_Down" }) {
            var port = Read(name);
            var original = Rules("Part/Components/" + name, "Cosmoteer.Ships.Networks.PartNetworkPortRules", nativeCross);
            foreach (string field in new[] { "Location", "Direction" }) Require(Equals(Field(port.GetType(), field).GetValue(port), Field(original.GetType(), field).GetValue(original)), "Mixed crossing changed native thermal geometry");
            foreach (string property in new[] { "RouteSource", "RouteEndpoint" }) Require(Equals(Property(port, property), Property(original, property)), "Mixed crossing changed native physical thermal routes");
            foreach (string property in new[] { "BlueprintRouteSource", "BlueprintRouteEndpoint" }) Require(Property(port, property).ToString() == "CrewlessLogisticsBlueprintNode", "Thermal diagram must use the centered independent blueprint node");
            Require(((Array)Property(port, "Types")).Cast<object>().Select(x => x.ToString()).SequenceEqual(new[] { "thermal" }), "Mixed crossing thermal socket is not the native thermal type");
            var overlay = Field(port.GetType(), "Overlay").GetValue(port)!;
            Require(Field(overlay.GetType(), "RouteDrawMode").GetValue(overlay)!.ToString() == "Connected" && Field(overlay.GetType(), "IconDrawMode").GetValue(overlay)!.ToString() == "Never", "Mixed crossover must retain the continuous native thermal indicator");
            foreach (string electric in new[] { "Port_Electric_Left", "Port_Electric_Right" }) {
                var electrical = Read(electric);
                Require(!(bool)Call(port, "IsCompatibleWith", electrical)! && !(bool)Call(electrical, "IsCompatibleWith", port)!, "Orthogonal crossing mixes thermal and electric compatibility");
                Require(Property(electrical, "RouteSource")!.ToString() == "CrewlessLogisticsNode" && Property(electrical, "RouteEndpoint")!.ToString() == "CrewlessLogisticsNode", "Electric crossover uses the thermal node");
            }
        }
        var visual = new List<CableAppearancePrimitive>();
        float[] Pair(object value) => new[] { Convert.ToSingle(Property(value, "X")), Convert.ToSingle(Property(value, "Y")) };
        float[] Channels(object color) => new[] { "R", "G", "B", "A" }.Select(c => Convert.ToSingle(Field(color.GetType(), c).GetValue(color))).ToArray();
        string SpriteFile(object sprite) => ((Array)Property(sprite, "Files")).GetValue(0)!.ToString()!;
        foreach (string name in new[] { "Graphics", "ThermalBody", "HeatGlow_Graphics", "Darkness_Graphics", "ElectricBridge" }) {
            var graphics = Read(name);
            Require(Field(graphics.GetType(), "OperationalRoofDoodad").GetValue(graphics) == null && Field(graphics.GetType(), "OperationalRoofLighting").GetValue(graphics) == null, "Mixed crossover leaks machinery/glow through plain exterior roof");
            float rotation = Convert.ToSingle(Call(Field(graphics.GetType(), "Rotation").GetValue(graphics)!, "ToDegrees"));
            Require(Math.Abs(rotation - (name.StartsWith("Electric") ? 90f : 0f)) < 0.001f, "Mixed crossover bridge lost its orthogonal physical orientation");
            string? gate = Field(graphics.GetType(), "OperationalToggle").GetValue(graphics)?.ToString();
            foreach (string slot in new[] { "Floor", "Walls", "Roof", "OperationalDoodad" }) {
                var group = Field(graphics.GetType(), slot).GetValue(graphics);
                if (group == null) continue;
                string layer = Field(group.GetType(), "Layer").GetValue(group)!.ToString()!;
                var sprites = (Array)Field(group.GetType(), "DamageLevels").GetValue(group)!;
                Require(sprites.Length == (name == "HeatGlow_Graphics" ? 1 : 3), "Mixed crossover discarded native damage sprites");
                if (name == "ElectricBridge") Require(layer == "doodads_high", "Electrical path must use the vanilla crossing doodad layer below the thermal bridge");
                if (name == "ThermalBody") Require(layer == "CrewlessLogisticsThermalBridge", "Thermal pipe must render above the electrical path and its glow");
                if (name == "HeatGlow_Graphics") Require(layer == "lights_add", "Thermal load glow must use the native light layer above both pipes");
                for (int damage = 0; damage < sprites.Length; damage++) {
                    var sprite = sprites.GetValue(damage)!;
                    var file = SpriteFile(sprite);
                    file = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(gameRoot, file));
                    Require(File.Exists(file), "Missing mixed-crossover native art: " + file);
                    var color = Channels(Property(sprite, "VertexColor"));
                    if (name == "Graphics" && slot is "Floor" or "Walls") {
                        Require(file.Replace('\\', '/').Contains("/armor/"), "Armored mixed crossing must use native dark armor instead of a white walking tile");
                        Require(color.All(c => c == 1f), "Mixed crossing floor/frame must retain native brightness");
                    }
                    if (name == "ElectricBridge") Require(Path.GetFileName(file) == "cable_crossing_body" + (damage == 0 ? "" : damage == 1 ? "_33" : "_66") + ".png", "Electrical path must use the baked blue crossing body");
                    if (name is "ThermalBody" or "ElectricBridge") Require(Pair(Property(sprite, "Size")).SequenceEqual(new[] { 1f, 1.0625f }), "Crossing pipe must overlap two source pixels at each axial tile seam");
                    // Dynamic thermal-load shading is outside this still-image
                    // preview. Empty native Average queries return NaN, so do not
                    // invent a thermal signal or export a false loaded state.
                    if (name == "HeatGlow_Graphics") continue;
                    if (slot == "Roof") Require(file.Replace('\\', '/').Contains("/armor/roof"), "Mixed crossover exterior is not native plain armor");
                    var spriteOffset = Pair(Property(sprite, "Offset")); var groupOffset = Pair(Field(group.GetType(), "Offset").GetValue(group)!);
                    visual.Add(new(name, slot, layer, gate, Pair(Field(graphics.GetType(), "Location").GetValue(graphics)!), Pair(Property(sprite, "Size")), new[] { spriteOffset[0] + groupOffset[0], spriteOffset[1] + groupOffset[1] }, color, file, (int)Property(sprite, "UVRotation"), (bool)Property(sprite, "MirrorU"), (bool)Property(sprite, "MirrorV"), rotation, damage));
                }
            }
        }
        foreach (string side in new[] { "Up", "Down" }) {
            var connector = Rules($"Part/Components/PipeConnector{side}_Components/Components/PipesConnector{side}_Graphics", "Cosmoteer.Ships.Parts.PartComponentRules", source);
            Require(Field(connector.GetType(), "Roof").GetValue(connector) == null && Field(connector.GetType(), "Floor").GetValue(connector) != null, "Conditional thermal connector leaks roof art or lost its interior art");
        }
        var cases = new List<object>();
        for (int transform = 0; transform < 8; transform++) cases.Add(HeatElectricCrossingCase(source, transform));
        string art = Path.Combine(projectRoot, "artifacts", "heat-electric-crossing-native.json");
        File.WriteAllText(art, JsonSerializer.Serialize(new { generatedAtUtc = DateTimeOffset.UtcNow, description = "Native orange thermal cylinder above rotated baked blue electrical path (its lamp lies fully under the opaque cylinder), native dark armor substrate and plain exterior armor. Two source pixels overlap each axial seam. Dynamic thermal-load glow is deliberately omitted from this CPU still-image preview.", excludedDynamicThermalGlow = true, styles = new Dictionary<string, List<CableAppearancePrimitive>> { ["heat_electric"] = visual } }, new JsonSerializerOptions { WriteIndented = true }));
        string proof = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "heat-electric-crossing-validation.json");
        File.WriteAllText(proof, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, transforms = cases, graphicsPrimitives = visual.Count, connectorRoofsOmitted = 2, plainArmorRoof = true, nativeThermalMechanics = true, separateNodesAndPortTypes = true, limits = "Actual native component rules, port discovery, topology queries and resource operations with synthetic ownership; converter ticks stepped manually. Full part/rendering/scheduler/save-load/multiplayer not exercised." }, new JsonSerializerOptions { WriteIndented = true }));
        scenarioResults.Add(new { name = "native orthogonal thermal/electric crossing", transforms = 8, independentChannels = true, heatAndBatteryConserved = true, nativeDamageArt = true, plainArmorRoof = true });
        Console.WriteLine($"PASS: native orthogonal heat/electric crossover; eight rotations/mirrors, isolated native IO, whole cells and plain armor roof; art={art}; evidence={proof}");
    }

    object HeatElectricCrossingCase(object source, int transform)
    {
        var manager = NewTopologyManager();
        (int X, int Y) World(int x, int y)
        {
            if ((transform & 4) != 0) x = -x;
            return (transform & 3) switch { 0 => (x, y), 1 => (-y, x), 2 => (-x, -y), _ => (y, -x) };
        }
        void Place(int x, int y) { var p = World(x, y); NewTopologyPart(p.X, p.Y, transform); }
        object FixturePort(string name, string direction, string? endpoint = null)
        {
            var rules = Rules(name, "Cosmoteer.Ships.Networks.PartNetworkPortRules");
            var facing = Field(rules.GetType(), "Direction"); facing.SetValue(rules, Enum.Parse(facing.FieldType, direction));
            if (endpoint != null) { var field = rules.GetType().GetProperty("RouteEndpoint", All)!; field.SetValue(rules, Id(Nullable.GetUnderlyingType(field.PropertyType)!, endpoint)); }
            var port = Component(rules); Call(port, "OnAddedToPart", part); return port;
        }
        Place(-1, 0);
        var batterySource = Storage("SourceStorage", 8000, 4500);
        var sourceView = Inline("SourceCells", batterySource);
        var electricInput = GraphIo(manager, "PortProbeElectricInput");
        var electricPump = Pump(sourceView, electricInput);
        var providerRules = Rules("SourceStore", "Cosmoteer.Ships.Networks.PartNetworkResourceStoreRules");
        var categories = Field(providerRules.GetType(), "Categories");
        var sourceCategories = Array.CreateInstance(categories.FieldType.GetElementType()!, 1);
        sourceCategories.SetValue(Id(categories.FieldType.GetElementType()!, "morgott.crewless_logistics.source"), 0); categories.SetValue(providerRules, sourceCategories);
        var provider = Component(providerRules); InitializeLinks(provider); RegisterNode(manager, provider);
        var sourceElectric = FixturePort("PortProbeSourceElectric", "Right", "SourceStore");
        Place(0, -1);
        var overclock = Component("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
        var heatSource = Storage("PortProbeHeatStorage", 100, 23);
        var heatInput = GraphIo(manager, "PortProbeHeatInput");
        var heatPump = Pump(heatSource, heatInput);
        var sourceHeat = FixturePort("PortProbeSourceHeat", "Down");
        Place(0, 0);
        object Read(string name) => Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", source);
        var heatNode = Component(Read("NetworkAverageThermalDissipationLoadValue")); Call(heatNode, "OnAddedToPart", part);
        var heatColor = Component(Read("ThermalLoadGlowColorFactor")); Call(heatColor, "OnAddedToPart", part);
        var electricRules = Read("CrewlessLogisticsNode"); Require(Flag(electricRules, "HasPhysicalComponent"), "Mixed crossing electrical node is absent from native physical construction");
        var electricNode = Component(electricRules); RegisterNode(manager, electricNode);
        var availability = Component(Read("CrewlessLogisticsAvailable")); InitializeLinks(availability); RegisterNode(manager, availability);
        var router = Component(Read("CrewlessLogisticsAvailabilityRouter")); Call(router, "OnAddedToPart", part);
        var ports = new Dictionary<string, object>();
        foreach (string name in new[] { "Port_Up", "Port_Down", "Port_Electric_Left", "Port_Electric_Right" }) { var port = Component(Read(name)); Call(port, "OnAddedToPart", part); ports.Add(name, port); }
        Place(1, 0);
        var batteryTarget = Storage("ReceiverStorage", 4000, 0);
        var receiver = Receiver(batteryTarget, out var bias);
        var electricStore = GraphStore(manager, "PortProbeElectricStore", receiver);
        var targetElectric = FixturePort("PortProbeTargetElectric", "Left");
        Place(0, 1);
        var heatTarget = Storage("PortProbeHeatStorage", 100, 0);
        var heatStore = GraphStore(manager, "PortProbeHeatStore", heatTarget);
        var targetHeat = FixturePort("PortProbeTargetHeat", "Up");
        RequireSeparated(electricNode, heatNode); RequireSeparated(electricInput, heatInput);
        RequirePair(sourceElectric, ports["Port_Electric_Left"], "orthogonal electrical input"); RequirePair(ports["Port_Electric_Right"], targetElectric, "orthogonal electrical output"); RequirePair(ports["Port_Down"], targetHeat, "orthogonal thermal output");
        Require(Peer(sourceHeat) == null && Peer(ports["Port_Up"]) == null, "Disabled heat source unexpectedly joined orthogonal crossing");
        Require(Flag(availability, "IsToggleOn") && Count(availability, "Resources") == 4, "Mixed crossing did not discover available full source cells");
        var circuit = new Circuit(new[] { batterySource }, new[] { batteryTarget }, new[] { bias }, electricPump);
        CheckedPulse(circuit); Pulse(heatPump); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 3500, 1000, 23, 0);
        SetToggle(overclock, true); RequirePair(sourceHeat, ports["Port_Up"], "orthogonal thermal input");
        Pulse(heatPump); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 3500, 1000, 22, 1);
        CheckedPulse(circuit); CheckedPulse(circuit); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 1500, 3000, 22, 1);
        Call(manager, "RequestPortDeregistration", ports["Port_Electric_Right"]);
        Require(Peer(targetElectric) == null, "Electrical cut left a stale crossover peer"); RequirePair(ports["Port_Down"], targetHeat, "thermal survives electrical cut");
        CheckedPulse(circuit); Pulse(heatPump); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 1500, 3000, 21, 2);
        Call(manager, "RequestPortRegistration", ports["Port_Electric_Right"]); RequirePair(ports["Port_Electric_Right"], targetElectric, "orthogonal electrical reconnect");
        CheckedPulse(circuit); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 500, 4000, 21, 2);
        Require(!Flag(availability, "IsToggleOn") && Count(availability, "Resources") == 0, "Fractional source remainder kept crossover voltage on");
        SetToggle(overclock, false); Pulse(heatPump); RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 500, 4000, 21, 2);
        RequireSeparated(electricStore, heatStore);
        return new { transform, sourceBattery = 500, targetBattery = 4000, sourceHeat = 21, targetHeat = 2, roundingBias = Count(bias, "Resources"), nativeHeatNode = heatNode.GetType().FullName, channelsSeparated = true };
    }
}
