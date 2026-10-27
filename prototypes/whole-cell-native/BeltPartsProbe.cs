sealed partial class NativeProbe
{
    // Belt parts (docs/BELTS_DESIGN.md §2) after the native mod actions: registry order after the cable parts,
    // editor tabs, rotation, crew walkability, filter toggle and the replaced (not merged) conveyor animation.
    void BeltPartsProbe(object root)
    {
        const string parts = "<ships/terran/terran.rules>/Terran/Parts/";
        string[] ids = { "morgott.crewless_logistics.belt", "morgott.crewless_logistics.belt_splitter", "morgott.crewless_logistics.belt_connector" };
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
            Require((float)ReadRuleObject(root, p + "CrewSpeedFactor/Up", typeof(float)) == 1 && (float)ReadRuleObject(root, p + "CrewSpeedFactor/Down", typeof(float)) == 1, "Belt part must be crew-walkable at corridor speed: " + ids[i]);
            Require(!Has(p + "Components/Graphics/OperationalDoodad/Layer"), "Belt part still draws the vanilla crew-conveyor animation: " + ids[i]);
            Require((string)ReadRuleObject(root, p + "Components/Graphics/Floor/Layer", typeof(string)) == "floors", "Belt surface must stay under the item layer: " + ids[i]);
            Require(Has(p + "Components/BeltFilter/Type") == (i == 2), "Only the connector has the belt filter: " + ids[i]);
        }
        string filter = parts + "101/Components/BeltFilter/";
        Require((string)ReadRuleObject(root, filter + "ToggleID", typeof(string)) == "morgott.crewless_logistics.belt_filter"
            && (int)ReadRuleObject(root, filter + "Default", typeof(int)) == 0 && (bool)ReadRuleObject(root, filter + "RememberValue", typeof(bool)), "Connector filter must default to None and be remembered (blueprints)");
        Require(((int[])ReadRuleObject(root, parts + "100/Size", typeof(int[]))).SequenceEqual(new[] { 2, 1 }), "Splitter must be 1x2");
        Console.WriteLine("PASS: belt, splitter and connector registered after the cables; Storage+Power tabs, rotatable, crew-walkable, connector filter default None");
    }
}
