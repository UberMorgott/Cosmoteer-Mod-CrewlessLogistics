using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Halfling.ObjectText;

namespace CrewlessLogistics.Belts;

/// <summary>Headless coverage checks (belts/tests GameTests, called by reflection).</summary>
internal static partial class NetworkCoverage
{
    /// <summary>Optional Workshop mods whose parts the former generated coverage handled (checked when installed).</summary>
    public static readonly string[] EquivalenceMods = { "3602446152", "3541853292" };

    public static List<(bool Ok, string What)> SelfTest(string gameRoot, string projectRoot)
    {
        var r = new List<(bool, string)>();
        Fixture(r, projectRoot);
        string cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = gameRoot; // OT resolves "./Data/..." lazily, against the game folder as in game
            Equivalence(r, gameRoot, projectRoot);
        }
        catch (Exception ex)
        {
            r.Add((false, "C2 threw " + ex));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
        return r;
    }

    private static void Equivalence(List<(bool, string)> r, string gameRoot, string projectRoot)
    {
        string workshop = Path.GetFullPath(Path.Combine(gameRoot, "..", "..", "workshop", "content", "799600"));
        string[] mods = EquivalenceMods.Select(id => Path.Combine(workshop, id)).Append(Path.Combine(projectRoot, "mod")).ToArray();
        r.Add((true, "C2 mods: " + string.Join(", ", mods.Where(Directory.Exists).Select(Path.GetFileName))));
        OTFile hooked = BuildRoot(gameRoot, mods);
        Report report = Apply(hooked);
        r.Add((report.NotApplied == null && report.Covered.Values.Sum() > 50, "C2 " + report.Summary()));
        foreach (string s in report.Skipped)
        {
            r.Add((true, "C2 skipped " + s));
        }
        var actual = Fingerprint(hooked);
        string baselinePath = Path.Combine(projectRoot, "belts", "tests", "coverage-baseline.tsv");
        // Baseline: Fingerprint of the former generated rules (the retired PowerShell generator, removed once this
        // matched with 0 diffs). Hashes of resolved values only, so it is machine-independent.
        var expected = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (string[] f in File.ReadAllLines(baselinePath).Select(l => l.Split('\t')))
        {
            (expected.TryGetValue(f[0], out var c) ? c : expected[f[0]] = new(StringComparer.Ordinal))[f[1]] = f[2];
        }
        int compared = 0, covered = 0;
        var diffs = new List<string>();
        foreach (var (id, want) in expected)
        {
            if (!actual.TryGetValue(id, out var got))
            {
                r.Add((true, $"C2 baseline part {id} not registered here (optional mod absent)"));
                continue;
            }
            compared++;
            covered += want.Keys.Any(k => k.StartsWith("CrewlessLogisticsPort_", StringComparison.Ordinal)) ? 1 : 0;
            var keys = want.Keys.Union(got.Keys).Where(k => want.GetValueOrDefault(k) != got.GetValueOrDefault(k)).ToList();
            if (keys.Count > 0)
            {
                diffs.Add($"{id}: {string.Join(",", keys.Take(6))}");
            }
        }
        foreach (string extra in actual.Keys.Except(expected.Keys))
        {
            r.Add((true, $"C2 part {extra} not in the baseline (newly installed mod)"));
        }
        r.Add((diffs.Count == 0, $"C2 equivalence: {compared} parts compared, {covered} with sockets, {diffs.Count} diffs {string.Join("; ", diffs.Take(20))}"));
    }

    /// <summary>Synthetic registry: a consumer, a part inheriting it with a different size, one changing its storage, and a non-device inheriting it.</summary>
    private static void Fixture(List<(bool, string)> r, string projectRoot)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cl-coverage-fixture-" + Environment.ProcessId);
        try
        {
            void Put(string rel, string text)
            {
                string path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            foreach (string t in new[] { "consumer_base.rules", "common.rules", "engine_room_power.rules", "source-components.rules", "capacitor.rules" })
            {
                Put("mod/" + t, File.ReadAllText(Path.Combine(projectRoot, "mod", t)));
            }
            Put("mod/cable.rules", "Part { ID = morgott.crewless_logistics.cable; Size = [1, 1]; Components { CrewlessLogisticsNode { Type = NetworkBasicNode } } }");
            Put("Data/cosmoteer.rules", "");
            Put("Data/ships/terran/terran.rules", "Terran { Parts [ &<a/a.rules>/Part, &<b/b.rules>/Part, &<c/c.rules>/Part, &<d/d.rules>/Part, &<f/f.rules>/Part, &<e/e.rules>/Part, &<g/g.rules>/Part, &<h/h.rules>/Part, &<r/r.rules>/Part, &<../../../mod/cable.rules>/Part ] }");
            // Codex review regressions. e: inherits consumer f but already has its own adapter child (kept; f must not leak).
            // g: reserve only inside a ToggledComponents branch. h: receiver pool with dynamic ViaBuffs members.
            // r: reactor without the IsOperational toggle the source template references.
            const string battery = "BatteryStorage { Type = ResourceStorage; ResourceType = battery; MaxResources = 2000 }";
            Put("Data/ships/terran/f/f.rules", "Part { ID = f; Size = [1, 1]; Components { " + battery + " BatteryConsumer { Type = ResourceConsumer; ResourceType = battery; Storage = BatteryStorage } } }");
            Put("Data/ships/terran/e/e.rules", "Part : <../f/f.rules>/Part { ID = e; Components : ^/0/Components { CrewlessLogisticsToggle { Type = UIToggle; ToggleID = \"e.own\" } } }");
            Put("Data/ships/terran/g/g.rules", "Part { ID = g; Size = [1, 1]; Components { On { Type = UIToggle; ToggleID = \"g.on\" } Branch { Type = ToggledComponents; Toggle = On; Components { " + battery + " } } BatteryConsumer { Type = ResourceConsumer; ResourceType = battery; Storage = BatteryStorage } } }");
            Put("Data/ships/terran/h/h.rules", "Part { ID = h; Size = [1, 1]; Components { Reserve { Type = ResourceStorage; ResourceType = battery; MaxResources = 2000 } Provider { Type = GridBuffProvider; BuffType = Engine } Combined { Type = MultiResourceStorage; ResourceType = battery; ResourceStorages = [Reserve]; ViaBuffs { OutgoingBuffProviders = [Provider]; ComponentIDs = [Reserve] } } BatteryConsumer { Type = ResourceConsumer; ResourceType = battery; Storage = Combined } } }");
            Put("Data/ships/terran/r/r.rules", "Part { ID = r; Size = [1, 1]; TypeCategories = [reactor]; Components { " + battery + " BatteryProducer { Type = ResourceConverter; ToStorage = BatteryStorage } } }");
            Put("Data/ships/terran/a/a.rules", "Part { ID = a; Size = [2, 2]; Components { BatteryStorage { Type = ResourceStorage; ResourceType = battery; MaxResources = 2000 } BatteryConsumer { Type = ResourceConsumer; ResourceType = battery; Storage = BatteryStorage } } }");
            Put("Data/ships/terran/b/b.rules", "Part : <../a/a.rules>/Part { ID = b; Size = [1, 1] }");
            Put("Data/ships/terran/c/c.rules", "Part : <../a/a.rules>/Part { ID = c; Components : ^/0/Components { Reserve { Type = ResourceStorage; ResourceType = battery; MaxResources = 3000 } Proxy { Type = ResourceStorageProxy; ResourceType = battery; ComponentID = Reserve } BatteryConsumer { Type = ResourceConsumer; ResourceType = battery; Storage = Proxy } } }");
            Put("Data/ships/terran/d/d.rules", "Part : <../a/a.rules>/Part { ID = d; Components : ^/0/Components { BatteryConsumer; } }");
            var rollbackRoot = new OTFile(Path.Combine(dir, "Data", "cosmoteer.rules"));
            var before = Fingerprint(rollbackRoot);
            FailAfterEdits = 5;
            string? rollback = null;
            try
            {
                Apply(rollbackRoot);
            }
            catch (InvalidOperationException ex)
            {
                rollback = ex.Message;
            }
            finally
            {
                FailAfterEdits = -1;
            }
            var after = Fingerprint(rollbackRoot);
            r.Add((rollback?.Contains("5 edits rolled back", StringComparison.Ordinal) == true && before.Count == after.Count && before.All(p => p.Value.SequenceEqual(after[p.Key])),
                "C1 failure while applying rolls every edit back (rules identical to before): " + rollback));
            var root = new OTFile(Path.Combine(dir, "Data", "cosmoteer.rules"));
            Report first = Apply(root);
            r.Add((first.Covered.GetValueOrDefault(DeviceKind.Consumer) == 3 && first.Derived == 3, "C1 fixture " + first.Summary()));
            Dictionary<string, List<string>> Names() => Read(root, out _, out _).ToDictionary(e => e.Id, e => ((OTGroupNode)e.Part.FindAtPath("Components", e.Ctx, out OTContext? c)).GetChildrenWithContext(c)
                .Where(x => x.Child is not OTVoidNode).Select(x => x.Child.Name).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList());
            string Ports(List<string> n) => string.Join(",", n.Where(x => x.StartsWith("CrewlessLogisticsPort_", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            var names = Names();
            r.Add((names.Values.All(n => n.Count == n.Distinct().Count()), "C1 no duplicated adapter children in any part"));
            r.Add((names["a"].Count(n => n.StartsWith("CrewlessLogisticsPort_", StringComparison.Ordinal)) == 8 && names["a"].Contains("CrewlessLogisticsToggle"), "C1 a (2x2 consumer): 8 sockets + adapter: " + Ports(names["a"])));
            r.Add((Ports(names["b"]) == "CrewlessLogisticsPort_Down_0_0,CrewlessLogisticsPort_Left_0_0,CrewlessLogisticsPort_Right_0_0,CrewlessLogisticsPort_Up_0_0" && names["b"].Contains("CrewlessLogisticsReceiver"),
                "C1 b (inherits a, 1x1): own 4 sockets, none of a's: " + Ports(names["b"])));
            string[]? agg(string id) => Read(root, out _, out _).Where(e => e.Id == id).Select(e => Try(e.Part, e.Ctx, "Components/CrewlessLogisticsReceivingAggregate/ResourceStorages", out string[]? v) ? v : null).Single();
            r.Add((string.Join(",", agg("c") ?? Array.Empty<string>()) == "Proxy,CrewlessLogisticsReceivingBias" && string.Join(",", agg("a") ?? Array.Empty<string>()) == "BatteryStorage,CrewlessLogisticsReceivingBias",
                $"C1 c (inherits a, own storage): receiver {string.Join(",", agg("c") ?? Array.Empty<string>())}; a keeps {string.Join(",", agg("a") ?? Array.Empty<string>())}"));
            r.Add((names["d"].Count == 0, "C1 d (inherits a, no consumer): no adapter children: " + string.Join(",", names["d"])));
            string Skip(string id) => first.Skipped.FirstOrDefault(s => s.StartsWith(id + " ", StringComparison.Ordinal) || s.StartsWith(id + ":", StringComparison.Ordinal)) ?? "(not skipped)";
            r.Add((names["f"].Count == 0 && names["e"].SequenceEqual(new[] { "CrewlessLogisticsToggle" }) && Skip("f").Contains("leak into e", StringComparison.Ordinal),
                "C1 e keeps its own adapter child, f is not covered: " + Skip("f")));
            r.Add((names["g"].Count == 0 && Skip("g").Contains("ToggledComponents", StringComparison.Ordinal), "C1 g (reserve in a branch) skipped: " + Skip("g")));
            r.Add((names["h"].Count == 0 && Skip("h").Contains("ViaBuffs", StringComparison.Ordinal), "C1 h (ViaBuffs pool) skipped: " + Skip("h")));
            r.Add((names["r"].Count == 0 && Skip("r").Contains("IsOperational", StringComparison.Ordinal), "C1 r (reactor without IsOperational) skipped: " + Skip("r")));
            Report second = Apply(root);
            r.Add((second.Covered.Count == 0 && second.Derived == 0 && Names().All(p => p.Value.SequenceEqual(names[p.Key])), "C1 second run changes nothing (idempotent)"));
        }
        catch (Exception ex)
        {
            r.Add((false, "C1 fixture threw " + ex));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Effective adapter fingerprint per registered part: one hash per CrewlessLogistics* component of the fully
    /// resolved (inheritance + context) Components tree, plus "#names" over every effective component name, so a
    /// leaked or duplicated child shows up. Order-insensitive within groups; paths never enter the hash.
    /// </summary>
    public static SortedDictionary<string, SortedDictionary<string, string>> Fingerprint(OTFile root)
    {
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (Entry e in Read(root, out _, out _))
        {
            if (e.Part.FindAtPath("Components", e.Ctx, out OTContext? cc) is not OTGroupNode components)
            {
                continue;
            }
            var children = components.GetChildrenWithContext(cc).Where(x => x.Child is not OTVoidNode).ToList();
            var parts = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["#names"] = Hash(string.Join(",", children.Select(x => x.Child.Name).Order(StringComparer.Ordinal))),
            };
            foreach (var (child, c) in children.Where(x => x.Child.Name.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                var sb = new StringBuilder();
                Flat(child, c, sb, 0);
                parts[child.Name] = Hash(sb.ToString());
            }
            result[e.Id] = parts;
        }
        return result;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static void Flat(IOTNode node, OTContext? ctx, StringBuilder sb, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("Reference depth exceeded at " + node.PathWithFile);
        }
        switch (node)
        {
            case OTVoidNode:
                sb.Append(';');
                break;
            case OTFieldNode f:
                sb.Append('=').Append(f.Value);
                break;
            case OTReferenceNode r:
                Flat(r.FindFinalTarget(ctx, out OTContext? rc), rc, sb, depth + 1);
                break;
            case OTGroupNode g:
                sb.Append('{');
                foreach (var (child, c) in g.GetChildrenWithContext(ctx).OrderBy(x => x.Child.Name, StringComparer.Ordinal))
                {
                    sb.Append(child.Name);
                    Flat(child, c, sb, depth + 1);
                    sb.Append(';');
                }
                sb.Append('}');
                break;
            case OTListNode l:
                sb.Append('[');
                foreach (var (child, c) in l.GetChildrenWithContext(ctx))
                {
                    Flat(child, c, sb, depth + 1);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
        }
    }

    /// <summary>
    /// Game rules root with the given mod folders' native actions applied in ModInfo.LoadMods order (ordinal mod ID),
    /// the same ApplyAction calls the game makes. Never loads mod DLLs.
    /// </summary>
    public static OTFile BuildRoot(string gameRoot, IEnumerable<string> modFolders)
    {
        string cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = gameRoot; // "./Data/..." paths resolve against the game folder, as in game
            Halfling.App.ObjectTextSerializer ??= S;
            var mods = new List<(string Id, OTFile Manifest)>();
            foreach (string folder in modFolders.Where(Directory.Exists))
            {
                string? manifest = Cosmoteer.Mods.ModInfo.GetModInfoPath(new Halfling.IO.AbsolutePath(folder))?.ToString();
                if (manifest != null)
                {
                    var file = new OTFile(manifest);
                    mods.Add((S.ReadFromPath<string>(file, "ID"), file));
                }
            }
            var root = new OTFile(Path.Combine(gameRoot, "Data", "cosmoteer.rules"));
            foreach (OTFile manifest in mods.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Manifest))
            {
                if (S.HasPath(manifest, "Actions"))
                {
                    foreach (Cosmoteer.Mods.ModAction action in S.ReadFromPath<List<Cosmoteer.Mods.ModAction>>(manifest, "Actions"))
                    {
                        action.ApplyAction(root);
                    }
                }
            }
            return root;
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
