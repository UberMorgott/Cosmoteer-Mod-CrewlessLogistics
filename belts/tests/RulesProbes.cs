using System.Text.RegularExpressions;

namespace CrewlessLogistics.Belts.Tests;

// Rules lint: ToggledComponents children are added to the part's flat component dictionary, so an ID declared inside a
// nested Components block must be unique across the part, including the IDs inherited from vanilla corridor/conveyor.
// A collision crashes the game on placement (Part.AddComponentsNoQueue: "An item with the same key has already been added").
static class RulesProbes
{
    public static void Run(Action<bool, string> check, string gameRoot, string projectRoot)
    {
        var inherited = NestedIds(File.ReadAllText(Path.Combine(gameRoot, @"Data\ships\terran\corridor\corridor.rules")));
        check(inherited.Contains("DarknessBlend"), $"R1 vanilla corridor nested ids parsed: {string.Join(",", inherited)}");
        foreach (var file in Directory.GetFiles(Path.Combine(projectRoot, "mod"), "*.rules"))
        {
            var ids = NestedIds(File.ReadAllText(file));
            var clash = ids.Where(inherited.Contains).Concat(ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)).Distinct().ToList();
            check(clash.Count == 0, $"R1 {Path.GetFileName(file)} nested component ids unique ({ids.Count} nested, clashes: {string.Join(",", clash)})");
        }
    }

    // IDs of keys declared (followed by ':' or '{') under two or more enclosing Components blocks.
    static List<string> NestedIds(string text)
    {
        var ids = new List<string>();
        var stack = new List<string>();
        string pending = "";
        text = Regex.Replace(text, "\"[^\"]*\"|//[^\n]*", "");
        foreach (Match m in Regex.Matches(text, @"(?<![/\w.])(\w+)\s*(?=[:{])|\{|\}"))
        {
            if (m.Value == "{")
            {
                stack.Add(pending);
                pending = "";
            }
            else if (m.Value == "}")
            {
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                pending = m.Groups[1].Value;
                if (stack.Count(s => s == "Components") >= 2)
                {
                    ids.Add(pending);
                }
            }
        }
        return ids;
    }
}
