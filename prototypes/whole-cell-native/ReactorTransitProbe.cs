sealed partial class NativeProbe
{
    void ReactorTransitProbe(object root)
    {
        const string consumer = "<ships/terran/laser_blaster_small/laser_blaster_small.rules>/Part/Components/";
        string Prefix(string name) => $"<ships/terran/{name}/{name}.rules>/Part/Components/";
        object Read(string prefix, string name) => Rules(prefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        string[] SourcePorts(int size) => PerimeterSockets(size);
        object Switch(string prefix) {
            var rules = Read(prefix, "CrewlessLogisticsToggle"); SetField(rules, "RememberValue", false);
            Require((int)Field(rules.GetType(), "Default").GetValue(rules)! == 0, "Reactor bus switch must default off");
            var toggle = Component(rules); Call(toggle, "OnAddedToPart", part); return toggle;
        }
        void Case(string relayName, int size, bool reverse, bool legacyNegativeControl = false)
        {
            var manager = NewTopologyManager();
            object Make(string prefix, string name, bool converter = false, object? overrideRules = null) {
                var component = Component(overrideRules ?? Read(prefix, name));
                if (Game("Cosmoteer.Ships.Parts.Resources.BaseResourceStorage").IsInstanceOfType(component)) {
                    InitializeLinks(component);
                    if (Game("Cosmoteer.Ships.Networks.BasePartNetworkResourceStorage").IsInstanceOfType(component)) RegisterNode(manager, component);
                } else if (Game("Cosmoteer.Ships.Networks.PartNetworkBasicNode").IsInstanceOfType(component)) RegisterNode(manager, component);
                else if (converter) InitializeLinks(component);
                else Call(component, "OnAddedToPart", part);
                return component;
            }
            object? legacyFile = null;
            if (legacyNegativeControl) {
                string path = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "legacy-reactor-routing-control.rules");
                File.WriteAllText(path, "LegacyRouter { Type = NetworkRouter; OperationalToggle = CrewlessLogisticsToggle; RouteGenerators [{ Type = Simple; Routes = [[CrewlessLogisticsInput, CrewlessLogisticsSource, 0]] }] }");
                legacyFile = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { path })!;
            }
            (object Battery, object Switch, object GenerationToggle, object Pump, object SupplyGate, object Router, object Provider) Reactor(string name, int dimension, int x, int stock, bool generationOn, bool legacy)
            {
                string prefix = Prefix(name); NewTopologyPart(x, 0, 0); SetField(Property(part, "Rules"), "Size", Vector(dimension, dimension));
                var generationRules = Rules("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
                var idField = Field(generationRules.GetType(), "ID"); idField.SetValue(generationRules, Id(idField.FieldType, "IsOperational"));
                var generationToggle = Component(generationRules); SetToggle(generationToggle, generationOn);
                var networkToggle = Switch(prefix); var supplyGate = Make(prefix, "CrewlessLogisticsCanTransfer");
                var battery = Storage("SourceStorage", 8000, stock);
                var cellRules = Read(prefix, "CrewlessLogisticsCells"); componentMap[Field(cellRules.GetType(), "FromStorage").GetValue(cellRules)!] = battery;
                Make(prefix, "CrewlessLogisticsCells"); Make(prefix, "CrewlessLogisticsInput"); var provider = Make(prefix, "CrewlessLogisticsSource");
                Make(prefix, "CrewlessLogisticsNode");
                var router = Make(prefix, "CrewlessLogisticsSourceRouter", false, legacy ? Rules("LegacyRouter", "Cosmoteer.Ships.Parts.PartComponentRules", legacyFile!) : null);
                var pump = Make(prefix, "CrewlessLogisticsTransfer", true);
                foreach (string portName in SourcePorts(dimension)) {
                    var portRules = Read(prefix, portName);
                    Require(Property(portRules, "RouteSource")?.ToString() == "CrewlessLogisticsNode" && Property(portRules, "RouteEndpoint")?.ToString() == "CrewlessLogisticsNode", "Reactor perimeter socket does not use the shared electrical bus: " + name + "/" + portName);
                    Require(!(bool)Field(Read(prefix, "CrewlessLogisticsSource").GetType(), "AllowReceiving").GetValue(Read(prefix, "CrewlessLogisticsSource"))!, "A relay reactor must never accept stored cells from another source");
                    if (legacy) {
                        var source = portRules.GetType().GetProperty("RouteSource", All)!; var endpoint = portRules.GetType().GetProperty("RouteEndpoint", All)!;
                        source.SetValue(portRules, Id(Nullable.GetUnderlyingType(source.PropertyType)!, "CrewlessLogisticsInput")); endpoint.SetValue(portRules, Id(Nullable.GetUnderlyingType(endpoint.PropertyType)!, "CrewlessLogisticsSource"));
                    }
                    Make(prefix, portName, false, portRules);
                }
                return (battery, networkToggle, generationToggle, pump, supplyGate, router, provider);
            }
            void Cable(int x) {
                NewTopologyPart(x, 0, 0); GraphNode(manager, "PortProbeElectricNode"); GraphPort("PortProbeCableElectricLeft"); GraphPort("PortProbeCableElectricRight");
            }
            var source = Reactor("reactor_small", 2, 0, 3500, true, false);
            int relayX = reverse ? -1 - size : 3;
            Cable(reverse ? -1 : 2);
            var relay = Reactor(relayName, size, relayX, 0, false, legacyNegativeControl);
            Cable(reverse ? -2 - size : 3 + size);
            NewTopologyPart(reverse ? -3 - size : 4 + size, -2, 0); SetField(Property(part, "Rules"), "Size", Vector(1, 3));
            var consumerSwitch = Switch(consumer); var target = Storage("ReceiverStorage", 4000, 0);
            var aggregateRules = Read(consumer, "CrewlessLogisticsReceivingAggregate"); var ids = (Array)Field(aggregateRules.GetType(), "ResourceStorages").GetValue(aggregateRules)!;
            componentMap[ids.GetValue(0)!] = target; var bias = Make(consumer, "CrewlessLogisticsReceivingBias"); Make(consumer, "CrewlessLogisticsReceivingAggregate"); Make(consumer, "CrewlessLogisticsCells"); Make(consumer, "CrewlessLogisticsReceiver");
            Make(consumer, reverse ? Socket("Right", 0, 2) : Socket("Left", 0, 2));
            SetProperty(source.Switch, "Mode", 1); SetProperty(relay.Switch, "Mode", 1); SetProperty(consumerSwitch, "Mode", 1);
            var circuit = new Circuit(new[] { source.Battery, relay.Battery }, new[] { target }, new[] { bias }, source.Pump);
            if (legacyNegativeControl) {
                for (int i = 0; i < 4; i++) CheckedPulse(circuit);
                Require(Count(source.Battery, "Resources") == 3500 && Count(target, "Resources") == 0 && Count(relay.Battery, "Resources") == 0, "Old reactor terminal routing did not reproduce blocked downstream delivery");
                scenarioResults.Add(new { name = "legacy reactor transit negative control", blocked = true, source = 3500, relay = 0, consumer = 0 });
                Console.WriteLine("PASS: negative control reproduces old reactor terminal routing blocking a downstream consumer while the source retains 3500 units.");
                return;
            }
            Require(Flag(relay.Router, "IsOperational") && !Flag(relay.Provider, "IsOperational"), "Electrical relay incorrectly depends on reactor generation operation");
            CheckedPulse(circuit);
            Require(Count(source.Battery, "Resources") == 2500 && Count(target, "Resources") == 1000 && Count(relay.Battery, "Resources") == 0, "Empty non-operational reactor failed to relay one cell without storing or duplicating it");
            SetProperty(relay.Switch, "Mode", 0); CheckedPulse(circuit);
            Require(Count(source.Battery, "Resources") == 2500 && Count(target, "Resources") == 1000 && Count(relay.Battery, "Resources") == 0, "Disabled reactor bus still relayed or changed stored energy");
            SetProperty(relay.Switch, "Mode", 1); CheckedPulse(circuit);
            Require(Count(source.Battery, "Resources") == 1500 && Count(target, "Resources") == 2000 && Count(relay.Battery, "Resources") == 0, "Re-enabled reactor bus failed to restore exact-cell relay");
            SetToggle(relay.GenerationToggle, true); CheckedPulse(circuit);
            Require(Count(source.Battery, "Resources") == 500 && Count(target, "Resources") == 3000 && Count(relay.Battery, "Resources") == 0, "An operational empty reactor changed transit energy accounting");
            CheckedPulse(circuit);
            Require(Count(source.Battery, "Resources") == 500 && Count(target, "Resources") == 3000 && Count(relay.Battery, "Resources") == 0, "Reactor transit combined or forwarded a fractional source cell");
            scenarioResults.Add(new { name = "native reactor electrical transit", reactor = relayName, size = dimensionForReport(size), direction = reverse ? "right to left" : "left to right", initializedPorts = SourcePorts(size).Length, generationOffRelay = true, switchDisconnectReconnect = true, source = 500, relay = 0, consumer = 3000, wholeCellEnergy = 3500 });
            Console.WriteLine($"PASS: native reactor bus {relayName}, reverse={reverse}; all {SourcePorts(size).Length} ports initialize, empty/generation-off relay, switch off/on and three exactly-once cells.");
        }
        static int[] dimensionForReport(int size) => new[] { size, size };
        Case("reactor_small", 2, false, true);
        foreach (var relay in new[] { ("reactor_small", 2), ("reactor_med", 3), ("reactor_large", 4) }) foreach (bool reverse in new[] { false, true }) Case(relay.Item1, relay.Item2, reverse);
    }
}
