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

    /// <summary>
    /// R2 (owner crash 2026-10-28, 9dac7b9): list items need a comma or newline between them; two quoted strings separated by
    /// spaces only are concatenated into one path by the game's rules parser. Every referenced asset / rules file must exist:
    /// relative to the rules file, "./Data/..." under the game, or (mod action targets like AddTo) relative to Data.
    /// </summary>
    public static void Assets(Action<bool, string> check, string? gameRoot, string projectRoot)
    {
        string data = gameRoot == null ? "" : Path.Combine(gameRoot, "Data");
        int refs = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(projectRoot, "mod"), "*.rules", SearchOption.AllDirectories))
        {
            string text = Regex.Replace(File.ReadAllText(file), "//[^\n]*", "");
            string name = Path.GetRelativePath(projectRoot, file);
            var glued = Regex.Matches(text, "\"[^\"\n]*\"[ \t]+\"").Select(m => m.Value).ToList();
            check(glued.Count == 0, $"R2 {name}: list items comma/newline separated (space-only: {string.Join(" | ", glued.Take(2))})");
            var missing = new List<string>();
            foreach (Match m in Regex.Matches(text, "\"([^\"\n]+\\.(?:png|jpe?g|wav|ogg|shader))\"|<([^<>\"\n]+\\.rules)>"))
            {
                string path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                refs++;
                bool vanilla = path.StartsWith("./Data/", StringComparison.Ordinal);
                string local = Path.Combine(Path.GetDirectoryName(file)!, path);
                if (!vanilla && File.Exists(local))
                {
                    continue;
                }
                if (gameRoot == null)
                {
                    continue; // vanilla reference, game not installed: not checkable here
                }
                if (!File.Exists(Path.Combine(data, vanilla ? path["./Data/".Length..] : path)))
                {
                    missing.Add(path);
                }
            }
            check(missing.Count == 0, $"R2 {name}: referenced files exist (missing: {string.Join(", ", missing.Distinct().Take(5))})");
        }
        check(refs > 200, $"R2 {refs} file references checked");
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
