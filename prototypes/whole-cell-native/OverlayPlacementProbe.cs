using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

sealed partial class NativeProbe
{
    void OverlayPlacementProbe(object root)
    {
        var rendererType = Game("Cosmoteer.Ships.Blueprints.Graphics.NetworkPortBlueprintOverlayRenderer");
        var renderer = RuntimeHelpers.GetUninitializedObject(rendererType);
        var methods = rendererType.GetMethods(All | BindingFlags.Static);
        var getOffset = methods.Single(m => m.Name.Contains("g___GetOffset|") && m.GetParameters().Length == 2);
        var shouldDrawRoutes = methods.Single(m => m.Name.Contains("g___ShouldDrawPortRoutes|") && m.GetParameters().Length == 2);
        var shouldDrawIcon = methods.Single(m => m.Name == "ShouldDrawPortIcon");
        var getPortDrawData = methods.Single(m => m.Name == "GetPortDrawData");
        var icon = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Game.Gui.PartNetworkOverlayIcon"));
        // Execute the actual native compiler-generated helper, rather than
        // reproducing its arithmetic. Independent blueprint nodes receive this
        // diagonal displacement before compatible icons/spokes are generated.
        var offset = getOffset.Invoke(null, new object[] { 1f / 3f, 1 })!;
        float ox = (float)Property(offset, "X");
        float oy = (float)Property(offset, "Y");
        Require(Math.Abs(ox + 1f / 6f) < 0.00001f && Math.Abs(oy + 1f / 6f) < 0.00001f, "Native dual-node diagonal offset changed");

        const string large = "<ships/terran/reactor_large/reactor_large.rules>/Part/Components/";
        var rules = Rules(large + PerimeterSocket(4, "Up", 0), "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
        var overlay = Field(rules.GetType(), "Overlay").GetValue(rules)!;
        Require(Field(overlay.GetType(), "RouteDrawMode").GetValue(overlay)!.ToString() == "Never", "Device spokes still use the native displaced diagram");
        Require(Field(overlay.GetType(), "IconDrawMode").GetValue(overlay)!.ToString() == "Primary", "Compatible device icons still use native displacement");
        Require((bool)shouldDrawIcon.Invoke(renderer, new object?[] { rules, true, null })!, "Exact primary device socket marker was hidden");
        Require(!(bool)shouldDrawIcon.Invoke(renderer, new object?[] { rules, false, null })!, "Displaced compatible socket icon was not hidden");
        // DrawPorts adds the connected-edge dot only for records admitted above.
        // Suppressing compatible icon records therefore suppresses their dots too.
        var blueprintPort = rules.GetType().GetMethod("CreateBlueprintComponent", All)!.Invoke(rules, null)!;
        foreach (bool connected in new[] { false, true }) Require(!(bool)shouldDrawRoutes.Invoke(null, new[] { blueprintPort, (object)connected })!, "Displaced device route was retained");
        var oldRules = Rules(large + PerimeterSocket(4, "Up", 0), "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
        var oldOverlay = Field(oldRules.GetType(), "Overlay").GetValue(oldRules)!;
        var modeField = Field(oldOverlay.GetType(), "IconDrawMode");
        modeField.SetValue(oldOverlay, Enum.Parse(modeField.FieldType, "Always"));
        var routeField = Field(oldOverlay.GetType(), "RouteDrawMode");
        routeField.SetValue(oldOverlay, Enum.Parse(routeField.FieldType, "Connected"));
        SetField(oldRules, "Overlay", oldOverlay);
        var oldBlueprintPort = oldRules.GetType().GetMethod("CreateBlueprintComponent", All)!.Invoke(oldRules, null)!;
        Require((bool)shouldDrawIcon.Invoke(renderer, new object?[] { oldRules, false, null })! && (bool)shouldDrawRoutes.Invoke(null, new[] { oldBlueprintPort, (object)true })!, "Negative control no longer reproduces displaced compatible diagram");

        var partRules = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartRules"));
        SetField(partRules, "Size", Vector(4, 4));
        int exactPlacementCases = 0;
        foreach (int rotation in Enumerable.Range(0, 4)) foreach (bool flip in new[] { false, true }) {
                var info = Activator.CreateInstance(Game("Cosmoteer.Ships.Parts.PartInfo"), new object[] { partRules, Vector(7, -3), rotation, flip })!;
                foreach (string side in new[] { "Up", "Right", "Down", "Left" }) for (int i = 0; i < 4; i++) {
                        var electric = Rules(large + PerimeterSocket(4, side, i), "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
                        var thermal = Rules(large + $"Port_Thermal_{side}{i}", "Cosmoteer.Ships.Networks.PartNetworkPortRules", root);
                        object DrawData(object portRules)
                        {
                            var pair = ((IEnumerable)Call(portRules, "GetPartCells")!).Cast<object>().Single();
                            return getPortDrawData.Invoke(null, new[] { info, Field(pair.GetType(), "Item1").GetValue(pair), Field(pair.GetType(), "Item2").GetValue(pair), icon, null, false, true })!;
                        }
                        var actual = Property(DrawData(electric), "Midpoint");
                        var original = Property(DrawData(thermal), "Midpoint");
                        Require(Equals(actual, original), "Native primary electrical marker no longer matches native thermal attachment geometry");
                        if (rotation == 0 && !flip && side == "Up" && i == 0) {
                            Require(Math.Abs((float)Property(actual, "X") - 7.5f) < 0.00001f && Math.Abs((float)Property(actual, "Y") + 3f) < 0.00001f, "Native primary placement midpoint is displaced from the exact physical edge");
                        }
                        exactPlacementCases++;
                    }
            }
        var cableFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "mod", "cable.rules") })!;
        var cable = Rules("Part/Components/Port_Up", "Cosmoteer.Ships.Networks.PartNetworkPortRules", cableFile);
        var cableOverlay = Field(cable.GetType(), "Overlay").GetValue(cable)!;
        Require(Field(cableOverlay.GetType(), "RouteDrawMode").GetValue(cableOverlay)!.ToString() == "Never" && Field(cableOverlay.GetType(), "IconDrawMode").GetValue(cableOverlay)!.ToString() == "Never", "Cable diagnostic overlays must not cover the physical conduit");
        var report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "overlay-placement-validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, nativeDualNodeOffset = new[] { ox, oy }, exactPlacementCases, deviceCompatibleIcons = false, deviceInternalSpokes = false, primarySocketMarkers = true, passiveCableRoutes = "Never", limits = "Actual native renderer CPU helpers and native rules; full renderer/GPU/live screenshot not exercised. Physical conduit art replaces diagnostic cable routes. This hides the native displaced compatible-device diagram rather than realigning a complete dual-network diagram." }, new JsonSerializerOptions { WriteIndented = true }));
        scenarioResults.Add(new { name = "native device overlay placement", exactPlacementCases, nativeDualNodeOffset = new[] { ox, oy }, workaround = "retain exact primary sockets; hide displaced compatible device icons and spokes" });
        Console.WriteLine($"PASS: native overlay offset negative control, hidden displaced device diagram, {exactPlacementCases} exact native primary socket placements; evidence={report}");
    }
}
