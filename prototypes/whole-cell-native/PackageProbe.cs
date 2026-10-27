using System.Collections;
using System.Reflection;
using System.Text.Json;

sealed partial class NativeProbe
{
    void ValidateModPackage()
    {
        var originalDirectory = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = gameRoot;
            var modDirectory = Path.Combine(projectRoot, "mod");
            object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
            var modFile = Open(Path.Combine(modDirectory, "mod.rules"));
            foreach (var path in Directory.GetFiles(modDirectory, "*.rules", SearchOption.AllDirectories)) Open(path);
            Require((string)ReadRuleObject(modFile, "ID", typeof(string)) == "morgott.crewless_logistics", "Wrong package ID");
            LocalizationProbe(modFile, modDirectory);
            MultiplayerPortabilityProbe();
            var actionsType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
            var actions = (IList)ReadRuleObject(modFile, "Actions", actionsType);
            var root = Open(Path.Combine(gameRoot, "Data", "cosmoteer.rules"));
            foreach (var action in actions) Call(action!, "ApplyAction", root);
            using var consumerMapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "docs", "consumer-coverage.json")));
            Require(actions.Count == 56 + consumerMapping.RootElement.GetProperty("moddedGeneratedActions").GetInt32(), "Unexpected vanilla/enabled-mod compatibility action count");
            ValidateCableMaterialLayer(root);
            CableEditorMetadataProbe();
            var crossingFile = Open(Path.Combine(modDirectory, "cable_crossing.rules"));
            Require((bool)ReadRuleObject(crossingFile, "Part/IsRotateable", typeof(bool)), "Crew crossing must inherit the rotatable vanilla crossing pipe");
            var toggleGuiFile = Open(Path.Combine(modDirectory, "network_toggle.rules"));
            // GUI texture factories require game graphics startup, so inspect their
            // native ObjectText fields while exercising real UIToggle components below.
            Require((string)ReadRuleObject(toggleGuiFile, "Toggle/ToggleID", typeof(string)) == "morgott.crewless_logistics.network", "Wrong network switch GUI ID");
            Require((bool)ReadRuleObject(toggleGuiFile, "Toggle/ShowInEditor", typeof(bool)), "Network switch must appear in the editor");
            Require((string)ReadRuleObject(toggleGuiFile, "Toggle/Style", typeof(string)) == "Switch", "Network switch must use the native switch style");
            foreach (int choice in new[] { 0, 1 }) {
                var iconFile = (string)ReadRuleObject(toggleGuiFile, $"Toggle/Choices/{choice}/ButtonSprite/Texture/File", typeof(string));
                Require(iconFile == "assets/network_toggle.png" && File.Exists(Path.Combine(modDirectory, iconFile)), "Network switch icon asset is missing");
                Require(((int[])ReadRuleObject(toggleGuiFile, $"Toggle/Choices/{choice}/ButtonSprite/Size", typeof(int[]))).SequenceEqual(new[] { 64, 64 }), "Network switch glyph must use the native button display size");
            }
            const string overlayRoot = "<gui/game/parts/networks/part_networks.rules>/";

            foreach (string marker in new[] { "CrewlessLogisticsElectricPort", "CrewlessLogisticsElectricDot" })
                Require(((int[])ReadRuleObject(root, overlayRoot + "OverlayIcons/" + marker + "/Sprite/VertexColor", typeof(int[]))).SequenceEqual(new[] { 110, 255, 45, 255 }), "Electrical socket markers must use energy-cell green");
            Require((string)ReadRuleObject(root, overlayRoot + "OverlayIcons/CrewlessLogisticsElectricDot/Sprite/File", typeof(string)) == "./Data/gui/dot.png", "Connected marker must not multiply green tint onto baked orange thermal artwork");

            const string reactor = "<ships/terran/reactor_small/reactor_small.rules>/Part/Components/";
            const string consumer = "<ships/terran/laser_blaster_small/laser_blaster_small.rules>/Part/Components/";
            object ReadComponent(string path) => Rules(path, "Cosmoteer.Ships.Parts.PartComponentRules", root);
            var added = new List<string>();
            foreach (var name in new[] { "CrewlessLogisticsToggle", "CrewlessLogisticsCanTransfer", "CrewlessLogisticsCells", "CrewlessLogisticsInput", "CrewlessLogisticsTransfer", "CrewlessLogisticsBlueprintNode", "CrewlessLogisticsNode", "CrewlessLogisticsSourceRouter" }.Concat(SmallSockets.Select(s => s.Electric))) {
                ReadComponent(reactor + name);
                added.Add(reactor + name);
            }
            foreach (var name in new[] { "CrewlessLogisticsToggle", "CrewlessLogisticsReceivingBias", "CrewlessLogisticsReceivingAggregate", "CrewlessLogisticsCells", "CrewlessLogisticsReceiver", "CrewlessLogisticsBlueprintNode", Socket("Left", 0, 2), Socket("Right", 0, 2), Socket("Down", 0, 2) }) {
                ReadComponent(consumer + name);
                added.Add(consumer + name);
            }
            foreach (string path in added.Where(path => path.Contains("CrewlessLogisticsPort"))) {
                var rules = ReadComponent(path);
                Require(Field(rules.GetType(), "OperationalToggle").GetValue(rules)?.ToString() == "CrewlessLogisticsToggle", "Device electric port must require its own network switch");
                Require(Field(rules.GetType(), "BlueprintOperationalToggle").GetValue(rules)?.ToString() == "CrewlessLogisticsToggle", "Device blueprint port must require its own network switch");
                CheckElectricOverlay(rules);
            }
            foreach (var mapping in SmallSockets) ComparePorts(root, reactor, mapping.Electric, "Port_Thermal_" + mapping.Thermal);
            foreach (var mapping in new[] { (Socket("Left", 0, 2), "Port_Thermal_Left"), (Socket("Right", 0, 2), "Port_Thermal_Right"), (Socket("Down", 0, 2), "Port_Thermal_Down") }) ComparePorts(root, consumer, mapping.Item1, mapping.Item2);

            Require((string)ReadRuleObject(root, reactor + "BatteryProducer/ToStorage", typeof(string)) == "BatteryStorage", "Native reactor production was redirected");
            Require((int)ReadRuleObject(root, consumer + "BatteryStorage/MaxResources", typeof(int)) == 4000, "Native consumer capacity changed");
            Require((string)ReadRuleObject(root, "<ships/terran/terran.rules>/Terran/Parts/96/ID", typeof(string)) == "morgott.crewless_logistics.cable", "Cable was not appended to the vanilla registry");
            Require((string)ReadRuleObject(root, "<ships/terran/terran.rules>/Terran/Parts/97/ID", typeof(string)) == "morgott.crewless_logistics.cable_crossing", "Crew crossing was not appended to the vanilla registry");
            Require((string)ReadRuleObject(root, "<ships/terran/terran.rules>/Terran/Parts/98/ID", typeof(string)) == "morgott.crewless_logistics.cable_heat_crossing", "Independent heat/electric crossover was not appended to the registry");
            Require(((string[])ReadRuleObject(Open(Path.Combine(modDirectory, "cable_heat_crossing.rules")), "Part/OtherIDs", typeof(string[]))).SequenceEqual(new[] { "morgott.powergrid.cable_heat_crossing" }), "Heat crossover lost its pre-0.3.3 part ID alias");
            BeltPartsProbe(root);
            foreach (string cable in new[] { "cable.rules", "cable_crossing.rules", "belt_cable_crossing.rules" }) {
                var cableFile = Open(Path.Combine(modDirectory, cable));
                bool crossing = cable != "cable.rules";
                var sides = crossing ? new[] { "Up", "Down" } : new[] { "Up", "Right", "Down", "Left" };
                var id = (string)ReadRuleObject(cableFile, "Part/ID", typeof(string));
                Require(id == "morgott.crewless_logistics." + Path.GetFileNameWithoutExtension(cable), "Wrong cable part ID");
                Require(((string[])ReadRuleObject(cableFile, "Part/OtherIDs", typeof(string[]))).SequenceEqual(cable.StartsWith("belt_") ? Array.Empty<string>() : new[] { "morgott.powergrid." + Path.GetFileNameWithoutExtension(cable) }), "Cable lost its pre-0.3.3 part ID alias");
                foreach (var name in new[] { "CrewlessLogisticsNode", "CrewlessLogisticsBlueprintNode" }.Concat(sides.Select(side => "Port_" + side))) {
                    Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", cableFile);
                    added.Add(cable + ":" + name);
                }
                var physicalNode = Rules("Part/Components/CrewlessLogisticsNode", "Cosmoteer.Ships.Parts.PartComponentRules", cableFile);
                if (!crossing) {
                    Require((int)ReadRuleObject(cableFile, "Part/MaxHealth", typeof(int)) == (int)ReadRuleObject(root, "<ships/terran/heat_pipe_adaptive_structure/heat_pipe_adaptive_structure.rules>/Part/MaxHealth", typeof(int)), "Cable must have vanilla heat pipe durability");
                    Require((float)ReadRuleObject(cableFile, "Part/CrewSpeedFactor", typeof(float)) == 0, "Armored cable must block crew passage");
                }
                ValidateCableRuntimeLinks(cableFile, id, sides);
                Require(Flag(physicalNode, "HasPhysicalComponent"), "CrewlessLogisticsNode is absent from native physical part construction: " + cable);
                var thermalRules = Rules("PortProbeCableHeatLeft", "Cosmoteer.Ships.Networks.PartNetworkPortRules");
                foreach (string side in sides) {
                    var electricRules = Rules("Part/Components/Port_" + side, "Cosmoteer.Ships.Networks.PartNetworkPortRules", cableFile);
                    CheckElectricOverlay(electricRules);
                    Require(Field(electricRules.GetType(), "OperationalToggle").GetValue(electricRules) == null && Field(electricRules.GetType(), "BlueprintOperationalToggle").GetValue(electricRules) == null, "Passive cables must not require a device switch");
                    var electricOverlay = Field(electricRules.GetType(), "Overlay").GetValue(electricRules)!;
                    Require(Field(electricOverlay.GetType(), "IconDrawMode").GetValue(electricOverlay)!.ToString() == "Never", "Cable must not draw unused electrical socket markers");
                    Require(((Array)Property(electricRules, "Types")).Cast<object>().Select(type => type.ToString()).SequenceEqual(new[] { "morgott.crewless_logistics.electric" }), "Electrical cable retained thermal compatibility");
                    Require(!(bool)Call(electricRules, "IsCompatibleWith", thermalRules)! && !(bool)Call(thermalRules, "IsCompatibleWith", electricRules)!, "Native compatibility allows heat-pipe to electrical-cable connection");
                    Require(Flag(electricRules, "HasBlueprintComponent") && Flag(electricRules, "HasDestroyedComponent"), "Cable port missing blueprint/redprint representation");
                }
                if (crossing) {
                    var doors = (int[][])ReadRuleObject(cableFile, "Part/AllowedDoorLocations", typeof(int[][]));
                    Require(doors.Length == 2 && doors[0].SequenceEqual(new[] { -1, 0 }) && doors[1].SequenceEqual(new[] { 1, 0 }), "Crew crossing must preserve perpendicular vanilla doors");
                    Require(Math.Abs((float)ReadRuleObject(cableFile, "Part/CrewSpeedFactor", typeof(float)) - 0.8f) < 0.0001f, "Crew crossing changed vanilla speed");
                }
                if (!crossing) Require(((string[])ReadRuleObject(cableFile, "Part/Components/CableBlueprints/SituationToggles", typeof(string[]))).SequenceEqual(new[] { "Port_Up", "Port_Right", "Port_Down", "Port_Left" }), "Adaptive electrical preview must follow its actual four native ports");
            }
            CablePreviewRegression();
            CableAppearancePreview();
            HeatElectricCrossingProbe();
            ThermalIndicatorProbe();
            PackageNetworkSwitch(root, reactor, consumer);
            CableAvailabilityAndGraphics(root, reactor);
            SourceStoragePackageProbe(root);
            EngineRoomPowerProbe(root);
            ValidateConsumerCoverage(root);
            ValidateModdedDevices();
            OverlayPlacementProbe(root);
            scenarioResults.Add(new { name = "installable rules-only package", nativeActions = actions.Count, parsedComponents = added.Count });
            var report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "package-validation.json");
            File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, nativeActions = actions.Count, parsedComponents = added, physicalCableInitializationCases = 2, limits = "Native mod actions, selected rules and inactive non-rendering cable initialization; no game UI, rendering, full part constructor or live scheduling test." }, IndentedJson));
            Console.WriteLine($"PASS: installable mod; {actions.Count} native actions, {added.Count} component rules, shared positions, independent network switches; evidence={report}");
        } finally {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    void CheckElectricOverlay(object rules)
    {
        var overlay = Field(rules.GetType(), "Overlay").GetValue(rules)!;

        Require(Field(overlay.GetType(), "ConnectedEdgeIcon").GetValue(overlay)?.ToString() == "CrewlessLogisticsElectricDot", "Electric port lacks connected edge marker");
        bool device = Field(rules.GetType(), "OperationalToggle").GetValue(rules)?.ToString() == "CrewlessLogisticsToggle";
        Require(Field(overlay.GetType(), "RouteDrawMode").GetValue(overlay)!.ToString() == "Never", "Electrical diagnostic routes must not paint bright lines over cable artwork");
        Require(Field(overlay.GetType(), "IconDrawMode").GetValue(overlay)!.ToString() == (device ? "Primary" : "Never"), "Electrical icon mode does not match device/cable overlay policy");
    }

    void ValidateCableRuntimeLinks(object cableFile, string partId, string[] sides)
    {
        // Match the native construction filter before linking actual package ports.
        // Synthetic inactive ownership avoids starting simulation or rendering.
        NewTopologyPart(0, 0, 0);
        SetField(part, "p_ship", null);
        SetField(part, "_selfActive", false);
        var partRules = Property(part, "Rules");
        SetField(partRules, "ID", Id(Field(partRules.GetType(), "ID").FieldType, partId));
        var runtime = new List<object>();
        foreach (string name in new[] { "CrewlessLogisticsNode", "CrewlessLogisticsBlueprintNode" }.Concat(sides.Select(side => "Port_" + side))) {
            var rules = Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", cableFile);
            if (Flag(rules, "HasPhysicalComponent")) runtime.Add(Component(rules));
        }
        foreach (var component in runtime) Call(component, "OnAddedToPart", part);
        Require(runtime.Count == sides.Length + 1, "Unexpected physical electrical cable routing component set");
        scenarioResults.Add(new { name = "native physical cable initialization", partId, physicalComponents = runtime.Count });
        Console.WriteLine("PASS: native physical-component filtering and port/toggle initialization: " + partId);
    }

    void ComparePorts(object root, string prefix, string electric, string thermal)
    {
        var electricRules = Rules(prefix + electric, "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
        var thermalRules = Rules(prefix + thermal, "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
        foreach (string field in new[] { "Location", "Direction" }) Require(Equals(Field(electricRules.GetType(), field).GetValue(electricRules), Field(thermalRules.GetType(), field).GetValue(thermalRules)), "Package socket position differs from vanilla thermal socket");
    }
}
