using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

try {
    if (args.Length > 3 && args[2] == "--inspect-save") {
        // Read only: decode the game's native ObjectBits nodes without creating
        // a world, executing mod DLLs, or writing back to the user's save.
        var coreAssembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "HalflingCore.dll"));
        var nodeType = coreAssembly.GetType("Halfling.ObjectBits.OBNode", true)!;
        using var saved = new MemoryStream(File.ReadAllBytes(args[3]), false);
        var headerBytes = new byte[9]; saved.ReadExactly(headerBytes);
        if (System.Text.Encoding.ASCII.GetString(headerBytes) != "COSMOSAVE") throw new InvalidDataException("Unsupported save header");
        using var headerReader = new BinaryReader(saved, System.Text.Encoding.UTF8, true);
        var header = Activator.CreateInstance(nodeType, headerReader, false)!;
        using var compressed = new System.IO.Compression.GZipStream(saved, System.IO.Compression.CompressionMode.Decompress, true);
        using var reader = new BinaryReader(compressed);
        var body = Activator.CreateInstance(nodeType, reader, false)!;
        string output = Path.Combine(args[1], "artifacts", "latest-save-native.txt");
        File.WriteAllText(output, (string)nodeType.GetMethod("GetSubtreeString")!.Invoke(body, null)!);
        Console.WriteLine("Read-only native save inspection written to " + output);
        return 0;
    }
    int modsArgument = Array.IndexOf(args, "--mods");
    var probe = new NativeProbe(args[0], args[1]) { ModsDirectory = modsArgument > 1 ? args[modsArgument + 1] : null };
    if (args.Length > 2 && args[2] == "--catalog") probe.BuildDeviceCatalog(args.Contains("--validate-candidate"));
    else probe.Run();
    return 0;
} catch (Exception error) {
    while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException;
    Console.Error.WriteLine(error);
    return 1;
}

// Fixture only: production algorithms are invoked from the actual game assembly.
sealed partial class NativeProbe
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    readonly Assembly game;
    readonly Assembly core;
    readonly string gameRoot;
    readonly string projectRoot;
    readonly object rulesFile;
    readonly object serializer;
    readonly Type serialStateType;
    object part;
    readonly object ship;
    readonly IDictionary totals;
    IDictionary componentMap;
    readonly List<object> scenarioResults = new();
    int checks;

    public NativeProbe(string gameRoot, string projectRoot)
    {
        this.gameRoot = gameRoot;
        this.projectRoot = projectRoot;
        game = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Cosmoteer.dll"));
        core = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "HalflingCore.dll"));
        serializer = Activator.CreateInstance(Core("Halfling.Serialization.ObjectText.ObjectTextSerializer"), new object[] { true })!;
        rulesFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] {
            Path.Combine(projectRoot, "prototypes", "whole-cell-native", "components.rules")
        })!;
        serialStateType = Core("Halfling.Serialization.SerialState");

        // Ownership fixtures provide Rand, a zero logical clock and native totals.
        // No live world, process, topology manager, or game save is opened.
        ship = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Ship"));
        part = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.Part"));
        SetField(part, "p_ship", ship);
        SetField(ship, "<Rand>k__BackingField", Activator.CreateInstance(Core("Halfling.Random.FastRand"), new object[] { 0UL })!);
        SetField(ship, "_root", RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Simulation.SimRoot")));
        var manager = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Resources.ResourceManager"));
        var totalsField = Field(manager.GetType(), "_storedResourceTotals");
        totals = (IDictionary)Activator.CreateInstance(totalsField.FieldType)!;
        totalsField.SetValue(manager, totals);
        SetField(ship, "<Resources>k__BackingField", manager);
        var mapField = Field(part.GetType(), "_componentsByID");
        componentMap = (IDictionary)Activator.CreateInstance(mapField.FieldType)!;
        mapField.SetValue(part, componentMap);
    }

    Type Game(string name) => game.GetType(name, true)!;
    Type Core(string name) => core.GetType(name, true)!;
    static FieldInfo Field(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType) {
            var field = current.GetField(name, All | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.FullName, name);
    }
    static void SetField(object target, string name, object? value) => Field(target.GetType(), name).SetValue(target, value);
    static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, All)!.Invoke(target, args);
    static int Count(object target, string property) => (int)target.GetType().GetProperty(property, All)!.GetValue(target)!;
    static void Add(object storage, int quantity) => Call(storage, "AddResources", quantity);
    static void SetCount(object storage, int quantity) => storage.GetType().GetProperty("Resources", All)!.SetValue(storage, quantity);
    static object Id(Type idType, string name) => Activator.CreateInstance(idType, new object[] { name })!;
    internal static void Route(object rules, string property, string id)
    {
        var p = rules.GetType().GetProperty(property, All)!;
        p.SetValue(rules, Id(Nullable.GetUnderlyingType(p.PropertyType)!, id));
    }
    // Generated device sockets are named CrewlessLogisticsPort_{Direction}_{x}_{y}.
    internal static string Socket(string side, int x, int y) => $"CrewlessLogisticsPort_{side}_{x}_{y}";
    static string PerimeterSocket(int size, string side, int i) => side switch { "Up" => Socket(side, i, 0), "Down" => Socket(side, i, size - 1), "Left" => Socket(side, 0, i), _ => Socket(side, size - 1, i) };
    internal static string[] PerimeterSockets(int size) => new[] { "Up", "Right", "Down", "Left" }.SelectMany(side => Enumerable.Range(0, size).Select(i => PerimeterSocket(size, side, i))).ToArray();
    // Vanilla 2x2 thermal port suffix -> electrical socket on the same edge.
    internal static readonly (string Thermal, string Electric)[] SmallSockets = {
        ("TopLeft", Socket("Left", 0, 0)), ("LeftUp", Socket("Up", 0, 0)), ("RightUp", Socket("Up", 1, 0)), ("TopRight", Socket("Right", 1, 0)),
        ("BottomRight", Socket("Right", 1, 1)), ("RightDown", Socket("Down", 1, 1)), ("LeftDown", Socket("Down", 0, 1)), ("BottomLeft", Socket("Left", 0, 1))
    };

    object Rules(string path, string typeName, object? sourceFile = null) => ReadRuleObject(sourceFile ?? rulesFile, path, Game(typeName));

    object ReadRuleObject(object sourceFile, string path, Type type)
    {
        using var state = (IDisposable)serialStateType.GetMethod("BeginOperation", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object?[] { null })!;
        var contextSetter = serialStateType.GetMethods(All).Single(m => m.Name == "SetCustomObject" && !m.IsGenericMethod && m.GetParameters().Length == 2);
        contextSetter.Invoke(state, new object?[] { Game("Cosmoteer.Ships.Parts.PartRules"), null });
        var componentIdType = Game("Cosmoteer.Data.ID`1").MakeGenericType(Game("Cosmoteer.Ships.Parts.PartComponentRules"));
        contextSetter.Invoke(state, new[] { componentIdType, Id(componentIdType, path.Split('/')[^1]) });
        var method = serializer.GetType().GetMethods(All).Single(m => m.Name == "ReadFromPath" && !m.IsGenericMethod && m.GetParameters().Length == 6 && m.GetParameters()[2].ParameterType == typeof(Type));
        var flags = Activator.CreateInstance(method.GetParameters()[3].ParameterType);
        return method.Invoke(serializer, new object?[] { sourceFile, path, type, flags, null, null })!;
    }

    object Component(string template, string rulesType)
    {
        var rules = Rules(template, rulesType);
        return Component(rules);
    }
    object Component(object rules)
    {
        var result = rules.GetType().GetMethod("CreateComponent", All)!.Invoke(rules, null)!;
        Attach(result);
        componentMap[Field(rules.GetType(), "ID").GetValue(rules)!] = result;
        return result;
    }
    void Attach(object component) => SetField(component, "<Part>k__BackingField", part);
    object Storage(string template, int capacity, int amount)
    {
        var rules = Rules(template, "Cosmoteer.Ships.Parts.Resources.ResourceStorageRules");
        SetField(rules, "MaxResources", capacity);
        SetField(rules, "StartingResources", 0);
        var storage = Component(rules);
        SetCount(storage, amount);
        return storage;
    }
    void InitializeLinks(object component) => Call(component, "OnAddedToPart2", part);
    object Inline(string template, object backing)
    {
        // The backing ID comes from the parsed fixture; native lifecycle resolves it.
        var rules = Rules(template, "Cosmoteer.Ships.Parts.Resources.InlineResourceConverterRules");
        componentMap[Field(rules.GetType(), "FromStorage").GetValue(rules)!] = backing;
        var result = Component(rules);
        InitializeLinks(result);
        return result;
    }
    object Receiver(object battery, out object bias)
    {
        bias = Storage("ReceivingBias", 999, 999);
        var rules = Rules("ReceivingAggregate", "Cosmoteer.Ships.Parts.Resources.MultiResourceStorageRules");
        var ids = (Array)Field(rules.GetType(), "ResourceStorages").GetValue(rules)!;
        componentMap[ids.GetValue(0)!] = battery;
        componentMap[ids.GetValue(1)!] = bias;
        var aggregate = Component(rules);
        InitializeLinks(aggregate);
        return Inline("ReceiverCells", aggregate);
    }
    object Store(string template, object backing)
    {
        var rules = Rules(template, "Cosmoteer.Ships.Networks.PartNetworkResourceStoreRules");
        componentMap[Field(rules.GetType(), "ResourceStorage").GetValue(rules)!] = backing;
        var store = Component(rules);
        InitializeLinks(store);
        Call(store, "OnNetworkRegistered");
        return store;
    }
    object NetworkIo(string template, bool source, object[] stores, bool allowFiltered = false)
    {
        var prefix = "Cosmoteer.Ships.Networks.";
        var rules = Rules(template, prefix + (source ? "PartNetworkResourceOutputRules" : "PartNetworkResourceInputRules"));
        var io = Component(rules);
        var types = new[] { Game("Cosmoteer.Ships.Parts.Part"), Game("Cosmoteer.Ships.Parts.PartComponent") };
        var cacheType = Game(prefix + "Queries.SubnetworkQueryCache`2").MakeGenericType(types);
        var cache = Activator.CreateInstance(cacheType, All, null, new object?[] { null }, null)!;
        var queryType = Game(prefix + "Queries.PartNetworkResourceQuery");
        var query = Activator.CreateInstance(queryType, All, null, new[] {
            Field(rules.GetType(), "ResourceType").GetValue(rules),
            Field(rules.GetType(), "NetworkFilter").GetValue(rules)
        }, null)!;
        var resultType = Game(prefix + "Queries." + (source ? "SubnetworkResourceSourceQueryResult`2" : "SubnetworkResourceSinkQueryResult`2")).MakeGenericType(types);
        var result = Activator.CreateInstance(resultType, All, null, new[] { cache, query }, null)!;
        var data = (IList)Field(resultType, "_data").GetValue(result)!;
        var indices = (IDictionary)Field(resultType, "_dataIndices").GetValue(result)!;
        foreach (var store in stores) {
            bool matches = (bool)Call(result, "MatchesCriteria", store)!;
            if (allowFiltered && !matches) continue;
            Require(matches, "Native network resource/category filter rejected a connected store");
            var entry = Call(result, "OnAddition", store);
            indices.Add(store, data.Count);
            data.Add(entry);
        }
        var queriesField = Field(cacheType, source ? "_resourceSourceQueries" : "_resourceSinkQueries");
        var queries = (IDictionary)Activator.CreateInstance(queriesField.FieldType)!;
        queries.Add(query, result);
        queriesField.SetValue(cache, queries);
        SetField(io, source ? "_subnetworkSources" : "_subnetworkSinks", result);
        return io;
    }
    object Pump(object output, object input, string template = "CellTransfer")
    {
        var pump = Component(template, "Cosmoteer.Ships.Parts.Resources.ResourceConverterRules");
        ((Array)Field(pump.GetType(), "_fromStorages").GetValue(pump)!).SetValue(output, 0);
        ((Array)Field(pump.GetType(), "_toStorages").GetValue(pump)!).SetValue(input, 0);
        return pump;
    }
    void Pulse(object pump) => Call(pump, "OnConversionTick", new object?[] { null });
    sealed record Circuit(object[] Sources, object[] Targets, object[] Bias, object Pump);
    Circuit CircuitOf(int[] sourceAmounts, int[] targetAmounts, int targetCapacity = 4000, bool naive = false, string pumpTemplate = "CellTransfer")
    {
        var sources = sourceAmounts.Select(amount => Storage("SourceStorage", Math.Max(8000, amount), amount)).ToArray();
        var sourceStores = sources.Select(storage => Store("SourceStore", Inline("SourceCells", storage))).ToArray();
        var targets = targetAmounts.Select(amount => Storage("ReceiverStorage", targetCapacity, amount)).ToArray();
        var biases = new List<object>();
        var targetStores = targets.Select(storage => {
            object receiver;
            if (naive) receiver = Inline("SourceCells", storage);
            else {
                receiver = Receiver(storage, out var bias);
                biases.Add(bias);
            }
            return Store("ReceiverStore", receiver);
        }).ToArray();
        var output = NetworkIo("NetworkOutput", true, sourceStores);
        var input = NetworkIo("NetworkInput", false, targetStores);
        return new Circuit(sources, targets, biases.ToArray(), Pump(output, input, pumpTemplate));
    }
    static int Energy(Circuit circuit) => circuit.Sources.Concat(circuit.Targets).Sum(storage => Count(storage, "Resources"));
    void CheckedPulse(Circuit circuit)
    {
        var storages = circuit.Sources.Concat(circuit.Targets).ToArray();
        var before = storages.Select(storage => Count(storage, "Resources")).ToArray();
        var totalBefore = before.Sum();
        var trackedBefore = totals.Keys.Cast<object>().ToDictionary(key => key, key => (int)totals[key]!);
        Pulse(circuit.Pump);
        Require(Energy(circuit) == totalBefore, "Native IO created or lost battery energy");
        for (int i = 0; i < storages.Length; i++) {
            int after = Count(storages[i], "Resources");
            Require((after - before[i]) % 1000 == 0, "A network transfer split a cell");
            Require(after >= 0 && after <= Count(storages[i], "MaxResources"), "Native storage exceeded capacity");
        }
        Require(circuit.Bias.All(storage => Count(storage, "Resources") == 999), "The non-energy bias changed");
        foreach (object key in totals.Keys) Require(trackedBefore.GetValueOrDefault(key) == (int)totals[key]!, "Native tracked resource total changed during transfer");
    }
    void Scenario(string name, int[] sources, int[] targets, int capacity, int pulses, int[] expectedTargets, string pumpTemplate = "CellTransfer")
    {
        var circuit = CircuitOf(sources, targets, capacity, pumpTemplate: pumpTemplate);
        for (int i = 0; i < pulses; i++) CheckedPulse(circuit);
        var actual = circuit.Targets.Select(storage => Count(storage, "Resources")).ToArray();
        Require(actual.SequenceEqual(expectedTargets), $"Scenario mismatch: {name}; actual=[{string.Join(',', actual)}]");
        scenarioResults.Add(new { name, sources = circuit.Sources.Select(storage => Count(storage, "Resources")).ToArray(), targets = actual, pulses });
        Console.WriteLine($"PASS: {name}; targets=[{string.Join(',', actual)}]");
    }
    void CapacitorIsolation()
    {
        var reactor = Storage("SourceStorage", 8000, 1000);
        var caps = new[] { Storage("ReceiverStorage", 1000, 0), Storage("ReceiverStorage", 1000, 0) };
        var consumer = Storage("ReceiverStorage", 1000, 1000);
        var biases = new List<object>();
        var stores = new List<object> { Store("ReactorStore", Inline("SourceCells", reactor)) };
        foreach (var cap in caps) {
            stores.Add(Store("CapacitorSupplyStore", Inline("SourceCells", cap)));
            stores.Add(Store("CapacitorReceiveStore", Receiver(cap, out var bias)));
            biases.Add(bias);
        }
        stores.Add(Store("ConsumerReceiveStore", Receiver(consumer, out var consumerBias)));
        var allStores = stores.ToArray();
        var reactorOutput = NetworkIo("ReactorOutput", true, allStores, allowFiltered: true);
        var capOutput = NetworkIo("CapacitorOutput", true, allStores, allowFiltered: true);
        var capInput = NetworkIo("CapacitorInput", false, allStores, allowFiltered: true);
        var consumerInput = NetworkIo("ConsumerInput", false, allStores, allowFiltered: true);
        Require(Count(Field(reactorOutput.GetType(), "_subnetworkSources").GetValue(reactorOutput)!, "DataCount") == 1, "Charging must draw from the reactor only");
        Require(Count(Field(capOutput.GetType(), "_subnetworkSources").GetValue(capOutput)!, "DataCount") == 2, "Consumer supply must see both capacitors");
        Require(Count(Field(capInput.GetType(), "_subnetworkSinks").GetValue(capInput)!, "DataCount") == 2, "Charging must target both capacitors only");
        Require(Count(Field(consumerInput.GetType(), "_subnetworkSinks").GetValue(consumerInput)!, "DataCount") == 1, "Capacitor supply must target the consumer only");
        var charging = new Circuit(new[] { reactor }, caps, biases.ToArray(), Pump(reactorOutput, capInput));
        var supplying = new Circuit(caps, new[] { consumer }, new[] { consumerBias }, Pump(capOutput, consumerInput));
        CheckedPulse(charging);
        Require(Count(caps[0], "Resources") == 1000 && Count(caps[1], "Resources") == 0, "Initial native capacitor charging mismatch");
        for (int i = 0; i < 100; i++) {
            CheckedPulse(charging);
            CheckedPulse(supplying);
            Require(Count(caps[0], "Resources") == 1000 && Count(caps[1], "Resources") == 0, "Energy moved between idle capacitors");
        }
        SetCount(reactor, 1000);
        CheckedPulse(charging);
        Require(caps.All(cap => Count(cap, "Resources") == 1000), "Next reactor cell must fill the other capacitor");
        Add(consumer, -1000);
        CheckedPulse(supplying);
        Require(Count(consumer, "Resources") == 1000 && Count(caps[0], "Resources") == 0 && Count(caps[1], "Resources") == 1000, "A whole capacitor cell must reach only the consumer");
        scenarioResults.Add(new { name = "two capacitor isolation", idleCycles = 100, idleCapacitors = new[] { 1000, 0 }, finalCapacitors = caps.Select(cap => Count(cap, "Resources")).ToArray(), finalConsumer = Count(consumer, "Resources") });
        Console.WriteLine("PASS: native category filters isolate two capacitors; 100 idle cycles preserve [1000,0]; later supply targets consumer only.");
    }
    void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    public void Run()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; actual assembly: {game.GetName().Name}");
        var source = Storage("SourceStorage", 8000, 1500);
        var sourceCells = Inline("SourceCells", source);
        Require(Count(sourceCells, "Resources") == 1, "Source must expose one complete cell");
        Require(Count(sourceCells, "ResourcesReadyToUse") == 1, "Native ready count must expose one complete cell");
        Add(sourceCells, -1);
        Require(Count(source, "Resources") == 500, "Native source conversion must retain its fractional remainder");

        var target = Storage("ReceiverStorage", 4000, 3500);
        var naive = Inline("SourceCells", target);
        Require(Count(naive, "MaxResources") - Count(naive, "Resources") == 1, "Negative control must expose the naive false slot");
        var safe = Receiver(target, out var bias);
        Require(Count(safe, "MaxResources") - Count(safe, "Resources") == 0, "Biased receiver must reject half-cell free capacity");
        SetCount(target, 2500);
        Require(Count(safe, "MaxResources") - Count(safe, "Resources") == 1, "Receiver must expose exactly one full free slot");
        Add(safe, 1);
        Require(Count(target, "Resources") == 3500, "Native receiver conversion must insert exactly one full cell");
        Require(Count(bias, "Resources") == 999, "Fake bias must stay unchanged");
        Console.WriteLine($"PASS: initial actual-native component checks={checks}; parsed .rules; source remainder and receiver rounding verified.");

        for (int amount = 0; amount <= 4000; amount++) {
            SetCount(target, amount);
            Require(Count(safe, "MaxResources") - Count(safe, "Resources") == (4000 - amount) / 1000, "Receiver full-slot count is incorrect");
            Require(Count(bias, "Resources") == 999, "Bias changed during receiver boundary sweep");
        }
        for (int amount = 0; amount <= 8000; amount++) {
            SetCount(source, amount);
            Require(Count(sourceCells, "ResourcesReadyToUse") == amount / 1000, "Source complete-cell count is incorrect");
        }
        Console.WriteLine("PASS: exhaustive 4001 receiver levels and 8001 source levels.");

        var control = CircuitOf(new[] { 1000 }, new[] { 3500 }, naive: true);
        int controlBefore = Energy(control);
        Pulse(control.Pump);
        Require(controlBefore - Energy(control) == 500, "Negative control must reproduce 500-unit overflow loss");
        Console.WriteLine("PASS: negative control reproduced naive-wrapper energy loss of 500; safe composition is required.");
        Scenario("half-cell deficit waits", new[] { 1500 }, new[] { 3500 }, 4000, 4, new[] { 3500 });
        Scenario("whole-cell deficit receives exactly one", new[] { 1500 }, new[] { 3000 }, 4000, 4, new[] { 4000 });
        Scenario("two half-cell deficits do not combine", new[] { 1000 }, new[] { 3500, 3500 }, 4000, 4, new[] { 3500, 3500 });
        Scenario("two half-cell sources do not combine", new[] { 500, 500 }, new[] { 0 }, 4000, 4, new[] { 0 });
        Scenario("one cell and five empty receivers", new[] { 1000 }, new int[5], 1000, 8, new[] { 1000, 0, 0, 0, 0 });
        Scenario("five cells and ten empty receivers", new[] { 5000 }, new int[10], 1000, 15, new[] { 1000, 1000, 1000, 1000, 1000, 0, 0, 0, 0, 0 });
        Scenario("partial destination remainder is retained", new[] { 2500 }, new[] { 2500 }, 4000, 6, new[] { 3500 });
        Scenario("fractional source excluded from IO", new[] { 1500, 2500, 999 }, new[] { 3999, 3000, 2000 }, 4000, 8, new[] { 3999, 4000, 4000 });
        Scenario("three whole cells in one native allocation", new[] { 3000 }, new[] { 0, 0 }, 4000, 1, new[] { 2000, 1000 }, "CellTransfer3");
        Scenario("five whole cells in one native allocation", new[] { 5000 }, new[] { 0, 0 }, 4000, 1, new[] { 3000, 2000 }, "CellTransfer5");
        Scenario("three separate one-cell allocations", new[] { 3000 }, new[] { 0, 0 }, 4000, 3, new[] { 3000, 0 });

        int boundaryCases = 0;
        foreach (int sourceAmount in new[] { 0, 1, 499, 500, 999, 1000, 1001, 1500, 1999, 2000, 7999 }) {
            foreach (int targetAmount in new[] { 0, 1, 999, 1000, 1001, 2000, 2499, 2500, 2999, 3000, 3001, 3500, 3999, 4000 }) {
                var circuit = CircuitOf(new[] { sourceAmount }, new[] { targetAmount });
                CheckedPulse(circuit);
                int expectedMove = sourceAmount >= 1000 && 4000 - targetAmount >= 1000 ? 1000 : 0;
                Require(Count(circuit.Sources[0], "Resources") == sourceAmount - expectedMove, "Source boundary matrix mismatch");
                Require(Count(circuit.Targets[0], "Resources") == targetAmount + expectedMove, "Receiver boundary matrix mismatch");
                boundaryCases++;
            }
        }
        scenarioResults.Add(new { name = "one-cell IO boundary matrix", cases = boundaryCases });
        Console.WriteLine($"PASS: native one-cell IO boundary matrix cases={boundaryCases}.");

        var random = new Random(20261025);
        var fuzz = CircuitOf(new[] { 7999, 3333, 500 }, new[] { 3999, 2999, 0, 1500, 4000 });
        for (int tick = 0; tick < 3000; tick++) {
            var sourceStorage = fuzz.Sources[random.Next(fuzz.Sources.Length)];
            Add(sourceStorage, random.Next(1001));
            var targetStorage = fuzz.Targets[random.Next(fuzz.Targets.Length)];
            Add(targetStorage, -random.Next(1001));
            CheckedPulse(fuzz);
        }
        scenarioResults.Add(new { name = "simulated fractional storage changes", ticks = 3000, finalEnergy = Energy(fuzz) });
        Console.WriteLine("PASS: 3000 native IO pulses with simulated fractional stock changes through native storage APIs.");
        CapacitorIsolation();
        SharedPhysicalPorts();
        ValidateModPackage();
        var loadedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(game.Location)));
        var installedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(gameRoot, "Bin", "Cosmoteer.dll"))));
        Require(loadedHash == installedHash, "Loaded game assembly differs from installed source assembly");
        var resultPath = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "result.json");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new {
            verifiedAtUtc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription,
            gameAssemblySha256 = installedHash,
            loadedAssemblySha256 = loadedHash,
            fixture = "native .rules parser, storage/IO/converter/distributor; whole-cell cases use seeded membership; shared-port cases use native physical port discovery, topology and queries with synthetic ownership",
            checks,
            scenarios = scenarioResults,
            limitations = new[] { "synthetic ownership and media-free physical storage fixtures with original IDs/capacities; no live world or rendering", "native topology discovery covered by shared-port, voltage and source/capacitor fixtures; exhaustive IO cases retain seeded membership", "native DebugToggle models operational/overclock inputs; actual overclock controller and UI not exercised", "no automatic simulation scheduling", "no full construction/destruction/ship-split lifecycle, save/load or multiplayer", "capacitor unscaled overclock proxy and separate electrical-delivery heat tested; full crew pickup/delivery lifecycle not exercised" }
        }, IndentedJson));
        Console.WriteLine($"PASS: all checks={checks}; evidence={resultPath}");
    }
}
