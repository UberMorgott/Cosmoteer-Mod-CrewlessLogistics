using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

sealed partial class NativeProbe
{
    void CompareCatalogGeometry(JsonElement entry, object baseline)
    {
        string prefix = entry.GetProperty("path").GetString()! + "/";
        var geometry = entry.GetProperty("geometry");
        bool Has(string path) {
            var method = serializer.GetType().GetMethods(All).Where(m => m.Name == "HasPath" && !m.IsGenericMethod).OrderBy(m => m.GetParameters().Length).First();
            var parameters = method.GetParameters(); var arguments = new object?[parameters.Length]; arguments[0] = baseline; arguments[1] = path;
            for (int i = 2; i < arguments.Length; i++) arguments[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            return (bool)method.Invoke(serializer, arguments)!;
        }
        int[] Array(string path) => (int[])ReadRuleObject(baseline, prefix + path, typeof(int[]));
        Require(Array("Size").SequenceEqual(geometry.GetProperty("size").EnumerateArray().Select(x => x.GetInt32())), "Native Size differs from generated perimeter catalog");
        int[]? physical = Has(prefix + "PhysicalRect") ? Array("PhysicalRect") : null;
        Require(JsonSerializer.Serialize(physical) == geometry.GetProperty("physicalRect").GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""), "Native PhysicalRect differs from generated perimeter catalog");
        foreach (string side in new[] { "left", "right", "above", "below" }) {
            string name = "Prohibit" + char.ToUpperInvariant(side[0]) + side[1..];
            int distance = Has(prefix + name) ? (int)ReadRuleObject(baseline, prefix + name, typeof(int)) : 0;
            Require(distance == geometry.GetProperty("prohibitSides").GetProperty(side).GetInt32(), "Native directional prohibition differs from generated catalog");
        }
        foreach (var field in new[] { ("Prohibits", "prohibits"), ("ProhibitedBy", "prohibitedBy") }) {
            var categories = Has(prefix + field.Item1) ? (string[])ReadRuleObject(baseline, prefix + field.Item1, typeof(string[])) : new[] { "default" };
            Require(categories.SequenceEqual(geometry.GetProperty(field.Item2).EnumerateArray().Select(x => x.GetString()!)), "Native prohibition categories differ from generated catalog");
        }
        var expectedRects = geometry.GetProperty("prohibitRects").EnumerateArray().ToArray();
        int rectangleCount = Has(prefix + "ProhibitRects") ? ((IEnumerable)ReadRuleObject(baseline, prefix + "ProhibitRects", Core("Halfling.ObjectText.OTNode").MakeArrayType())).Cast<object>().Count() : 0;
        Require(rectangleCount == expectedRects.Length, "Native explicit prohibition count differs from generated catalog");
        for (int i = 0; i < rectangleCount; i++) {
            Require((string)ReadRuleObject(baseline, prefix + $"ProhibitRects/{i}/0", typeof(string)) == expectedRects[i].GetProperty("category").GetString() && Array($"ProhibitRects/{i}/1").SequenceEqual(expectedRects[i].GetProperty("rect").EnumerateArray().Select(x => x.GetInt32())), "Native explicit prohibition differs from generated catalog");
        }
    }

    int ValidateNativePerimeterGeometry(JsonElement entry)
    {
        var geometry = entry.GetProperty("geometry");
        var size = geometry.GetProperty("size").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        int[] physical = geometry.GetProperty("physicalRect").ValueKind == JsonValueKind.Array ? geometry.GetProperty("physicalRect").EnumerateArray().Select(x => x.GetInt32()).ToArray() : new[] { 0, 0, size[0], size[1] };
        var native = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartRules"));
        SetField(native, "Size", Vector(size[0], size[1]));
        var rectanglesField = Field(native.GetType(), "ProhibitRects");
        var rectangles = (IList)Activator.CreateInstance(rectanglesField.FieldType)!;
        var tupleType = rectanglesField.FieldType.GetGenericArguments()[0];
        var categoryType = tupleType.GetGenericArguments()[0];
        void AddRect(string category, int[] rect) {
            var bounds = Activator.CreateInstance(Core("Halfling.Geometry.IntRect"), rect.Cast<object>().ToArray())!;
            rectangles.Add(Activator.CreateInstance(tupleType, new[] { Id(categoryType, category), bounds })!);
        }
        foreach (var rectangle in geometry.GetProperty("prohibitRects").EnumerateArray()) AddRect(rectangle.GetProperty("category").GetString()!, rectangle.GetProperty("rect").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        var sides = geometry.GetProperty("prohibitSides");
        foreach (string side in new[] { "left", "right", "above", "below" }) {
            int distance = sides.GetProperty(side).GetInt32();
            if (distance <= 0) continue;
            int[] rect = side switch { "left" => new[] { -distance, 0, distance, size[1] }, "right" => new[] { size[0], 0, distance, size[1] }, "above" => new[] { 0, -distance, size[0], distance }, _ => new[] { 0, size[1], size[0], distance } };
            foreach (var category in geometry.GetProperty("prohibits").EnumerateArray()) AddRect(category.GetString()!, rect);
        }
        rectanglesField.SetValue(native, rectangles);
        bool IsForbidden(int x, int y) => (bool)Call(native, "IsCellProhibited", Id(categoryType, "default"), Vector(x, y), Vector(0, 0), 0, false)!;
        string Key(int x, int y, string direction) => $"{x},{y}/{direction}";
        var expected = new HashSet<string>(StringComparer.Ordinal);
        int rejected = 0;
        void Edge(int x, int y, string direction, int nx, int ny) {
            // Native normal-size occupancy reserves barrel/exhaust artwork cells
            // even when they lie outside PhysicalRect.
            if ((nx >= 0 && ny >= 0 && nx < size[0] && ny < size[1]) || IsForbidden(nx, ny)) { rejected++; return; }
            expected.Add(Key(x, y, direction));
        }
        for (int x = physical[0]; x < physical[0] + physical[2]; x++) {
            Edge(x, physical[1], "Up", x, physical[1] - 1);
            Edge(x, physical[1] + physical[3] - 1, "Down", x, physical[1] + physical[3]);
        }
        for (int y = physical[1]; y < physical[1] + physical[3]; y++) {
            Edge(physical[0], y, "Left", physical[0] - 1, y);
            Edge(physical[0] + physical[2] - 1, y, "Right", physical[0] + physical[2], y);
        }
        var ports = entry.GetProperty("ports").EnumerateArray().ToArray();
        var actual = ports.Select(p => Key(p.GetProperty("location")[0].GetInt32(), p.GetProperty("location")[1].GetInt32(), p.GetProperty("direction").GetString()!)).ToArray();
        Require(actual.Distinct(StringComparer.Ordinal).Count() == actual.Length && expected.SetEquals(actual), "Generated perimeter differs from native prohibition geometry: " + entry.GetProperty("id").GetString());
        // A deliberately added forbidden candidate must never be accepted by
        // the native predicate or the exact generated-edge set above.
        foreach (var port in ports) {
            var neighbor = port.GetProperty("neighbor");
            Require(!IsForbidden(neighbor[0].GetInt32(), neighbor[1].GetInt32()), "Generated socket enters native prohibited space");
        }
        return rejected;
    }

    void ValidateModdedDevices()
    {
        ValidateEnabledRootRegistry();
        var baseline = BuildEnabledRulesRoot(false);
        var candidate = BuildEnabledRulesRoot(true);
        ValidateCableMaterialLayer(candidate);
        // Revalidate vanilla adapters against the actual enabled mod action
        // order, then the additional standard native-schema mod equipment.
        ValidateConsumerCoverage(candidate, baseline: baseline);
        ValidateConsumerCoverage(candidate, modded: true, baseline: baseline);
        using var mapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "docs", "consumer-coverage.json")));
        var entries = mapping.RootElement.GetProperty("parts").EnumerateArray().Concat(mapping.RootElement.GetProperty("moddedParts").EnumerateArray()).ToArray();
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
        foreach (var entry in entries) {
            string prefix = entry.GetProperty("path").GetString()! + "/Components";
            var fields = (IDictionary)ReadRuleObject(candidate, prefix, dictionaryType);
            var actual = fields.Keys.Cast<string>().Where(name => name.StartsWith("CrewlessLogisticsPort", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var expected = entry.GetProperty("ports").EnumerateArray().Select(port => port.GetProperty("component").GetString()!).ToHashSet(StringComparer.Ordinal);
            Require(actual.SetEquals(expected), "Inherited/duplicate/out-of-geometry electrical ports require an explicit mapping: " + entry.GetProperty("id").GetString());
        }
        var actionsFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "mod", "generated.actions.rules") })!;
        var actionsType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
        // Optional Workshop targets are the generated actions marked IgnoreIfNotExisting.
        var actions = ((IList)ReadRuleObject(actionsFile, "Actions", actionsType)).Cast<object>().Where(a => (bool)Field(a.GetType(), "IgnoreIfNotExisting").GetValue(a)!).ToList();
        Require(actions.Count == mapping.RootElement.GetProperty("moddedGeneratedActions").GetInt32(), "Generated mod action count differs from mapping");
        var temporary = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "missing-mod-action.rules");
        string missing = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "absent-workshop-mod", "part.rules").Replace('\\', '/');
        string baseFile = Path.Combine(projectRoot, "mod", "consumer_base.rules").Replace('\\', '/');
        File.WriteAllText(temporary, $"Action = AddBase\nAddBaseTo = \"<{missing}>/Part/Components\"\nBaseToAdd = &<{baseFile}>/Components\nIgnoreIfNotExisting = true\n");
        var absentFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { temporary })!;
        var optionalAction = ReadRuleObject(absentFile, "", Game("Cosmoteer.Mods.ModAction"));
        Call(optionalAction, "ApplyAction", baseline);
        scenarioResults.Add(new { name = "enabled-mod action ordering and optional targets", additionalConsumers = actions.Count, exactInheritedPortSets = entries.Length, absentWorkshopTarget = "ignored by actual native ModAddBaseAction" });
        Console.WriteLine($"PASS: enabled native action order, {actions.Count} additional consumers, no inherited extra ports, optional missing workshop target.");
    }
}
