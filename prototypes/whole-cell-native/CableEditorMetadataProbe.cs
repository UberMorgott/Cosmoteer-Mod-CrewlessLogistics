using System.Collections;
sealed partial class NativeProbe
{
    void CableEditorMetadataProbe()
    {
        foreach (var fileName in new[] { "cable.rules", "cable_crossing.rules", "cable_heat_crossing.rules" }) {
            var source = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "mod", fileName) })!;
            var groups = fileName == "cable_heat_crossing.rules" ? new[] { "Power", "Thermal" } : new[] { "Power" };
            Require(((string[])ReadRuleObject(source, "Part/EditorGroups", typeof(string[]))).SequenceEqual(groups), "Ordinary cables belong in Power; the mixed crossover must also appear in Thermal");
            Require(((Array)ReadRuleObject(source, "Part/EditorParentParts", Core("Halfling.ObjectText.OTNode").MakeArrayType())).Length == 0, "Cable must not create a parent/submenu hierarchy");
            Require(((string[])ReadRuleObject(source, "Part/DontStackInEditorGroups", typeof(string[]))).SequenceEqual(groups), "Cable button must not create a separate stacked section");
            Require(((Array)ReadRuleObject(source, "Part/Resources", Core("Halfling.ObjectText.OTNode").MakeArrayType())).Length == 2, "Cable recipe has unexpected resources");
            Require((string)ReadRuleObject(source, "Part/Resources/0/0", typeof(string)) == "coil" && (int)ReadRuleObject(source, "Part/Resources/0/1", typeof(int)) == 4, "Cable must cost four coils");
            Require((string)ReadRuleObject(source, "Part/Resources/1/0", typeof(string)) == "steel" && (int)ReadRuleObject(source, "Part/Resources/1/1", typeof(int)) == 2, "Cable must cost two steel");
            // Owner requirement: cable durability/stats equal the vanilla heat pipe counterpart; no armor exploit.
            var pipeName = fileName == "cable.rules" ? "heat_pipe_adaptive_structure" : "heat_pipe_crossing";
            var pipe = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(gameRoot, "Data", "ships", "terran", pipeName, pipeName + ".rules") })!;
            foreach (var stat in new[] { "MaxHealth", "Density", "InitialPenetrationResistance", "ContinuingPenetrationResistance" })
                Require((float)ReadRuleObject(source, "Part/" + stat, typeof(float)) == (float)ReadRuleObject(pipe, "Part/" + stat, typeof(float)), fileName + " " + stat + " differs from vanilla " + pipeName);
            var categories = (string[])ReadRuleObject(source, "Part/TypeCategories", typeof(string[]));
            Require(!categories.Contains("armor") && !categories.Contains("non_flammable"), fileName + " must not carry armor categories");
            var components = ((IDictionary)ReadRuleObject(source, "Part/Components", typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode")))).Keys.Cast<string>();
            Require(!components.Contains("EmpAbsorber"), fileName + " must not absorb EMP like armor");
            Console.WriteLine($"  {fileName}: MaxHealth={ReadRuleObject(source, "Part/MaxHealth", typeof(float))} Density={ReadRuleObject(source, "Part/Density", typeof(float))} TypeCategories=[{string.Join(", ", categories)}] (= {pipeName})");
        }
        Console.WriteLine("PASS: cables in Power and mixed crossover additionally in Thermal, without nested sections; each costs four coils and two steel; health, density and penetration equal the vanilla heat pipes, no armor category.");
    }
}
