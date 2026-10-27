using System.Linq.Expressions;

sealed partial class NativeProbe
{
    void PackageNetworkSwitch(object root, string reactorPrefix, string consumerPrefix)
    {
        var manager = NewTopologyManager();
        NewTopologyPart(0, 0, 0);
        SetField(Property(part, "Rules"), "Size", Vector(2, 2));
        object Read(string prefix, string name) => Rules(prefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        object NativeComponent(string prefix, string name, bool initialize = true) {
            var component = Component(Read(prefix, name));
            if (initialize) {
                // Resource type catalog/crew registration requires full game startup.
                // Resolve storage links directly, as in the whole-cell fixture.
                if (Game("Cosmoteer.Ships.Parts.Resources.BaseResourceStorage").IsInstanceOfType(component)) {
                    InitializeLinks(component);
                    if (Game("Cosmoteer.Ships.Networks.BasePartNetworkResourceStorage").IsInstanceOfType(component)) RegisterNode(manager, component);
                }
                else if (Game("Cosmoteer.Ships.Networks.PartNetworkBasicNode").IsInstanceOfType(component)) RegisterNode(manager, component);
                else Call(component, "OnAddedToPart", part);
            }
            return component;
        }
        object NativeSwitch(string prefix) {
            var rules = Read(prefix, "CrewlessLogisticsToggle");
            Require((int)Field(rules.GetType(), "Default").GetValue(rules)! == 0, "Network switch must default off");
            Require((bool)Field(rules.GetType(), "RememberValue").GetValue(rules)!, "Network switch must use native remembered state");
            Require(!(bool)Field(rules.GetType(), "RequiresCommand").GetValue(rules)!, "Empty devices must allow network bootstrap without command power");
            // The isolated ship has no save metadata. Only persistence is bypassed;
            // actual Mode setters/events and native operational callbacks are used.
            SetField(rules, "RememberValue", false);
            var component = Component(rules);
            Call(component, "OnAddedToPart", part);
            var idType = Field(rules.GetType(), "ToggleID").FieldType;
            var callbackType = typeof(Func<,>).MakeGenericType(idType, typeof(int?));
            foreach (int? mode in new int?[] { null, 0, 1 }) {
                var argument = Expression.Parameter(idType, "id");
                var callback = Expression.Lambda(callbackType, Expression.Constant(mode, typeof(int?)), argument).Compile();
                Require((bool)Call(rules, "IsBlueprintToggleOn", null, callback)! == (mode == 1), "Native editor switch state mismatch");
            }
            return component;
        }

        var sourceSwitch = NativeSwitch(reactorPrefix);
        var operational = Component("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
        SetToggle(operational, true);
        var componentIdType = Field(Read(reactorPrefix, "CrewlessLogisticsToggle").GetType(), "ID").FieldType;
        componentMap[Id(componentIdType, "IsOperational")] = operational;
        NativeComponent(reactorPrefix, "CrewlessLogisticsCanTransfer");
        var source = Storage("SourceStorage", 8000, 1500);
        var sourceCellRules = Read(reactorPrefix, "CrewlessLogisticsCells");
        componentMap[Field(sourceCellRules.GetType(), "FromStorage").GetValue(sourceCellRules)!] = source;
        var sourceCells = NativeComponent(reactorPrefix, "CrewlessLogisticsCells");
        var input = NativeComponent(reactorPrefix, "CrewlessLogisticsInput");
        NativeComponent(reactorPrefix, "CrewlessLogisticsSource");
        NativeComponent(reactorPrefix, "CrewlessLogisticsNode");
        NativeComponent(reactorPrefix, "CrewlessLogisticsSourceRouter");
        var sourcePort = NativeComponent(reactorPrefix, Socket("Right", 1, 0));
        NativeComponent(reactorPrefix, Socket("Left", 0, 0)); // hyperdrive FtlDrive gate scenario
        var pumpRules = Read(reactorPrefix, "CrewlessLogisticsTransfer");
        Require(Field(pumpRules.GetType(), "OperationalToggle").GetValue(pumpRules)?.ToString() == "CrewlessLogisticsCanTransfer", "Source pump must honor operation and network switch");
        Require((string)ReadRuleObject(root, reactorPrefix + "CrewlessLogisticsCanTransfer/Mode", typeof(string)) == "All", "Source pump gate must require every condition");
        Require(((string[])ReadRuleObject(root, reactorPrefix + "CrewlessLogisticsCanTransfer/Toggles", typeof(string[]))).SequenceEqual(new[] { "IsOperational", "CrewlessLogisticsToggle" }), "Source pump gate must preserve reactor operation");
        var pump = Pump(sourceCells, input);

        NewTopologyPart(2, -2, 0);
        SetField(Property(part, "Rules"), "Size", Vector(1, 3));
        var receiverSwitch = NativeSwitch(consumerPrefix);
        var target = Storage("ReceiverStorage", 4000, 0);
        var batteryId = Field(Read(consumerPrefix, "CrewlessLogisticsReceivingAggregate").GetType(), "ResourceStorages");
        var targetStorageId = ((Array)batteryId.GetValue(Read(consumerPrefix, "CrewlessLogisticsReceivingAggregate"))!).GetValue(0)!;
        componentMap[targetStorageId] = target;
        var bias = NativeComponent(consumerPrefix, "CrewlessLogisticsReceivingBias");
        NativeComponent(consumerPrefix, "CrewlessLogisticsReceivingAggregate");
        NativeComponent(consumerPrefix, "CrewlessLogisticsCells");
        var store = NativeComponent(consumerPrefix, "CrewlessLogisticsReceiver");
        var targetPort = NativeComponent(consumerPrefix, Socket("Left", 0, 2));
        var circuit = new Circuit(new[] { source }, new[] { target }, new[] { bias }, pump);

        Require(!Flag(sourceSwitch, "IsToggleOn") && !Flag(receiverSwitch, "IsToggleOn"), "Both devices must start disconnected");
        Require(Peer(sourcePort) == null && Peer(targetPort) == null, "Default-off device ports connected");
        CheckedPulse(circuit);
        Require(Count(target, "Resources") == 0, "Disabled circuit transferred energy");
        SetProperty(sourceSwitch, "Mode", 1);
        Require(Flag(sourcePort, "IsOperational") && !Flag(store, "IsOperational"), "Source and receiver switches are not independent");
        CheckedPulse(circuit);
        Require(Count(target, "Resources") == 0, "Source-on receiver-off circuit transferred energy");
        SetProperty(receiverSwitch, "Mode", 1);
        RequirePair(sourcePort, targetPort, "network switches enabled without overclock");
        CheckedPulse(circuit);
        Require(Count(source, "Resources") == 500 && Count(target, "Resources") == 1000, "Enabling both devices did not transfer exactly one cell");

        SetProperty(receiverSwitch, "Mode", 0);
        Require(Peer(sourcePort) == null && Peer(targetPort) == null, "Receiver switch left a connected port");
        Add(source, 1000);
        CheckedPulse(circuit);
        Require(Count(source, "Resources") == 1500 && Count(target, "Resources") == 1000, "Receiver shutdown lost stored energy or accepted a cell");
        SetProperty(receiverSwitch, "Mode", 1);
        RequirePair(sourcePort, targetPort, "receiver switch reconnect");
        CheckedPulse(circuit);
        Require(Count(target, "Resources") == 2000, "Receiver reconnect failed");

        SetProperty(sourceSwitch, "Mode", 0);
        Require(!Flag(sourcePort, "IsOperational") && Flag(store, "IsOperational"), "Source shutdown affected the receiver switch");
        Add(source, 1000);
        CheckedPulse(circuit);
        Require(Count(source, "Resources") == 1500 && Count(target, "Resources") == 2000, "Source shutdown lost reserve or supplied energy");
        SetProperty(sourceSwitch, "Mode", 1);
        RequirePair(sourcePort, targetPort, "source switch reconnect");
        CheckedPulse(circuit);
        Require(Count(source, "Resources") == 500 && Count(target, "Resources") == 3000, "Source reconnect failed whole-cell delivery");
        scenarioResults.Add(new { name = "per-device native network UI switches", defaultMode = 0, states = "off/off, on/off, on/on, on/off, on/on, off/on, on/on", source = 500, target = 3000, persistence = "not exercised; RememberValue bypassed in fixture only" });

        // Vanilla hyperdrive BatteryConsumer: OperationalToggle = FtlDrive, AllowNonOperationalMinPriorityDelivery = false.
        // The network receiver must accept only while the drive spools, beside an ungated weapon.
        const string hyper = "<ships/terran/hyperdrive_small/hyperdrive_small.rules>/Part/Components/";
        Require(((string[])ReadRuleObject(root, hyper + "CrewlessLogisticsAcceptGate/Toggles", typeof(string[]))).SequenceEqual(new[] { "CrewlessLogisticsToggle", "FtlDrive" }), "Hyperdrive receiver gate must mirror vanilla FtlDrive delivery");
        Require((string)ReadRuleObject(root, hyper + "CrewlessLogisticsRoomInlet/ProxyToggle", typeof(string)) == "CrewlessLogisticsAcceptGate", "Hyperdrive engine-room inlet must use the FtlDrive gate");
        Require((string)ReadRuleObject(root, consumerPrefix + "CrewlessLogisticsReceiver/OperationalToggle", typeof(string)) == "CrewlessLogisticsToggle", "Weapon receiver must stay switch-only");
        NewTopologyPart(-2, 0, 0);
        SetField(Property(part, "Rules"), "Size", Vector(2, 2));
        var hyperSwitch = NativeSwitch(hyper);
        var ftl = Component("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
        SetToggle(ftl, false);
        componentMap[Id(componentIdType, "FtlDrive")] = ftl;
        NativeComponent(hyper, "CrewlessLogisticsAcceptGate");
        var hyperTarget = Storage("ReceiverStorage", 16000, 0);
        componentMap[Id(componentIdType, "BatteryStorage")] = hyperTarget;
        var hyperBias = NativeComponent(hyper, "CrewlessLogisticsReceivingBias");
        NativeComponent(hyper, "CrewlessLogisticsReceivingAggregate");
        NativeComponent(hyper, "CrewlessLogisticsCells");
        var hyperStore = NativeComponent(hyper, "CrewlessLogisticsReceiver");
        var hyperPort = NativeComponent(hyper, Socket("Right", 1, 0));
        var both = new Circuit(new[] { source }, new[] { target, hyperTarget }, new[] { bias, hyperBias }, pump);
        SetProperty(hyperSwitch, "Mode", 1);
        Require(!Flag(hyperStore, "IsOperational"), "Hyperdrive receiver open with FtlDrive off");
        SetCount(target, 1000); Add(source, 4000);
        CheckedPulse(both); CheckedPulse(both); CheckedPulse(both);
        Require(Count(hyperTarget, "Resources") == 0 && Count(target, "Resources") == 4000, "Switch-on hyperdrive accepted energy without FtlDrive, or weapon starved");
        SetToggle(ftl, true);
        Require(Flag(hyperStore, "IsOperational"), "Hyperdrive receiver closed while FtlDrive spools");
        CheckedPulse(both);
        Require(Count(hyperTarget, "Resources") == 1000, "Spooling hyperdrive did not fill from the network");
        SetToggle(ftl, false);
        CheckedPulse(both);
        Require(Count(hyperTarget, "Resources") == 1000 && Count(source, "Resources") == 500, "Hyperdrive kept accepting after FtlDrive stopped");
        scenarioResults.Add(new { name = "hyperdrive receiver mirrors vanilla FtlDrive crew delivery", ftlOff = 0, ftlOn = 1000, weapon = 4000 });
        Console.WriteLine("PASS: switch-on hyperdrive receives 0 with FtlDrive off, fills while FtlDrive spools; weapon on the same network keeps receiving.");
        Console.WriteLine("PASS: actual package UIToggle events independently disconnect/reconnect both devices, retain energy and bootstrap an empty consumer without overclock.");
    }
}
