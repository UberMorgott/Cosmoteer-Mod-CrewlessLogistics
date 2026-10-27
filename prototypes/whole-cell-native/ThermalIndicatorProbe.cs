using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

sealed partial class NativeProbe
{
    // Exercise the vanilla renderer's graph decomposition and offset helper.
    // No renderer, allocator or diagnostic drawing code is installed by the mod.
    void ThermalIndicatorProbe(bool requireCurrentRules = true)
    {
        var source = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "mod", "cable_heat_crossing.rules") })!;
        var rendererType = Game("Cosmoteer.Ships.Blueprints.Graphics.NetworkPortBlueprintOverlayRenderer");
        var componentType = Game("Cosmoteer.Ships.Parts.PartComponentRules");
        var bpType = Game("Cosmoteer.Ships.Blueprints.BlueprintPart");
        var bpComponentType = Game("Cosmoteer.Ships.Blueprints.BlueprintPartComponent");
        var bpNodeType = Game("Cosmoteer.Ships.Networks.ComponentNode`2").MakeGenericType(bpType, bpComponentType);
        var getOffset = rendererType.GetMethods(All | BindingFlags.Static).Single(m => m.Name.Contains("g___GetOffset|") && m.GetParameters().Length == 2);
        var shouldDraw = rendererType.GetMethods(All | BindingFlags.Static).Single(m => m.Name.Contains("g___ShouldDrawPortRoutes|") && m.GetParameters().Length == 2);
        var getPortDrawData = rendererType.GetMethod("GetPortDrawData", All | BindingFlags.Static)!;
        var icon = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Game.Gui.PartNetworkOverlayIcon"));
        var namesType = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
        var names = ((IDictionary)ReadRuleObject(source, "Part/Components", namesType)).Keys.Cast<string>().ToArray();
        object Read(string name) => Rules("Part/Components/" + name, componentType.FullName!, source);
        string RuleId(object rules) => Field(rules.GetType(), "ID").GetValue(rules)!.ToString()!;
        object[] NodeAndPortRules(bool candidate)
        {
            var result = new List<object>();
            foreach (string name in names) {
                string type = (string)ReadRuleObject(source, "Part/Components/" + name + "/Type", typeof(string));
                if (type != "NetworkBasicNode" && type != "NetworkPort") continue;
                var rules = Read(name);
                if (type == "NetworkBasicNode" && !Flag(rules, "HasBlueprintComponent")) continue;
                if (candidate && name == "OverlaySpacingNode") continue;
                if (candidate && name.StartsWith("Port_")) {
                    bool thermal = name == "Port_Up" || name == "Port_Down";
                    Route(rules, "BlueprintRouteSource", thermal ? "CrewlessLogisticsBlueprintNode" : "BlueprintNetworkNode");
                    Route(rules, "BlueprintRouteEndpoint", thermal ? "CrewlessLogisticsBlueprintNode" : "BlueprintNetworkNode");
                    if (thermal) {
                        var overlay = Field(rules.GetType(), "Overlay").GetValue(rules)!;
                        var mode = Field(overlay.GetType(), "RouteDrawMode");
                        mode.SetValue(overlay, Enum.Parse(mode.FieldType, "Connected"));
                        SetField(rules, "Overlay", overlay);
                    }
                }
                result.Add(rules);
            }
            if (candidate) {
                var spacing = Read("CrewlessLogisticsBlueprintNode");
                SetField(spacing, "ID", Id(Field(spacing.GetType(), "ID").FieldType, "OverlaySpacingNode"));
                result.Add(spacing);
            }
            return result.ToArray();
        }
        object Decompose(object[] rules, bool bothChannels, int transform)
        {
            var bp = RuntimeHelpers.GetUninitializedObject(bpType);
            foreach (string field in new[] { "_components", "_componentsByID" }) SetField(bp, field, Activator.CreateInstance(Field(bpType, field).FieldType)!);
            // Actual native components initialize their own route references via
            // BlueprintPart.AddComponents; only ownership/registration is synthetic.
            var components = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(bpComponentType))!;
            foreach (var rule in rules) components.Add(Call(rule, "CreateBlueprintComponent")!);
            Call(bp, "AddComponents", components);
            foreach (var c in components.Cast<object>()) {
                if (c.GetType().Name == "BlueprintPartNetworkBasicNode") {
                    var node = Activator.CreateInstance(bpNodeType, new object?[] { null, c })!;
                    Call(c, "OnNetworkRegistering", node);
                } else SetField(c, "<IsActive>k__BackingField", true);
            }
            var renderer = RuntimeHelpers.GetUninitializedObject(rendererType);
            foreach (string field in new[] { "_nodes", "_nodeStack", "_primaryPorts" }) SetField(renderer, field, Activator.CreateInstance(Field(rendererType, field).FieldType)!);
            var primary = (IList)Field(rendererType, "_primaryPorts").GetValue(renderer)!;
            var ppType = primary.GetType().GetGenericArguments()[0];
            foreach (var c in components.Cast<object>().Where(c => c.GetType().Name != "BlueprintPartNetworkBasicNode")) {
                var rule = c.GetType().GetProperty("Rules", All | BindingFlags.DeclaredOnly)!.GetValue(c)!;
                string id = RuleId(rule);
                if (bothChannels || id == "Port_Up" || id == "Port_Down") primary.Add(Activator.CreateInstance(ppType, new object?[] { rule, c })!);
            }
            var strongType = rendererType.GetNestedType("StrongComponent", BindingFlags.NonPublic)!;
            var strong = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(strongType))!;
            Call(renderer, "GetStrongComponents", bp, strong);
            string thermalId = Property(rules.Single(r => RuleId(r) == "Port_Up"), "BlueprintRouteSource").ToString()!;
            string electricId = Property(rules.Single(r => RuleId(r) == "Port_Electric_Left"), "BlueprintRouteSource").ToString()!;
            int Find(string id)
            {
                for (int i = 0; i < strong.Count; i++) foreach (var n in (IEnumerable)Field(strongType, "Nodes").GetValue(strong[i])!) {
                        if (n.GetType().Name != "NetworkNodeWrapper") continue;
                        var underlying = Property(n, "UnderlyingNode");
                        if (Property(underlying, "ID").ToString() == id) return i;
                    }
                throw new InvalidOperationException("Native renderer omitted blueprint node " + id);
            }
            int thermalIndex = Find(thermalId), electricIndex = Find(electricId);
            Require(thermalIndex != electricIndex, "Thermal and electrical blueprint paths merged");
            Require(strong.Cast<object>().All(s => ((IList)Field(strongType, "AccessibleNeighbours").GetValue(s)!).Count == 0), "Independent blueprint paths gained an inter-channel route");
            var offset = getOffset.Invoke(null, new object[] { 1f / (strong.Count + 1f), thermalIndex })!;
            float x = (float)Property(offset, "X"), y = (float)Property(offset, "Y");
            // The renderer applies this world-axis offset independently of the
            // part transform. Zero therefore preserves every rotated edge.
            var partRules = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartRules"));
            SetField(partRules, "Size", Vector(1, 1));
            var info = Activator.CreateInstance(Game("Cosmoteer.Ships.Parts.PartInfo"), new object[] { partRules, Vector(7, -3), transform & 3, (transform & 4) != 0 })!;
            float[] Midpoint(string name)
            {
                var rule = rules.Single(r => RuleId(r) == name);
                var pair = ((IEnumerable)Call(rule, "GetPartCells")!).Cast<object>().Single();
                var draw = getPortDrawData.Invoke(null, new[] { info, Field(pair.GetType(), "Item1").GetValue(pair), Field(pair.GetType(), "Item2").GetValue(pair), icon, null, false, true })!;
                var point = Property(draw, "Midpoint");
                return new[] { (float)Property(point, "X"), (float)Property(point, "Y") };
            }
            var up = Midpoint("Port_Up"); var down = Midpoint("Port_Down");
            Require(Math.Abs((up[0] + down[0]) / 2f - 7.5f) < 0.00001f && Math.Abs((up[1] + down[1]) / 2f + 2.5f) < 0.00001f, "Native rotated thermal sockets no longer share the exact tile center");
            Require(Math.Abs(Math.Abs(up[0] - down[0]) + Math.Abs(up[1] - down[1]) - 1f) < 0.00001f, "Native thermal overlay does not span the full tile edge-to-edge");
            return new { transform, bothChannels, components = strong.Count, thermalIndex, electricIndex, x, y, physicalUpMidpoint = up, physicalDownMidpoint = down, routeUpMidpoint = new[] { up[0] + x, up[1] + y }, routeDownMidpoint = new[] { down[0] + x, down[1] + y } };
        }
        var candidateRules = NodeAndPortRules(true);
        var candidateCases = new List<object>();
        foreach (int transform in Enumerable.Range(0, 8)) foreach (bool both in new[] { false, true }) {
                var result = Decompose(candidateRules, both, transform);
                Require((int)Property(result, "components") == 3 && (int)Property(result, "thermalIndex") == 1 && (float)Property(result, "x") == 0f && (float)Property(result, "y") == 0f, "Native three-node spacing does not center the thermal channel");
                candidateCases.Add(result);
            }
        var legacyRules = NodeAndPortRules(true).Where(r => RuleId(r) != "OverlaySpacingNode").ToArray();
        foreach (var rule in legacyRules.Where(r => RuleId(r).StartsWith("Port_"))) {
            bool thermal = RuleId(rule) is "Port_Up" or "Port_Down";
            Route(rule, "BlueprintRouteSource", thermal ? "BlueprintNetworkNode" : "CrewlessLogisticsBlueprintNode");
            Route(rule, "BlueprintRouteEndpoint", thermal ? "BlueprintNetworkNode" : "CrewlessLogisticsBlueprintNode");
        }
        var legacy = Decompose(legacyRules, false, 0);
        Require((int)Property(legacy, "components") == 2 && Math.Abs((float)Property(legacy, "x") - 1f / 6f) < 0.00001f && Math.Abs((float)Property(legacy, "y") - 1f / 6f) < 0.00001f, "Negative control no longer reproduces native two-network displacement");
        var actualRules = NodeAndPortRules(false);
        var currentCases = new List<object>();
        foreach (int transform in Enumerable.Range(0, 8)) foreach (bool both in new[] { false, true }) currentCases.Add(Decompose(actualRules, both, transform));
        foreach (var rule in candidateRules.Where(r => RuleId(r) is "Port_Up" or "Port_Down")) {
            var bpPort = Call(rule, "CreateBlueprintComponent")!;
            Require(!(bool)shouldDraw.Invoke(null, new[] { bpPort, (object)false })! && (bool)shouldDraw.Invoke(null, new[] { bpPort, (object)true })!, "Thermal route must be native Connected, not always visible or hidden");
        }
        string report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "thermal-indicator-validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, candidateCases, currentCases, negativeControl = legacy, graphEngine = "Actual vanilla GetStrongComponents, _GetOffset and GetPortDrawData", limits = "Native renderer CPU graph and rotated port helpers with synthetic blueprint ownership and direct node registration; no GPU or live world." }, IndentedJson));
        if (requireCurrentRules) {
            foreach (var actual in currentCases) Require((int)Property(actual, "components") == 3 && (int)Property(actual, "thermalIndex") == 1 && (float)Property(actual, "x") == 0f && (float)Property(actual, "y") == 0f, "Current mixed crossing still shifts the orange thermal indicator; use electric BlueprintNetworkNode, thermal CrewlessLogisticsBlueprintNode, then isolated OverlaySpacingNode");
            foreach (var rule in actualRules.Where(r => RuleId(r) is "Port_Up" or "Port_Down")) {
                var bpPort = Call(rule, "CreateBlueprintComponent")!;
                Require(!(bool)shouldDraw.Invoke(null, new[] { bpPort, (object)false })! && (bool)shouldDraw.Invoke(null, new[] { bpPort, (object)true })!, "Current mixed crossing must draw only connected orange thermal routes");
            }
        }
        Console.WriteLine($"PASS: native thermal diagnostic spacing; {candidateCases.Count} candidate graph cases and independent channels; evidence={report}");
    }
}
