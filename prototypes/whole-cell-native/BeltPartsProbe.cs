sealed partial class NativeProbe
{
    // Belt parts (docs/BELTS_DESIGN.md §2) after the native mod actions: registry order after the cable parts,
    // editor tabs, rotation, crew walkability, filter toggle and the replaced (not merged) conveyor animation.
    void BeltPartsProbe(object root)
    {
        const string parts = "<ships/terran/terran.rules>/Terran/Parts/";
        string[] ids = { "morgott.crewless_logistics.belt", "morgott.crewless_logistics.belt_splitter", "morgott.crewless_logistics.belt_connector",
            "morgott.crewless_logistics.belt_crossing" };
        const string vanillaCrossing = "<ships/terran/heat_pipe_crossing/heat_pipe_crossing.rules>/Part/";
        bool Has(string path)
        {
            try {
                ReadRuleObject(root, path, typeof(string));
                return true;
            } catch {
                return false;
            }
        }
        for (int i = 0; i < ids.Length; i++) {
            string p = parts + (99 + i) + "/";
            Require((string)ReadRuleObject(root, p + "ID", typeof(string)) == ids[i], "Belt part was not appended to the vanilla registry: " + ids[i]);
            Require(((string[])ReadRuleObject(root, p + "EditorGroups", typeof(string[]))).SequenceEqual(new[] { "Storage", "Power" }), "Belt part must be in the Storage and Power build tabs: " + ids[i]);
            Require((bool)ReadRuleObject(root, p + "IsRotateable", typeof(bool)) && !(bool)ReadRuleObject(root, p + "IsFlippable", typeof(bool)), "Belt part must rotate and not flip: " + ids[i]);
            if (i < 3) {
                // Q6 (owner): belt walkable any way at .7; splitter and connector block crew (0, like armor).
                float crew = i == 0 ? 0.7f : 0f;
                foreach (var dir in new[] { "Up", "Left", "Right", "Down" })
                    Require(Math.Abs((float)ReadRuleObject(root, p + "CrewSpeedFactor/" + dir, typeof(float)) - crew) < 0.0001f, "Belt part crew speed must be " + crew + " " + dir + ": " + ids[i]);
            } else {
                // Crossings keep the vanilla heat pipe crossing body: crew walk along the belt (Left/Right doors) at .7 (Q6),
                // and its health, density and penetration (owner: pipe stats parity, no armor exploit).
                var doors = (int[][])ReadRuleObject(root, p + "AllowedDoorLocations", typeof(int[][]));
                Require(doors.Length == 2 && doors[0].SequenceEqual(new[] { -1, 0 }) && doors[1].SequenceEqual(new[] { 1, 0 })
                    && Math.Abs((float)ReadRuleObject(root, p + "CrewSpeedFactor", typeof(float)) - 0.7f) < 0.0001f, "Belt crossing must keep vanilla crossing doors along the belt and crew speed .7: " + ids[i]);
                foreach (var stat in new[] { "MaxHealth", "Density", "InitialPenetrationResistance", "ContinuingPenetrationResistance" })
                    Require((float)ReadRuleObject(root, p + stat, typeof(float)) == (float)ReadRuleObject(root, vanillaCrossing + stat, typeof(float)), ids[i] + " " + stat + " differs from the vanilla heat pipe crossing");
                // Nothing may draw above the belt items: no pipe cylinder, darkness overlay or heat glow.
                foreach (var g in new[] { "Darkness_Graphics/NonOperationalDoodad/Layer", "HeatGlow_Graphics/OperationalDoodad/Layer", "HeatGlow_Graphics/OperationalRoofDoodad/Layer" })
                    Require(!Has(p + "Components/" + g), "Belt crossing still draws " + g + ": " + ids[i]);
                // §25 network axis = part Up/Down (belt front is part cell (1, 0) in BeltManager): thermal and electric
                // ports on both edges (each links only a compatible neighbour), none on the belt axis.
                foreach (var side in new[] { "Up", "Down" }) {
                    Require(((string[])ReadRuleObject(root, p + "Components/Port_" + side + "/Types", typeof(string[]))).SequenceEqual(new[] { "thermal" }), "Belt crossing thermal port has the wrong type: Port_" + side);
                    Require(((string[])ReadRuleObject(root, p + "Components/ElectricPort_" + side + "/Types", typeof(string[]))).SequenceEqual(new[] { "morgott.crewless_logistics.electric" }), "Belt crossing electric port has the wrong type: ElectricPort_" + side);
                }
                foreach (var side in new[] { "Left", "Right" })
                    Require(!Has(p + "Components/Port_" + side + "/Type") && !Has(p + "Components/ElectricPort_" + side + "/Type"), "Belt crossing must not have network ports on the belt axis: " + side);
                // Saves and blueprints of the two former crossings load as this part.
                Require(((string[])ReadRuleObject(root, p + "OtherIDs", typeof(string[]))).SequenceEqual(new[] { "morgott.crewless_logistics.belt_heat_crossing", "morgott.crewless_logistics.belt_cable_crossing" }), "Belt crossing must keep the former crossing IDs in OtherIDs");
            }
            Require(!Has(p + "Components/Graphics/OperationalDoodad/Layer"), "Belt part still draws the vanilla crew-conveyor animation: " + ids[i]);
            Require((string)ReadRuleObject(root, p + "Components/Graphics/Floor/Layer", typeof(string)) == "floors", "Belt surface must stay under the item layer: " + ids[i]);
            Require(Has(p + "Components/BeltFilter/Type") == (i == 2), "Only the connector has the belt filter: " + ids[i]);
        }
        string filter = parts + "101/Components/BeltFilter/";
        Require((string)ReadRuleObject(root, filter + "ToggleID", typeof(string)) == "morgott.crewless_logistics.belt_filter"
            && (int)ReadRuleObject(root, filter + "Default", typeof(int)) == 0 && (bool)ReadRuleObject(root, filter + "RememberValue", typeof(bool)), "Connector filter must default to None and be remembered (blueprints)");
        Require(((int[])ReadRuleObject(root, parts + "100/Size", typeof(int[]))).SequenceEqual(new[] { 2, 1 }), "Splitter must be 1x2");
        Console.WriteLine("PASS: belt, splitter, connector and adaptive belt crossing (old heat/cable crossing IDs in OtherIDs) registered after the cables; Storage+Power tabs, rotatable, belt crew .7 / splitter+connector crew-blocked, connector filter default None; crossings keep vanilla crossing stats/doors, crew .7, Up/Down thermal + electric ports only, nothing drawn above items");
    }
}
