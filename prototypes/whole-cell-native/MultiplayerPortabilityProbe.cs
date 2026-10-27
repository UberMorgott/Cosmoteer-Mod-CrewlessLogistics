using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class NativeProbe
{
    void MultiplayerPortabilityProbe()
    {
        // Real ModAddBaseAction, native component deserialization and the game's
        // PortableHasher; no world, lobby, loader, or user configuration is opened.
        object Open(string path) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
        var actionsType = typeof(List<>).MakeGenericType(Game("Cosmoteer.Mods.ModAction"));
        var candidate = Open(Path.Combine(projectRoot, "mod", "generated.actions.rules"));
        var packageActions = ((IList)ReadRuleObject(candidate, "Actions", actionsType)).Cast<object>().Where(a => ((string)Field(a.GetType(), "AddBaseTo").GetValue(a)!).Contains("/workshop/")).ToList();
        Require(packageActions.Count == 7, "Multiplayer portability fixture expects all seven workshop compatibility targets");
        var relativeTargets = new List<string>();
        foreach (object action in packageActions) {
            string target = (string)Field(action.GetType(), "AddBaseTo").GetValue(action)!;
            Require(Regex.IsMatch(target, @"^<\.\./\.\./\.\./workshop/content/799600/[0-9]+/[^<>:]+\.rules>/Part/Components$"), "Workshop compatibility target is not Steam-library-relative: " + target);
            Require(!Regex.IsMatch(target, @"[A-Za-z]:|(?i:users[/\\]|appdata[/\\])"), "Workshop compatibility target contains a developer machine/user path");
            Require((bool)Field(action.GetType(), "IgnoreIfNotExisting").GetValue(action)!, "Absent optional workshop equipment must remain optional");
            relativeTargets.Add(target);
        }

        string evidenceRoot = Path.Combine(projectRoot, "artifacts", "multiplayer-portability");
        // Every run gets fresh OTFile names, avoiding any native file-node cache
        // from a preceding probe invocation or an already-applied action.
        string runRoot = Path.Combine(evidenceRoot, "runs", Guid.NewGuid().ToString("N"));
        const string workshop = "../../../workshop/content/799600/3602446152/Fixture/part.rules";
        const string adapter = """
            Components
            {
                CrewlessLogisticsCells
                {
                    Type = InlineResourceConverter
                    ResourceType = fake
                    FromStorage = BatteryStorage
                    FromQuantity = 1000
                    ToQuantity = 1
                    SuppliesResources = false
                }
                CrewlessLogisticsReceiver
                {
                    Type = NetworkResourceStore
                    ResourceType = fake
                    ResourceStorage = CrewlessLogisticsCells
                    AllowProviding = false
                    AllowReceiving = true
                    Categories = [morgott.crewless_logistics.cells.consumer]
                }
                CrewlessLogisticsPort
                {
                    Type = NetworkPort
                    Location = [0, 0]
                    Direction = Right
                    Types = [morgott.crewless_logistics.electric]
                    RouteEndpoint = CrewlessLogisticsReceiver
                }
            }
            """;
        const string partRules = """
            Part
            {
                Components
                {
                    BatteryStorage
                    {
                        Type = ResourceStorage
                        ResourceType = battery
                        MaxResources = 4000
                        StartingResources = 0
                        SuppliesResources = false
                        IsDrainable = false
                    }
                }
            }
            """;
        var nodeDictionary = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
        Type componentType = Game("Cosmoteer.Ships.Parts.PartComponentRules");
        var componentListType = typeof(List<>).MakeGenericType(componentType);
        Type hasherType = Core("Halfling.PortableHashing.PortableHasher");
        MethodInfo hashMethod = hasherType.GetMethod("GetPortableHash", new[] { typeof(object), typeof(MemberInfo) })!;
        int Hash(object value) => (int)hashMethod.Invoke(Activator.CreateInstance(hasherType), new object?[] { value, null })!;

        (object Root, string Data, string Part) Fixture(string scenario, string machine)
        {
            string data = Path.Combine(runRoot, scenario, machine, "steamapps", "common", "Cosmoteer", "Data");
            string partPath = Path.GetFullPath(Path.Combine(data, workshop));
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
            File.WriteAllText(Path.Combine(data, "cosmoteer.rules"), "FixtureRoot = true\n");
            File.WriteAllText(Path.Combine(data, "fixture-adapter.rules"), adapter);
            File.WriteAllText(partPath, partRules);
            return (Open(Path.Combine(data, "cosmoteer.rules")), data, partPath);
        }
        (int Hash, string[] Components) Apply((object Root, string Data, string Part) fixture, string target)
        {
            string actionPath = Path.Combine(fixture.Data, "fixture-action.rules");
            File.WriteAllText(actionPath, "Action = AddBase\nAddBaseTo = \"" + target + "\"\nBaseToAdd = &<fixture-adapter.rules>/Components\nIgnoreIfNotExisting = true\n");
            var action = ReadRuleObject(Open(actionPath), "", Game("Cosmoteer.Mods.ModAction"));
            Call(action, "ApplyAction", fixture.Root);
            string componentsPath = "<" + workshop + ">/Part/Components";
            var nodes = (IDictionary)ReadRuleObject(fixture.Root, componentsPath, nodeDictionary);
            string[] names = nodes.Keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var components = (IList)Activator.CreateInstance(componentListType)!;
            foreach (string name in names)
                components.Add(ReadRuleObject(fixture.Root, componentsPath + "/" + name, componentType));
            int hash = Hash(components);
            Require(hash == Hash(components), "Fresh native PortableHasher instances disagree on the same component state");
            return (hash, names);
        }

        var oldA = Fixture("absolute", "machine-a");
        var oldB = Fixture("absolute", "machine-b");
        string absoluteA = "<" + oldA.Part.Replace('\\', '/') + ">/Part/Components";
        // A separate absent absolute target models a peer without the original
        // developer's library. The fixture does not hide/rename real files or
        // pretend that two simultaneous roots have isolated Windows namespaces.
        string absentB = Path.Combine(oldB.Data, "missing-developer-install", "part.rules");
        Require(File.Exists(oldA.Part) && !File.Exists(absentB), "Absolute-path availability negative control is invalid");
        var applied = Apply(oldA, absoluteA);
        var skipped = Apply(oldB, "<" + absentB.Replace('\\', '/') + ">/Part/Components");
        Require(applied.Components.Length == 4 && skipped.Components.SequenceEqual(new[] { "BatteryStorage" }), "Native optional AddBase did not apply/skip according to target availability");
        Require(applied.Hash != skipped.Hash, "Old optional absolute-target application mismatch did not change native simulation component hash");

        var newA = Fixture("relative", "machine-a");
        var newB = Fixture("relative", "machine-b");
        string portableTarget = "<" + workshop + ">/Part/Components";
        var relocatedA = Apply(newA, portableTarget);
        var relocatedB = Apply(newB, portableTarget);
        Require(newA.Part != newB.Part && File.Exists(newA.Part) && File.Exists(newB.Part), "Relocated fixture does not represent separate Steam libraries");
        Require(relocatedA.Components.SequenceEqual(applied.Components) && relocatedB.Components.SequenceEqual(applied.Components), "Relative AddBase omitted native electrical components after relocation");
        Require(relocatedA.Hash == relocatedB.Hash && relocatedA.Hash == applied.Hash, "Identical relative compatibility actions produced different native component hashes after Steam-library relocation");

        Directory.CreateDirectory(evidenceRoot);
        File.WriteAllText(Path.Combine(evidenceRoot, "native-component-hash-validation.json"), JsonSerializer.Serialize(new {
            passed = true, verifiedAtUtc = DateTimeOffset.UtcNow, packageRelativeTargets = relativeTargets,
            fixtureRun = Path.GetRelativePath(evidenceRoot, runRoot).Replace('\\', '/'),
            absoluteTarget = new { appliedHash = applied.Hash, skippedHash = skipped.Hash, appliedComponents = applied.Components, skippedComponents = skipped.Components, hashesDiffer = applied.Hash != skipped.Hash },
            relocatedTarget = new { machineAHash = relocatedA.Hash, machineBHash = relocatedB.Hash, components = relocatedA.Components, identicalHash = relocatedA.Hash == relocatedB.Hash },
            limits = "Real native optional ModAddBaseAction and PartComponentRules hashing in isolated OTFile fixtures. Absolute-missing peer is explicitly modeled with an absent fixture path, not separate filesystem namespaces. Representative native battery/cell-view/store/port adapter; does not start game, lobby, multiplayer simulation or hash the entire Rules object. Installed package byte identity is checked separately by installer."
        }, IndentedJson));
        scenarioResults.Add(new { name = "native multiplayer compatibility path portability", absoluteMismatchHashDetected = true, relocatedSteamLibrariesHashIdentical = true, packageRelativeWorkshopTargets = relativeTargets.Count });
        Console.WriteLine("PASS: native optional absolute-target mismatch changes component hash; identical relative targets across two Steam libraries preserve it; seven candidate workshop targets are portable.");
    }
}
