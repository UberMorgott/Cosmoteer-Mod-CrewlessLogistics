using System.Collections;

sealed partial class NativeProbe
{
    // Native graph discovery, category queries, switches and resource operations.
    // Only the converter schedule is stepped by the fixture; no game world opens.
    void SourceStoragePackageProbe(object root)
    {
        const string reactor = "<ships/terran/reactor_small/reactor_small.rules>/Part/Components/";
        const string capacitor = "<ships/terran/power_storage/power_storage.rules>/Part/Components/";
        const string consumer = "<ships/terran/laser_blaster_small/laser_blaster_small.rules>/Part/Components/";
        bool legacyCapacitorTerminals = false;
        object? legacyRouterFile = null;
        object Read(string prefix, string name)
        {
            if (legacyCapacitorTerminals && prefix == capacitor && name == "CrewlessLogisticsSourceRouter") return Rules("LegacyRouter", "Cosmoteer.Ships.Parts.PartComponentRules", legacyRouterFile!);
            var rules = Rules(prefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", root);
            if (legacyCapacitorTerminals && prefix == capacitor && name.StartsWith("CrewlessLogisticsPort")) {
                var source = rules.GetType().GetProperty("RouteSource", All)!;
                var endpoint = rules.GetType().GetProperty("RouteEndpoint", All)!;
                source.SetValue(rules, Id(Nullable.GetUnderlyingType(source.PropertyType)!, "CrewlessLogisticsInput"));
                endpoint.SetValue(rules, Id(Nullable.GetUnderlyingType(endpoint.PropertyType)!, "CrewlessLogisticsSource"));
            }
            return rules;
        }
        string TopLeft = Socket("Left", 0, 0), TopRight = Socket("Right", 1, 0);
        var parsed = new List<string>();
        foreach (var entry in new[] { ("reactor_small", 2), ("reactor_med", 3), ("reactor_large", 4) }) {
            string prefix = $"<ships/terran/{entry.Item1}/{entry.Item1}.rules>/Part/Components/";
            foreach (string name in new[] { "CrewlessLogisticsToggle", "CrewlessLogisticsCanTransfer", "CrewlessLogisticsCells", "CrewlessLogisticsInput", "CrewlessLogisticsTransfer", "CrewlessLogisticsSource", "CrewlessLogisticsSourceRouter", "CrewlessLogisticsBlueprintNode", "CrewlessLogisticsNode" }) {
                Read(prefix, name); parsed.Add(prefix + name);
            }
            Require(!(bool)Field(Read(prefix, "CrewlessLogisticsSource").GetType(), "AllowReceiving").GetValue(Read(prefix, "CrewlessLogisticsSource"))!, "Reactor may not receive cells from other reactors or capacitors");
            Require((string)ReadRuleObject(root, prefix + "BatteryProducer/ToStorage", typeof(string)) == "BatteryStorage", "Reactor vanilla generation changed: " + entry.Item1);
            foreach (string socket in PerimeterSockets(entry.Item2)) {
                var port = Read(prefix, socket); parsed.Add(prefix + socket);
                Require(Field(port.GetType(), "OperationalToggle").GetValue(port)?.ToString() == "CrewlessLogisticsToggle", "Reactor socket ignores network switch");
                Require(Property(port, "RouteSource")?.ToString() == "CrewlessLogisticsNode" && Property(port, "RouteEndpoint")?.ToString() == "CrewlessLogisticsNode", "Reactor sockets must share a bidirectional native electrical bus");
            }
        }
        foreach (string name in new[] { "CrewlessLogisticsToggle", "CrewlessLogisticsCanTransfer", "CrewlessLogisticsCells", "CrewlessLogisticsReceivingBias", "CrewlessLogisticsReceivingAggregate", "CrewlessLogisticsReceivingCells", "CrewlessLogisticsInput", "CrewlessLogisticsTransfer", "CrewlessLogisticsSource", "CrewlessLogisticsReactorOutput", "CrewlessLogisticsConsumersSatisfied", "CrewlessLogisticsCanCharge", "CrewlessLogisticsNoReactorCells", "CrewlessLogisticsCanSupply", "CrewlessLogisticsCharge", "CrewlessLogisticsDeliveryTrigger", "CrewlessLogisticsDeliveryHeat", "CrewlessLogisticsBlueprintNode", "CrewlessLogisticsNode", "CrewlessLogisticsSourceRouter" }) {
            Read(capacitor, name); parsed.Add(capacitor + name);
        }
        foreach (string name in SmallSockets.Select(s => s.Electric)) {
            var rules = Read(capacitor, name); parsed.Add(capacitor + name);
            Require(Property(rules, "RouteSource")?.ToString() == "CrewlessLogisticsNode" && Property(rules, "RouteEndpoint")?.ToString() == "CrewlessLogisticsNode", "Capacitor must join every electrical socket to its native bus");
        }
        Require((string)ReadRuleObject(root, capacitor + "CrewlessLogisticsCells/FromStorage", typeof(string)) == "BatteryReceiverStorage", "Capacitor source must use the unscaled receiver without crew pickup proxies");
        Require(((string[])ReadRuleObject(root, capacitor + "CrewlessLogisticsReceivingAggregate/ResourceStorages", typeof(string[]))).SequenceEqual(new[] { "BatteryReceiverStorage", "CrewlessLogisticsReceivingBias" }), "Capacitor receiver must use physical reserve plus rounding bias only");
        Require((string)ReadRuleObject(root, capacitor + "BatteryConsumer/Storage", typeof(string)) == "BatteryReceiverStorage", "Vanilla crew receiver changed");
        Require((string)ReadRuleObject(root, capacitor + "Overclock_PowerDeliveredTrigger/TriggerID", typeof(string)) == "CrewResourcesReceived", "Vanilla receiving trigger changed");

        var manager = NewTopologyManager();
        var storageFixture = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "prototypes", "whole-cell-native", "source-storage.rules") })!;
        object Make(string prefix, string name, bool nativeLifecycle = true)
        {
            var component = Component(Read(prefix, name));
            if (Game("Cosmoteer.Ships.Parts.Resources.BaseResourceStorage").IsInstanceOfType(component)) {
                InitializeLinks(component);
                if (Game("Cosmoteer.Ships.Networks.BasePartNetworkResourceStorage").IsInstanceOfType(component)) RegisterNode(manager, component);
            } else if (Game("Cosmoteer.Ships.Networks.PartNetworkBasicNode").IsInstanceOfType(component)) {
                RegisterNode(manager, component);
            } else if (nativeLifecycle) Call(component, "OnAddedToPart", part);
            else InitializeLinks(component);
            return component;
        }
        object Switch(string prefix)
        {
            var rules = Read(prefix, "CrewlessLogisticsToggle");
            Require((int)Field(rules.GetType(), "Default").GetValue(rules)! == 0, "Storage/source network switch must default off");
            SetField(rules, "RememberValue", false);
            var toggle = Component(rules); Call(toggle, "OnAddedToPart", part);
            return toggle;
        }
        object ExternalToggle(string id)
        {
            var rules = Rules("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
            var idField = Field(rules.GetType(), "ID"); idField.SetValue(rules, Id(idField.FieldType, id));
            var toggle = Component(rules); SetToggle(toggle, true); return toggle;
        }
        object PumpFromPackage(string prefix, string name) => Make(prefix, name, false);
        void Cable(int x, int y)
        {
            NewTopologyPart(x, y, 0); GraphNode(manager, "PortProbeElectricNode");
            foreach (string side in new[] { "Up", "Right", "Down", "Left" }) {
                var rules = Rules("PortProbeCableElectricLeft", "Cosmoteer.Ships.Networks.PartNetworkPortRules");
                var idField = Field(rules.GetType(), "ID"); idField.SetValue(rules, Id(idField.FieldType, "CablePort" + side));
                var direction = Field(rules.GetType(), "Direction"); direction.SetValue(rules, Enum.Parse(direction.FieldType, side));
                var port = Component(rules); Call(port, "OnAddedToPart", part);
            }
        }

        NewTopologyPart(0, 0, 0); SetField(Property(part, "Rules"), "Size", Vector(2, 2));
        var reactorPart = part;
        ExternalToggle("IsOperational"); var reactorSwitch = Switch(reactor);
        var reactorGate = Make(reactor, "CrewlessLogisticsCanTransfer");
        var source = Storage("SourceStorage", 8000, 6500);
        componentMap[Field(Read(reactor, "CrewlessLogisticsCells").GetType(), "FromStorage").GetValue(Read(reactor, "CrewlessLogisticsCells"))!] = source;
        Make(reactor, "CrewlessLogisticsCells"); Make(reactor, "CrewlessLogisticsInput"); Make(reactor, "CrewlessLogisticsSource"); Make(reactor, "CrewlessLogisticsNode"); Make(reactor, "CrewlessLogisticsSourceRouter");
        var reactorPump = PumpFromPackage(reactor, "CrewlessLogisticsTransfer"); Make(reactor, TopRight);
        Cable(2, 0); Cable(2, -1); Cable(2, 1);

        (object Battery, object Extra, object Overclock, object Bias, object Switch, object Supply, object Charge, object ChargeGate, object Heat, object Source, object SupplyGate) Capacitor(int x, int y, string socket)
        {
            NewTopologyPart(x, y, 0); SetField(Property(part, "Rules"), "Size", Vector(2, 2));
            ExternalToggle("ConsumptionToggle"); var toggle = Switch(capacitor); Make(capacitor, "CrewlessLogisticsCanTransfer");
            var battery = Storage("ReceiverStorage", 2000, 0);
            Require(((string[])ReadRuleObject(root, capacitor + "BatteryReceiverStorage/ResourceStorages", typeof(string[]))).SequenceEqual(new[] { "BatteryStorage", "Overclock_BatteryStorageProxy" }), "Vanilla capacitor receiver composition changed");
            Require((string)ReadRuleObject(root, capacitor + "BatteryReceiverStorage/AddMode", typeof(string)) == "InOrder", "Vanilla capacitor receiver order changed");
            var receiverRules = Rules("CapacitorReceiver", "Cosmoteer.Ships.Parts.Resources.MultiResourceStorageRules", storageFixture);
            var receiverId = Field(receiverRules.GetType(), "ID"); receiverId.SetValue(receiverRules, Id(receiverId.FieldType, "BatteryReceiverStorage"));
            var backingIds = (Array)Field(receiverRules.GetType(), "ResourceStorages").GetValue(receiverRules)!;
            componentMap[backingIds.GetValue(0)!] = battery;
            var extra = Storage("ReceiverStorage", 2000, 0);
            var extraIdField = Field(receiverRules.GetType(), "ID");
            componentMap[Id(extraIdField.FieldType, "Overclock_BatteryStorage")] = extra;
            var overclock = ExternalToggle("IsOverclocked"); SetToggle(overclock, false);
            var proxyRules = Read(capacitor, "Overclock_BatteryStorageProxy");
            Require((float)Field(proxyRules.GetType(), "QuantityScale").GetValue(proxyRules)! == 1f, "Capacitor overflow proxy changes energy scale");
            var proxy = Component(proxyRules); InitializeLinks(proxy); Call(proxy, "OnPartAttached2");
            var nativeReceiver = Component(receiverRules); InitializeLinks(nativeReceiver);
            Make(capacitor, "CrewlessLogisticsCells"); var bias = Make(capacitor, "CrewlessLogisticsReceivingBias");
            Make(capacitor, "CrewlessLogisticsReceivingAggregate"); Make(capacitor, "CrewlessLogisticsReceivingCells");
            Make(capacitor, "CrewlessLogisticsInput"); var sourceStore = Make(capacitor, "CrewlessLogisticsSource"); Make(capacitor, "CrewlessLogisticsReactorOutput");
            Make(capacitor, "CrewlessLogisticsConsumersSatisfied"); var gate = Make(capacitor, "CrewlessLogisticsCanCharge"); Make(capacitor, "CrewlessLogisticsNoReactorCells"); var supplyGate = Make(capacitor, "CrewlessLogisticsCanSupply"); Make(capacitor, "CrewlessLogisticsNode"); Make(capacitor, "CrewlessLogisticsSourceRouter");
            var supply = PumpFromPackage(capacitor, "CrewlessLogisticsTransfer"); var charge = PumpFromPackage(capacitor, "CrewlessLogisticsCharge");
            var heat = Storage("PortProbeHeatStorage", 1000000, 0);
            var heatRules = Read(capacitor, "CrewlessLogisticsDeliveryHeat");
            componentMap[Field(heatRules.GetType(), "ResourceStorage").GetValue(heatRules)!] = heat;
            Make(capacitor, "CrewlessLogisticsDeliveryTrigger"); Make(capacitor, "CrewlessLogisticsDeliveryHeat");
            Make(capacitor, socket);
            return (battery, extra, overclock, bias, toggle, supply, charge, gate, heat, sourceStore, supplyGate);
        }
        var capA = Capacitor(3, 0, TopLeft);
        var capB = Capacitor(1, -3, Socket("Down", 1, 1));
        NewTopologyPart(2, 2, 0); var consumerSwitch = Switch(consumer);
        var target = Storage("ReceiverStorage", 2000, 0);
        var aggregateRules = Read(consumer, "CrewlessLogisticsReceivingAggregate");
        var receiverIds = (Array)Field(aggregateRules.GetType(), "ResourceStorages").GetValue(aggregateRules)!;
        componentMap[receiverIds.GetValue(0)!] = target;
        var targetBias = Make(consumer, "CrewlessLogisticsReceivingBias"); Make(consumer, "CrewlessLogisticsReceivingAggregate"); Make(consumer, "CrewlessLogisticsCells"); Make(consumer, "CrewlessLogisticsReceiver");
        var targetPortRules = Rules("PortProbeTargetElectric", "Cosmoteer.Ships.Networks.PartNetworkPortRules");
        SetField(targetPortRules, "Direction", Enum.Parse(Field(targetPortRules.GetType(), "Direction").FieldType, "Up"));
        SetField(targetPortRules, "OperationalToggle", Field(Read(consumer, "CrewlessLogisticsReceiver").GetType(), "OperationalToggle").GetValue(Read(consumer, "CrewlessLogisticsReceiver")));
        var endpoint = targetPortRules.GetType().GetProperty("RouteEndpoint", All)!; endpoint.SetValue(targetPortRules, Id(Nullable.GetUnderlyingType(endpoint.PropertyType)!, "CrewlessLogisticsReceiver"));
        var targetPort = Component(targetPortRules); Call(targetPort, "OnAddedToPart", part);

        var stocks = new List<object> { source, target, capA.Battery, capB.Battery, capA.Extra, capB.Extra };
        var biases = new List<object> { targetBias, capA.Bias, capB.Bias };
        // Supply pumps run only while their native operational gate is on.
        var gates = new Dictionary<object, object>(ReferenceEqualityComparer.Instance) { [reactorPump] = reactorGate, [capA.Supply] = capA.SupplyGate, [capB.Supply] = capB.SupplyGate };
        void Checked(object pump)
        {
            var before = stocks.Select(s => Count(s, "Resources")).ToArray(); if (!gates.TryGetValue(pump, out var gate) || Flag(gate, "IsToggleOn")) Pulse(pump);
            Require(stocks.Sum(s => Count(s, "Resources")) == before.Sum(), "Source/capacitor network created or lost energy");
            for (int i = 0; i < stocks.Count; i++) Require((Count(stocks[i], "Resources") - before[i]) % 1000 == 0, "Source/capacitor network split a cell");
            Require(biases.All(b => Count(b, "Resources") == 999), "Source/capacitor network consumed rounding bias");
        }
        foreach (var toggle in new[] { reactorSwitch, consumerSwitch, capA.Switch, capB.Switch }) SetProperty(toggle, "Mode", 1);
        Require(!Flag(capA.ChargeGate, "IsToggleOn") && !Flag(capB.ChargeGate, "IsToggleOn"), "Capacitors attempted charging before consumers were full");
        Checked(reactorPump); Checked(reactorPump);
        Require(Count(target, "Resources") == 2000 && Count(capA.Battery, "Resources") == 0 && Count(capB.Battery, "Resources") == 0, "Consumer priority failed");
        Require(Flag(capA.ChargeGate, "IsToggleOn") && Flag(capB.ChargeGate, "IsToggleOn"), "Full consumers did not enable capacitor charging through native query events");
        Checked(capA.Charge); Checked(capB.Charge); Checked(capA.Charge); Checked(capB.Charge);
        Require(Count(source, "Resources") == 500 && Count(capA.Battery, "Resources") == 2000 && Count(capB.Battery, "Resources") == 2000, "Reactor surplus did not fill capacitor reserves in whole cells");
        Require(Count(capA.Heat, "Resources") == 0 && Count(capB.Heat, "Resources") == 0, "Capacitor network charging generated overclock heat while overclock was off");
        for (int i = 0; i < 20; i++) { Checked(capA.Charge); Checked(capB.Charge); Checked(capA.Supply); Checked(capB.Supply); }
        Require(Count(capA.Battery, "Resources") == 2000 && Count(capB.Battery, "Resources") == 2000, "Capacitor stock circulated with no demand");
        Add(target, -1000);
        Require(!Flag(capA.ChargeGate, "IsToggleOn") && !Flag(capB.ChargeGate, "IsToggleOn"), "Consumer deficit failed to stop capacitor charging");
        // Reactor-first: a complete reactor cell suppresses capacitor supply, so
        // full reactors never idle their generation while capacitors drain.
        Add(source, 1000);
        Require(Flag(reactorGate, "IsToggleOn") && !Flag(capA.SupplyGate, "IsToggleOn") && !Flag(capB.SupplyGate, "IsToggleOn"), "Capacitor supply was not suppressed by a reactor holding a complete cell");
        Checked(capA.Supply); Checked(capB.Supply); Checked(reactorPump);
        Require(Count(source, "Resources") == 500 && Count(target, "Resources") == 2000 && Count(capA.Battery, "Resources") == 2000, "Reactor did not supply consumers before capacitor reserves");
        Require(Flag(capA.SupplyGate, "IsToggleOn") && Flag(capB.SupplyGate, "IsToggleOn"), "Capacitors did not take over when no reactor held a complete cell");
        Add(target, -1000);
        Checked(capA.Supply);
        Require(Count(target, "Resources") == 2000 && Count(capA.Battery, "Resources") == 1000, "Capacitor failed to supply one complete cell");
        for (int i = 0; i < 20; i++) { Checked(capA.Charge); Checked(capB.Charge); }
        Require(Count(capA.Battery, "Resources") == 1000 && Count(capB.Battery, "Resources") == 2000, "Capacitor-to-capacitor charging occurred");
        SetProperty(capB.Switch, "Mode", 0); Add(target, -1000); Checked(capA.Supply);
        Require(Count(capA.Battery, "Resources") == 0 && Count(capB.Battery, "Resources") == 2000, "Disabled capacitor supplied or lost energy");
        Require(!Flag(capB.ChargeGate, "IsToggleOn"), "Disabled capacitor can still charge");
        // Native unscaled overclock proxy exposes the same building's extra reserve.
        Add(capA.Extra, 1000);
        Require(Count(capA.Source, "AvailableResources") == 0, "Hidden inactive overclock reserve leaked into network");
        SetToggle(capA.Overclock, true);
        Require(Count(capA.Source, "AvailableResources") == 1, "Active capacitor overflow cell was not exposed to cable voltage queries");
        Add(target, -1000);
        Checked(capA.Supply);
        Require(Count(target, "Resources") == 2000 && Count(capA.Extra, "Resources") == 0, "Overclock reserve did not supply a complete cell without scaling");
        Add(source, 3000);
        Checked(capA.Charge); Checked(capA.Charge); Checked(capA.Charge);
        Require(Count(capA.Battery, "Resources") == 2000 && Count(capA.Extra, "Resources") == 1000 && Count(source, "Resources") == 500, "Network did not charge native overclock reserve without energy scaling");
        int heatPerCell = (int)ReadRuleObject(root, "<ships/terran/power_storage/power_storage.rules>/OVERCLOCK/HEAT_PER_BATTERY_DELIVERED", typeof(int)) * (int)ReadRuleObject(root, "<ships/terran/power_storage/power_storage.rules>/HEAT_TO_RESOURCE", typeof(int));
        Require(Count(capA.Heat, "Resources") == heatPerCell * 3, $"Overclock network delivery heat did not match vanilla heat per whole cell: actual={Count(capA.Heat, "Resources")}, expected={heatPerCell * 3}");
        Require(Count(capB.Battery, "Resources") == 2000 && Count(capB.Extra, "Resources") == 0, "Disabled capacitor accepted network charge");
        // A newly connected consumer on the reactor's opposite side is reachable
        // through the native bus. The reactor supplies first; capacitor energy
        // crosses the reactor bus once the reactor has no complete cell.
        part = reactorPart; componentMap = (IDictionary)Field(part.GetType(), "_componentsByID").GetValue(part)!;
        Make(reactor, TopLeft);
        NewTopologyPart(-1, -2, 0); SetField(Property(part, "Rules"), "Size", Vector(1, 3));
        var branchSwitch = Switch(consumer); var branchTarget = Storage("ReceiverStorage", 2000, 0);
        componentMap[receiverIds.GetValue(0)!] = branchTarget;
        var branchBias = Make(consumer, "CrewlessLogisticsReceivingBias"); Make(consumer, "CrewlessLogisticsReceivingAggregate"); Make(consumer, "CrewlessLogisticsCells"); Make(consumer, "CrewlessLogisticsReceiver"); Make(consumer, Socket("Right", 0, 2));
        stocks.Add(branchTarget); biases.Add(branchBias); SetProperty(branchSwitch, "Mode", 1); Add(source, 1000);
        Require(!Flag(capA.SupplyGate, "IsToggleOn"), "Capacitor supplied across the bus while the reactor held a complete cell");
        Checked(capA.Supply); Checked(reactorPump);
        Require(Count(source, "Resources") == 500 && Count(branchTarget, "Resources") == 1000, "Reactor did not supply the reachable branch first");
        Checked(capA.Supply);
        Require(Count(branchTarget, "Resources") == 2000 && Count(source, "Resources") == 500 && Count(capA.Battery, "Resources") + Count(capA.Extra, "Resources") == 2000, "Capacitor did not supply the opposite reactor branch through the bus in exact cells");
        scenarioResults.Add(new { name = "native source/capacitor integration", reactorSources = 3, parsedComponents = parsed.Count, source = Count(source, "Resources"), target = Count(target, "Resources"), capacitors = new[] { Count(capA.Battery, "Resources") + Count(capA.Extra, "Resources"), Count(capB.Battery, "Resources") + Count(capB.Extra, "Resources") }, wholeCellEnergy = stocks.Sum(s => Count(s, "Resources")), priority = "consumers before capacitor charging; reactor supply before capacitor reserve", noCapacitorShuffle = true, nativeOverclockReserve = "unscaled proxy charging and supplying verified", limits = "synthetic ownership; converter ticks stepped, game scheduler/rendering/overclock UI not exercised" });
        Console.WriteLine("PASS: three reactor rule sets, physical capacitor cells, native consumer-first charge gates, reactor-first supply, two capacitors without circulation, conservation and default-off switches.");

        void InlineCapacitors(int count, bool reverse, bool legacyNegativeControl = false)
        {
            legacyCapacitorTerminals = legacyNegativeControl;
            if (legacyNegativeControl) {
                string legacyPath = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "legacy-capacitor-routing-control.rules");
                File.WriteAllText(legacyPath, "LegacyRouter { Type = NetworkRouter; OperationalToggle = CrewlessLogisticsToggle; RouteGenerators [{ Type = Simple; Routes = [[CrewlessLogisticsReactorOutput, CrewlessLogisticsInput, 0], [CrewlessLogisticsInput, CrewlessLogisticsSource, 0]] }] }");
                legacyRouterFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { legacyPath })!;
            }
            manager = NewTopologyManager();
            NewTopologyPart(0, 0, 0); SetField(Property(part, "Rules"), "Size", Vector(2, 2));
            ExternalToggle("IsOperational"); var generatorSwitch = Switch(reactor); var supplyGate = Make(reactor, "CrewlessLogisticsCanTransfer");
            var generation = Storage("SourceStorage", 20000, 5000);
            var sourceRules = Read(reactor, "CrewlessLogisticsCells"); componentMap[Field(sourceRules.GetType(), "FromStorage").GetValue(sourceRules)!] = generation;
            Make(reactor, "CrewlessLogisticsCells"); Make(reactor, "CrewlessLogisticsInput"); Make(reactor, "CrewlessLogisticsSource");
            Make(reactor, "CrewlessLogisticsNode"); Make(reactor, "CrewlessLogisticsSourceRouter");
            var generatorPump = PumpFromPackage(reactor, "CrewlessLogisticsTransfer"); Make(reactor, reverse ? TopLeft : TopRight);
            for (int i = 0; i <= count; i++) Cable(reverse ? -1 - 3 * i : 2 + 3 * i, 0);
            var inlineCaps = Enumerable.Range(0, count).Select(i => {
                var item = Capacitor(reverse ? -3 - 3 * i : 3 + 3 * i, 0, reverse ? TopRight : TopLeft);
                Make(capacitor, reverse ? TopLeft : TopRight);
                return item;
            }).ToArray();
            NewTopologyPart(reverse ? -2 - 3 * count : 3 + 3 * count, -2, 0); SetField(Property(part, "Rules"), "Size", Vector(1, 3));
            var sinkSwitch = Switch(consumer); var sink = Storage("ReceiverStorage", 1000, 0); componentMap[receiverIds.GetValue(0)!] = sink;
            var sinkBias = Make(consumer, "CrewlessLogisticsReceivingBias"); Make(consumer, "CrewlessLogisticsReceivingAggregate"); Make(consumer, "CrewlessLogisticsCells"); Make(consumer, "CrewlessLogisticsReceiver");
            Make(consumer, reverse ? Socket("Right", 0, 2) : Socket("Left", 0, 2));
            var realStores = new[] { generation, sink }.Concat(inlineCaps.SelectMany(c => new[] { c.Battery, c.Extra })).ToArray();
            var roundingStores = new[] { sinkBias }.Concat(inlineCaps.Select(c => c.Bias)).ToArray();
            void NativePulse(object pump)
            {
                var before = realStores.Select(s => Count(s, "Resources")).ToArray(); Pulse(pump);
                Require(realStores.Sum(s => Count(s, "Resources")) == before.Sum(), "Inline capacitor chain created or lost energy");
                for (int j = 0; j < realStores.Length; j++) Require((Count(realStores[j], "Resources") - before[j]) % 1000 == 0, "Inline capacitor chain transferred a partial cell");
                Require(roundingStores.All(s => Count(s, "Resources") == 999), "Inline capacitor chain consumed rounding bias");
            }
            void Tick()
            {
                foreach (var cap in inlineCaps) if (Flag(cap.SupplyGate, "IsToggleOn")) NativePulse(cap.Supply);
                if (Flag(supplyGate, "IsToggleOn")) NativePulse(generatorPump);
                foreach (var cap in inlineCaps) if (Flag(cap.ChargeGate, "IsToggleOn")) NativePulse(cap.Charge);
            }
            foreach (var item in inlineCaps) SetProperty(item.Switch, "Mode", 1);
            SetProperty(generatorSwitch, "Mode", 1); SetProperty(sinkSwitch, "Mode", 1);
            Require(inlineCaps.All(c => !Flag(c.ChargeGate, "IsToggleOn")), "Empty inline capacitor charged before an empty downstream consumer");
            if (legacyNegativeControl) {
                for (int i = 0; i < 4; i++) Tick();
                Require(Count(generation, "Resources") == 5000 && Count(sink, "Resources") == 0 && inlineCaps.All(c => Count(c.Battery, "Resources") == 0), "Legacy capacitor terminal routing did not reproduce the user's downstream supply/recharge deadlock");
                scenarioResults.Add(new { name = "legacy inline capacitor routing negative control", startingReactorEnergy = 5000, consumerEnergy = 0, capacitorEnergy = 0, consumerSupplyBlocked = true, capacitorRechargeBlocked = true });
                Console.WriteLine("PASS: negative control reproduces legacy inline capacitor deadlock: reactor retains 5000, downstream consumer and capacitor remain empty.");
                legacyCapacitorTerminals = false;
                return;
            }
            NativePulse(generatorPump);
            Require(Count(sink, "Resources") == 1000 && inlineCaps.All(c => Count(c.Battery, "Resources") == 0), "An empty inline capacitor isolated its downstream consumer");
            for (int i = 0; i < 4; i++) Tick();
            Require(inlineCaps.All(c => Count(c.Battery, "Resources") == 2000), "Inline capacitor failed to refill from reactor surplus");
            for (int i = 0; i < 20; i++) {
                Add(sink, -1000); Add(generation, 1000); Tick();
                Require(Count(sink, "Resources") == 1000 && inlineCaps.All(c => Count(c.Battery, "Resources") == 2000), "Inline capacitor chain stalled during sustained consumption and replenishment");
            }
            SetProperty(inlineCaps[0].Switch, "Mode", 0);
            for (int i = 1; i < inlineCaps.Length; i++) Add(inlineCaps[i].Battery, -Count(inlineCaps[i].Battery, "Resources"));
            Add(sink, -1000); Add(generation, 1000); Tick();
            Require(Count(sink, "Resources") == 0 && Count(inlineCaps[0].Battery, "Resources") == 2000, "Disabled inline capacitor failed to disconnect its bus or retain energy");
            SetProperty(inlineCaps[0].Switch, "Mode", 1); Tick();
            Require(Count(sink, "Resources") == 1000, "Re-enabling an inline capacitor failed to restore downstream supply");
            scenarioResults.Add(new { name = "native inline capacitor bus", capacitors = count, direction = reverse ? "right to left" : "left to right", startingReactorEnergy = 5000, sustainedCycles = 20, capacitorShuffle = false, energyConservation = true, toggledBusReconnect = true });
            Console.WriteLine($"PASS: inline native capacitor bus; capacitors={count}, reverse={reverse}, empty consumer supply, surplus charging, 20 sustained cycles and off/on reconnection.");
        }
        InlineCapacitors(1, false, true);
        foreach (int count in new[] { 1, 2 }) foreach (bool reverse in new[] { false, true }) InlineCapacitors(count, reverse);
        ReactorTransitProbe(root);
    }
}

