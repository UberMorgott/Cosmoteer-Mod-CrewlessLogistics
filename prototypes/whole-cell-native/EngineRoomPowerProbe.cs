using System.Collections;
using System.Text.Json;

sealed partial class NativeProbe
{
    sealed record EngineRoomFixture(object Part, IDictionary Map, object Battery, object Bias,
        object Cells, object Receiver, object Recipients, object Handler, object Supply,
        object Gate, object NetworkSwitch, object DistributionSwitch, object Pump);
    sealed record EngineRecipientFixture(object Part, object Battery, object Bias, object Cells, object Receiver, object Switch);

    void EngineRoomPowerProbe(object root)
    {
        const string prefix = "<ships/terran/engine_room/engine_room.rules>/Part/Components/";
        object Read(string name) => Rules(prefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        Require((string)ReadRuleObject(root, prefix + "BatteryConsumer/Storage", typeof(string)) == "BatteryDistributionStorage", "Engine-room native crew delivery changed");
        Require((string)ReadRuleObject(root, prefix + "Overclock_CombinedBatteryStorageProxy/ComponentID", typeof(string)) == "CombinedBatteryStorage", "Engine-room native overclock delivery changed");
        Require((float)ReadRuleObject(root, prefix + "Overclock_CombinedBatteryStorageProxy/QuantityScale", typeof(float)) < 1f, "Native overclock distribution multiplier was removed");
        Require(((string[])ReadRuleObject(root, prefix + "CrewlessLogisticsReceivingAggregate/ResourceStorages", typeof(string[]))).SequenceEqual(new[] { "BatteryStorage", "CrewlessLogisticsReceivingBias" }), "Engine room exposes overlapping network recipient capacity");
        Require((string)ReadRuleObject(root, prefix + "CrewlessLogisticsEngineRoomSupplyCells/FromStorage", typeof(string)) == "BatteryStorage", "Engine-room outgoing cells must use the unbiased physical reserve");
        Require((int)ReadRuleObject(root, prefix + "CrewlessLogisticsEngineRoomDistribution/MinFromQuantityForConversion", typeof(int)) == 2, "Engine room must retain one complete cell");
        Require((int)ReadRuleObject(root, prefix + "CrewlessLogisticsEngineRoomDistribution/FromQuantity", typeof(int)) == 1 && (int)ReadRuleObject(root, prefix + "CrewlessLogisticsEngineRoomDistribution/ToQuantity", typeof(int)) == 1, "Engine room electrical distribution must remain unscaled whole cells");
        Require(((string[])ReadRuleObject(root, prefix + "CrewlessLogisticsEngineRoomRecipients/ViaBuffs/ComponentIDs", typeof(string[]))).SequenceEqual(new[] { "CrewlessLogisticsRoomInlet" }), "Engine room targets raw or overlapping recipient aliases");

        object Native(string name, bool initialize = true)
        {
            var value = Component(Read(name));
            if (initialize) InitializeLinks(value);
            return value;
        }
        object DebugInput(string id, bool enabled)
        {
            var rules = Rules("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
            var field = Field(rules.GetType(), "ID"); field.SetValue(rules, Id(field.FieldType, id));
            var value = Component(rules); SetToggle(value, enabled); return value;
        }
        EngineRoomFixture Room(int amount, int x)
        {
            NewTopologyPart(x, 0, 0);
            var battery = Storage("ReceiverStorage", 4000, amount);
            componentMap[Field(Read("CrewlessLogisticsEngineRoomSupplyCells").GetType(), "FromStorage").GetValue(Read("CrewlessLogisticsEngineRoomSupplyCells"))!] = battery;
            var networkSwitch = DebugInput("CrewlessLogisticsToggle", true);
            var distributionSwitch = DebugInput("DistributePowerToggle", true);
            var bias = Native("CrewlessLogisticsReceivingBias");
            Native("CrewlessLogisticsReceivingAggregate");
            var cells = Native("CrewlessLogisticsCells");
            var receiver = Native("CrewlessLogisticsReceiver"); Call(receiver, "OnNetworkRegistered");
            var supply = Native("CrewlessLogisticsEngineRoomSupplyCells");
            var recipients = Native("CrewlessLogisticsEngineRoomRecipients");
            var gate = Native("CrewlessLogisticsEngineRoomCanDistribute", false); Call(gate, "OnAddedToPart", part);
            var pump = Native("CrewlessLogisticsEngineRoomDistribution", false);
            ((Array)Field(pump.GetType(), "_fromStorages").GetValue(pump)!).SetValue(supply, 0);
            ((Array)Field(pump.GetType(), "_toStorages").GetValue(pump)!).SetValue(recipients, 0);
            var handler = Field(recipients.GetType(), "_viaBuffs").GetValue(recipients)!;
            return new(part, componentMap, battery, bias, cells, receiver, recipients, handler, supply, gate, networkSwitch, distributionSwitch, pump);
        }
        EngineRecipientFixture Recipient(int amount, int x)
        {
            NewTopologyPart(x, 1, 0);
            var battery = Storage("ReceiverStorage", 4000, amount);
            var cells = Receiver(battery, out var bias);
            var cellIdType = Field(Read("CrewlessLogisticsCells").GetType(), "ID").FieldType;
            componentMap[Id(cellIdType, "CrewlessLogisticsCells")] = cells;
            // Actual package inlet: native proxy gated by the recipient's own switch.
            var networkSwitch = DebugInput("CrewlessLogisticsToggle", true);
            var inlet = Component(Read("CrewlessLogisticsRoomInlet")); InitializeLinks(inlet); Call(inlet, "OnPartAttached2");
            var receiver = Store("ReceiverStore", cells);
            return new(part, battery, bias, cells, receiver, networkSwitch);
        }
        void Connect(EngineRoomFixture room, EngineRecipientFixture target) => Call(room.Handler, "OnOutgoingBuffReceiverAdded", target.Part);
        void Disconnect(EngineRoomFixture room, EngineRecipientFixture target) => Call(room.Handler, "OnOutgoingBuffReceiverRemoved", target.Part);
        void Tick(EngineRoomFixture room, params EngineRecipientFixture[] targets)
        {
            // The native scheduler is not created in this media-free fixture.
            // Its operational input is the actual native MultiToggle and events.
            if (Flag(room.Gate, "IsToggleOn")) CheckedPulse(new Circuit(new[] { room.Battery }, targets.Select(x => x.Battery).Distinct().ToArray(), new[] { room.Bias }.Concat(targets.Select(x => x.Bias)).ToArray(), room.Pump));
        }

        var room = Room(3200, 0);
        var a = Recipient(0, 1); var b = Recipient(0, 2);
        // Negative control: the existing local receiver cannot spend the room's
        // 3200 units merely because native thruster recipients are empty.
        var source = Storage("SourceStorage", 8000, 1500);
        var inputRules = Rules("<ships/terran/reactor_small/reactor_small.rules>/Part/Components/CrewlessLogisticsInput", "Cosmoteer.Ships.Networks.PartNetworkResourceInputRules", root);
        var legacy = new Circuit(new[] { source }, new[] { room.Battery }, new[] { room.Bias }, Pump(NetworkIo("NetworkOutput", true, new[] { Store("SourceStore", Inline("SourceCells", source)) }), ConsumerCoverageInput(inputRules, room.Receiver)));
        CheckedPulse(legacy);
        Require(Count(room.Battery, "Resources") == 3200 && Count(a.Battery, "Resources") == 0 && Count(b.Battery, "Resources") == 0, "Negative control failed to reproduce stored room energy with empty engines");

        // The native buff handler resolves each actual Part's component map;
        // seeded callbacks model provider add/remove events, not a custom list.
        Connect(room, a); Connect(room, b);
        Require(Count(room.Recipients, "MaxResources") == 8, "Native outgoing buff discovery did not find both whole-cell recipient views");
        Connect(room, a);
        Require(Count(room.Recipients, "MaxResources") == 8, "Native buff reference counting duplicated recipient capacity");
        Disconnect(room, a);
        for (int i = 0; i < 10; i++) Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 1200 && Count(a.Battery, "Resources") == 1000 && Count(b.Battery, "Resources") == 1000, "Native room reserve failed to supply both engines while preserving its final cell/remainder");

        SetCount(room.Battery, 4000); SetCount(a.Battery, 3500); SetCount(b.Battery, 3500);
        Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 4000 && Count(a.Battery, "Resources") == 3500 && Count(b.Battery, "Resources") == 3500, "Engine room combined fractional recipient deficits");
        SetCount(a.Battery, 3000); Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 3000 && Count(a.Battery, "Resources") == 4000 && Count(b.Battery, "Resources") == 3500, "Engine room split one cell between recipients");
        foreach (int available in new[] { 0, 500, 999, 1000, 1500, 1999 }) {
            SetCount(room.Battery, available); SetCount(a.Battery, 0); SetCount(b.Battery, 4000);
            Tick(room, a, b);
            Require(Count(room.Battery, "Resources") == available && Count(a.Battery, "Resources") == 0, "Engine room spent its retained cell or fractional source reserve");
        }
        SetCount(room.Battery, 2000); Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 1000 && Count(a.Battery, "Resources") == 1000, "Two local cells did not permit exactly one cell of engine distribution");
        // A recipient whose own Electrical Network switch is off receives nothing.
        SetCount(room.Battery, 4000); SetCount(a.Battery, 0); SetCount(b.Battery, 4000);
        SetToggle(a.Switch, false);
        Require(Count(room.Recipients, "MaxResources") == 4, "Switched-off recipient still advertised room delivery capacity");
        Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 4000 && Count(a.Battery, "Resources") == 0, "Engine room delivered into a recipient with its network switch off");
        SetToggle(a.Switch, true); Tick(room, a, b);
        Require(Count(room.Battery, "Resources") == 3000 && Count(a.Battery, "Resources") == 1000, "Re-enabled recipient switch did not resume room delivery");

        foreach (object control in new[] { room.NetworkSwitch, room.DistributionSwitch }) {
            SetCount(room.Battery, 4000); SetCount(a.Battery, 0); SetCount(b.Battery, 0);
            SetToggle(control, false);
            Require(!Flag(room.Gate, "IsToggleOn"), "Native electrical/distribution switch failed to disable the delivery gate");
            Tick(room, a, b);
            Require(Count(room.Battery, "Resources") == 4000 && Count(a.Battery, "Resources") == 0, "Disabled room distribution moved energy");
            SetToggle(control, true);
            Require(Flag(room.Gate, "IsToggleOn"), "Native delivery gate did not reopen");
        }
        // Incoming electrical delivery remains independent of room operation,
        // crew/distribution and overclock: its native gate is CrewlessLogisticsToggle.
        SetToggle(room.DistributionSwitch, false);
        foreach (string reactor in new[] { "reactor_small", "reactor_med", "reactor_large" }) {
            SetCount(room.Battery, 0); SetCount(source, 1500);
            var rules = Rules($"<ships/terran/{reactor}/{reactor}.rules>/Part/Components/CrewlessLogisticsInput", "Cosmoteer.Ships.Networks.PartNetworkResourceInputRules", root);
            var incoming = new Circuit(new[] { source }, new[] { room.Battery }, new[] { room.Bias }, Pump(NetworkIo("NetworkOutput", true, new[] { Store("SourceStore", Inline("SourceCells", source)) }), ConsumerCoverageInput(rules, room.Receiver)));
            CheckedPulse(incoming);
            Require(Count(room.Battery, "Resources") == 1000 && Count(source, "Resources") == 500, "Electrical room bootstrap incorrectly requires active distribution: " + reactor);
        }
        SetToggle(room.DistributionSwitch, true);

        // Separate physical room reserves can target the same engine. A direct
        // engine wire must also update those native whole-cell views immediately.
        var second = Room(4000, 4); Connect(second, a);
        SetCount(room.Battery, 4000); SetCount(a.Battery, 3000); SetCount(b.Battery, 4000);
        var directSource = Storage("SourceStorage", 8000, 1500);
        var direct = new Circuit(new[] { directSource }, new[] { a.Battery }, new[] { a.Bias }, Pump(NetworkIo("NetworkOutput", true, new[] { Store("SourceStore", Inline("SourceCells", directSource)) }), NetworkIo("NetworkInput", false, new[] { a.Receiver })));
        CheckedPulse(direct);
        Tick(room, a, b); Tick(second, a);
        Require(Count(room.Battery, "Resources") == 4000 && Count(second.Battery, "Resources") == 4000 && Count(a.Battery, "Resources") == 4000, "Direct engine wire left stale room-recipient capacity");
        SetCount(a.Battery, 3000); Tick(room, a, b); Tick(second, a);
        Require(Count(room.Battery, "Resources") == 3000 && Count(second.Battery, "Resources") == 4000 && Count(a.Battery, "Resources") == 4000, "Two rooms spent two cells on one engine deficit");
        SetCount(a.Battery, 0); Disconnect(room, a); Disconnect(second, a);
        Require(Count(room.Recipients, "MaxResources") == 4 && Count(second.Recipients, "MaxResources") == 0, "Buff removal left stale recipient capacity");
        Tick(room, a, b); Tick(second, a);
        Require(Count(a.Battery, "Resources") == 0, "Disconnected native buff target still received power");
        Connect(room, a); Tick(room, a, b);
        Require(Count(a.Battery, "Resources") == 1000, "Reconnected engine failed to resume native distribution");

        string reportPath = Path.Combine(projectRoot, "artifacts", "whole-cell-native", "engine-room-power-validation.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
            negativeControl = "Room retains 3200 units while both engines remain empty with the old local-only receiver.",
            wholeCellRecipientDistribution = true, localWholeCellReserve = 1, nativeBuffRecipientCallbacks = true,
            duplicateRecipientReferenceCounting = true, fractionalSourceAndDeficitChecks = true,
            incomingRoomBootstrap = new[] { "reactor_small", "reactor_med", "reactor_large" },
            directWireAndTwoRoomOverlap = true, distributionAndNetworkGates = true, recipientSwitchGate = true, disconnectAndReconnect = true,
            overclock = "Electrical 1:1 conversion has no overclock references; original crew multiplier remains unchanged.",
            limitations = new[] { "Synthetic ownership; actual native outgoing-buff add/remove callbacks are invoked directly. Full grid-provider adjacency scanning is not exercised.", "Actual native MultiToggle events are exercised; automatic simulation scheduling, crew lifecycle and overclock controller are not instantiated.", "Remote rocket extension/battery aliases are not traversed by this direct-recipient adapter." }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: engine-room local-reserve native whole-cell delivery; two engines, retained cell, fractional waits, gates, recipient switch, 3 reactor bootstrap queries, shared engine/direct wire and native buff callback removal.");
    }
}
