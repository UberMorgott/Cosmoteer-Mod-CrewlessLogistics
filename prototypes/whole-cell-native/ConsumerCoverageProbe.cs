using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;

sealed partial class NativeProbe
{
    void ValidateConsumerCoverage(object root, bool modded = false, object? baseline = null)
    {
        using var mapping = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "docs", "consumer-coverage.json")));
        var parts = mapping.RootElement.GetProperty(modded ? "moddedParts" : "parts").EnumerateArray().ToArray();
        Require(parts.Length == (modded ? mapping.RootElement.GetProperty("moddedConsumerCount").GetInt32() : 47), "Consumer count differs from generated native catalog");
        var vanilla = baseline ?? Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(gameRoot, "Data", "cosmoteer.rules") })!;
        var reports = new List<object>();
        var capacityClasses = new HashSet<int>();
        int socketCount = 0, newSocketCount = 0, parsed = 0, forbiddenEdgesRejected = 0;
        foreach (var entry in parts) {
            string name = entry.GetProperty("part").GetString()!;
            string prefix = entry.GetProperty("path").GetString()! + "/Components/";
            var componentDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
            var componentRules = (IDictionary)ReadRuleObject(root, prefix.TrimEnd('/'), componentDictionaryType);
            Require(!componentRules.Keys.Cast<string>().Any(id => id.StartsWith("CrewlessLogisticsSocket", StringComparison.Ordinal)), "Persistent electrical socket artwork remains outside cable placement: " + name);
            string backing = entry.GetProperty("backingStorage").GetString()!;
            string receiving = entry.GetProperty("receiverStorage").GetString()!;
            var sourcePath = entry.GetProperty("sourcePath").GetString()!;
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))) == entry.GetProperty("sourceSha256").GetString(), "Vanilla source changed after consumer generation: " + name);
            Require(((string[])ReadRuleObject(vanilla, entry.GetProperty("path").GetString()! + "/TypeCategories", typeof(string[]))).Contains("uses_power"), "Consumer is not electrical equipment: " + name);
            CompareCatalogGeometry(entry, vanilla);
            forbiddenEdgesRejected += ValidateNativePerimeterGeometry(entry);
            object Read(string component) { parsed++; return Rules(prefix + component, "Cosmoteer.Ships.Parts.PartComponentRules", root); }
            // Native vanilla storage media rules construct audio/graphics factories.
            // Clone only the measured physical capacity/ID into a native storage
            // fixture; energy accounting still runs actual game storage methods.
            Require((string)ReadRuleObject(root, prefix + backing + "/Type", typeof(string)) == "ResourceStorage", "Endpoint is not a physical battery: " + name);
            int capacity = (int)ReadRuleObject(root, prefix + backing + "/MaxResources", typeof(int));
            var physicalRules = Rules("ReceiverStorage", "Cosmoteer.Ships.Parts.Resources.ResourceStorageRules");
            SetField(physicalRules, "MaxResources", capacity);
            SetField(physicalRules, "ID", Id(Field(physicalRules.GetType(), "ID").FieldType, backing));
            Require(capacity >= 1000 && capacity % 1000 == 0, "Consumer capacity cannot receive exact cells: " + name);
            Require(capacity == (int)ReadRuleObject(vanilla, prefix + backing + "/MaxResources", typeof(int)), "Native battery capacity was changed: " + name);
            Require((string)ReadRuleObject(root, prefix + backing + "/ResourceType", typeof(string)) == "battery", "Physical battery type was changed: " + name);
            var toggleRules = Read("CrewlessLogisticsToggle");
            Require((int)Field(toggleRules.GetType(), "Default").GetValue(toggleRules)! == 0 && (bool)Field(toggleRules.GetType(), "RememberValue").GetValue(toggleRules)!, "Consumer network switch defaults/persistence changed: " + name);
            var aggregateRules = Read("CrewlessLogisticsReceivingAggregate");
            var ids = (Array)Field(aggregateRules.GetType(), "ResourceStorages").GetValue(aggregateRules)!;
            Require(ids.Length == 2 && ids.GetValue(0)!.ToString() == receiving && ids.GetValue(1)!.ToString() == "CrewlessLogisticsReceivingBias", "Consumer adapter does not target its own intended receiver: " + name);
            var cellRules = Read("CrewlessLogisticsCells");
            Require(Field(cellRules.GetType(), "FromStorage").GetValue(cellRules)!.ToString() == "CrewlessLogisticsReceivingAggregate", "Consumer cell view lost rounding adapter: " + name);
            var receiverRules = Read("CrewlessLogisticsReceiver");
            Require(!(bool)Field(receiverRules.GetType(), "AllowProviding").GetValue(receiverRules)! && (bool)Field(receiverRules.GetType(), "AllowReceiving").GetValue(receiverRules)!, "Consumer became an electrical source: " + name);
            Require(Field(receiverRules.GetType(), "OperationalToggle").GetValue(receiverRules)!.ToString() == "CrewlessLogisticsToggle", "Consumer receiver is not gated: " + name);
            Read("CrewlessLogisticsBlueprintNode");
            foreach (var socket in entry.GetProperty("ports").EnumerateArray()) {
                string component = socket.GetProperty("component").GetString()!;
                var port = Read(component);
                socketCount++;
                Require(Field(port.GetType(), "OperationalToggle").GetValue(port)!.ToString() == "CrewlessLogisticsToggle" && Field(port.GetType(), "BlueprintOperationalToggle").GetValue(port)!.ToString() == "CrewlessLogisticsToggle", "Consumer socket is not gated: " + name);
                Require(Property(port, "RouteEndpoint")!.ToString() == "CrewlessLogisticsReceiver", "Consumer socket endpoint is wrong: " + name);
                Require(((Array)Property(port, "Types")).Cast<object>().Select(type => type.ToString()).SequenceEqual(new[] { "morgott.crewless_logistics.electric" }), "Consumer socket retained thermal type: " + name);
                CheckElectricOverlay(port);
                Require(socket.GetProperty("graphics").ValueKind == JsonValueKind.Null, "Consumer mapping still assigns permanent socket artwork: " + name);
                if (socket.GetProperty("thermalComponent").ValueKind == JsonValueKind.String) ComparePorts(root, prefix, component, socket.GetProperty("thermalComponent").GetString()!);
                else {
                    newSocketCount++;
                    Require(((int[])ReadRuleObject(root, prefix + component + "/Location", typeof(int[]))).SequenceEqual(socket.GetProperty("location").EnumerateArray().Select(x => x.GetInt32())), "New socket location drifted: " + name);
                    Require((string)ReadRuleObject(root, prefix + component + "/Direction", typeof(string)) == socket.GetProperty("direction").GetString(), "New socket direction drifted: " + name);
                }
            }
            // Preserve original door metadata exactly, including null/all-edge defaults.
            foreach (string field in new[] { "AllowedDoorLocations", "BlockedTravelCells" }) {
                int[][]? Original(object source) { try { return (int[][])ReadRuleObject(source, entry.GetProperty("path").GetString()! + "/" + field, typeof(int[][])); } catch (System.Reflection.TargetInvocationException) { return null; } }
                Require(JsonSerializer.Serialize(Original(root)) == JsonSerializer.Serialize(Original(vanilla)), "Original crew access changed: " + name);
            }

            // Use the actual package adapter over native storage with the exact
            // measured vanilla ID/capacity; rendering/crew media are excluded.
            NewTopologyPart(0, 0, 0);
            var battery = Component(physicalRules);
            SetCount(battery, capacity - 999);
            object receiverBacking = battery;
            object? boostProxy = null;
            if (name == "thruster_boost") {
                var proxyRules = Read("BatteryStorage");
                Require((string)ReadRuleObject(root, prefix + "BatteryStorage/ComponentID", typeof(string)) == "RealBatteryStorage" && (string)ReadRuleObject(root, prefix + "BatteryStorage/ProxyToggle", typeof(string)) == "CanReceivePower", "Boost charging bypasses the native boost lockout");
                boostProxy = Component(proxyRules);
                ConnectConsumerProxy(boostProxy, battery);
                receiverBacking = boostProxy;
            }
            componentMap[ids.GetValue(0)!] = receiverBacking;
            var bias = Component(Read("CrewlessLogisticsReceivingBias"));
            var aggregate = Component(aggregateRules);
            InitializeLinks(aggregate);
            var cells = Component(cellRules);
            InitializeLinks(cells);
            var source = Storage("SourceStorage", 8000, 1500);
            var sourceStore = Store("SourceStore", Inline("SourceCells", source));
            var receiverStore = Component(receiverRules);
            InitializeLinks(receiverStore);
            Call(receiverStore, "OnNetworkRegistered");
            var nativeInputRules = Rules("<ships/terran/reactor_small/reactor_small.rules>/Part/Components/CrewlessLogisticsInput", "Cosmoteer.Ships.Networks.PartNetworkResourceInputRules", root);
            var circuit = new Circuit(new[] { source }, new[] { battery }, new[] { bias }, Pump(NetworkIo("NetworkOutput", true, new[] { sourceStore }), ConsumerCoverageInput(nativeInputRules, receiverStore)));
            CheckedPulse(circuit);
            Require(Count(battery, "Resources") == capacity - 999 && Count(source, "Resources") == 1500, "Consumer accepted a fractional-cell refill: " + name);
            Add(battery, -1);
            CheckedPulse(circuit);
            Require(Count(battery, "Resources") == capacity && Count(source, "Resources") == 500, "Consumer did not receive exactly one complete cell: " + name);
            if (boostProxy != null) {
                Add(battery, -1000);
                Add(source, 1000);
                ConnectConsumerProxy(boostProxy, null);
                CheckedPulse(circuit);
                Require(Count(battery, "Resources") == capacity - 1000 && Count(source, "Resources") == 1500, "Closed boost proxy accepted charging");
                ConnectConsumerProxy(boostProxy, battery);
                CheckedPulse(circuit);
                Require(Count(battery, "Resources") == capacity && Count(source, "Resources") == 500, "Boost proxy failed to resume charging");
            }
            if (name == "engine_room") {
                Require((string)ReadRuleObject(root, prefix + "PowerDrain/FromStorage", typeof(string)) == backing, "Engine room does not consume its mapped local battery");
                Require((string)ReadRuleObject(root, prefix + "BatteryConsumer/Storage", typeof(string)) == "BatteryDistributionStorage", "Native engine-room crew distribution changed");
                var drain = Component(Read("PowerDrain"));
                ((Array)Field(drain.GetType(), "_fromStorages").GetValue(drain)!).SetValue(battery, 0);
                int before = Count(battery, "Resources");
                Pulse(drain);
                Require(Count(battery, "Resources") == before - 100, "Actual native engine-room drain did not consume network-filled battery");
            }
            if (name.StartsWith("thruster_rocket_")) ValidateRocketLocalDraw(root, prefix, name, battery);
            capacityClasses.Add(capacity);
            reports.Add(new { part = name, capacity, backing, receiving, sockets = entry.GetProperty("ports").GetArrayLength(), exactCellTransfer = true });
        }
        var report = Path.Combine(projectRoot, "artifacts", "whole-cell-native", modded ? "modded-device-validation.json" : "consumer-coverage-validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, consumers = reports, socketCount, newSocketCount, forbiddenEdgesRejected, parsedComponentRules = parsed, capacityClasses = capacityClasses.Order().ToArray(), limits = "Actual package adapter rules and native storage/IO methods. Physical battery ID/capacity and perimeter/prohibition geometry are cloned into native fixture objects to avoid media factories; proxy membership is injected. Full ship construction, custom collider/build restrictions, neighboring rocket buff discovery, crew/media hooks, GUI rendering, operational scheduling and multiplayer are not exercised." }, new JsonSerializerOptions { WriteIndented = true }));
        scenarioResults.Add(new { name = modded ? "enabled-mod electrical consumers" : "all vanilla electrical consumers", consumerCount = parts.Length, socketCount, newSocketCount, forbiddenEdgesRejected, parsedComponentRules = parsed });
        Console.WriteLine($"PASS: {(modded ? "enabled-mod" : "vanilla")} consumers={parts.Length}; sockets={socketCount}; forbidden edges rejected={forbiddenEdgesRejected}; exact cells and preserved native stores/doors; evidence={report}");
    }

    void ConnectConsumerProxy(object proxy, object? storage)
    {
        var handler = Field(proxy.GetType(), "_proxy").GetValue(proxy)!;
        if (storage != null) SetField(handler, "_proxiedPart", part);
        handler.GetType().GetProperty("ProxiedComponent", All)!.SetValue(handler, storage);
    }

    object ConsumerCoverageInput(object rules, object store)
    {
        // Seed membership only; native package query/category rules and native IO
        // perform matching, available-capacity tracking, and whole-cell delivery.
        var io = Component(rules);
        const string prefix = "Cosmoteer.Ships.Networks.Queries.";
        var arguments = new[] { Game("Cosmoteer.Ships.Parts.Part"), Game("Cosmoteer.Ships.Parts.PartComponent") };
        var cacheType = Game(prefix + "SubnetworkQueryCache`2").MakeGenericType(arguments);
        var cache = Activator.CreateInstance(cacheType, All, null, new object?[] { null }, null)!;
        var query = Activator.CreateInstance(Game(prefix + "PartNetworkResourceQuery"), All, null, new[] { Field(rules.GetType(), "ResourceType").GetValue(rules), Field(rules.GetType(), "NetworkFilter").GetValue(rules) }, null)!;
        var resultType = Game(prefix + "SubnetworkResourceSinkQueryResult`2").MakeGenericType(arguments);
        var result = Activator.CreateInstance(resultType, All, null, new[] { cache, query }, null)!;
        Require((bool)Call(result, "MatchesCriteria", store)!, "Actual reactor consumer query rejects actual package receiver");
        var data = (IList)Field(resultType, "_data").GetValue(result)!;
        var indices = (IDictionary)Field(resultType, "_dataIndices").GetValue(result)!;
        indices.Add(store, data.Count);
        data.Add(Call(result, "OnAddition", store));
        var queriesField = Field(cacheType, "_resourceSinkQueries");
        var queries = (IDictionary)Activator.CreateInstance(queriesField.FieldType)!;
        queries.Add(query, result);
        queriesField.SetValue(cache, queries);
        SetField(io, "_subnetworkSinks", result);
        return io;
    }

    void ValidateRocketLocalDraw(object root, string prefix, string name, object battery)
    {
        object Read(string component) => Rules(prefix + component, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        // Reproduce native aggregate links without neighboring ship/buff discovery.
        foreach (string side in name == "thruster_rocket_battery" ? new[] { "LeftExtendableBatteryStorage", "RightExtendableBatteryStorage" } : new[] { "LeftBatteryStorage", "RightBatteryStorage" }) Component(Read(side));
        var extended = Component(Read("ExtendableBatteryStorage"));
        InitializeLinks(extended);
        object draw = extended;
        if (name != "thruster_rocket_battery") {
            var localProxy = Component(Read("ToggledExtendableBatteryStorage"));
            ConnectConsumerProxy(localProxy, extended);
            Component(Read("PrevCombinedBatteryStorage"));
            draw = Component(Read("CombinedBatteryStorage"));
            InitializeLinks(draw);
        }
        if (name == "thruster_rocket_nozzle") Require((string)ReadRuleObject(root, prefix + "MainThruster/FuelStorage", typeof(string)) == "CombinedBatteryStorage", "Rocket nozzle fuel does not reference the native aggregate");
        int before = Count(battery, "Resources");
        Add(draw, -1000);
        Require(Count(battery, "Resources") == before - 1000, "Native rocket draw does not consume mapped local battery: " + name);
    }
}
