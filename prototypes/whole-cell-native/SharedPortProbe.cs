using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

// Test ownership only. Native game code discovers ports, builds routes and queries,
// reacts to toggle events, and transfers resources. No graph is supplied here.
sealed partial class NativeProbe
{
    object Vector(int x, int y) => Activator.CreateInstance(Core("Halfling.Geometry.IntVector2"), x, y)!;
    static object Property(object target, string name) => target.GetType().GetProperty(name, All)!.GetValue(target)!;
    static bool Flag(object target, string name) => (bool)Property(target, name);
    static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, All)!.SetValue(target, value);

    void NewTopologyPart(int x, int y, int rotFlip)
    {
        part = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.Part"));
        SetField(part, "p_ship", ship);
        SetField(part, "_selfActive", true);
        SetField(part, "_rotFlip", rotFlip);
        var eventsField = Field(part.GetType(), "_events");
        eventsField.SetValue(part, Activator.CreateInstance(eventsField.FieldType)!);
        SetField(part, "<Location>k__BackingField", Vector(x, y));
        var rules = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartRules"));
        SetField(rules, "Size", Vector(1, 1));
        SetField(part, "<Rules>k__BackingField", rules);
        var mapField = Field(part.GetType(), "_componentsByID");
        componentMap = (IDictionary)Activator.CreateInstance(mapField.FieldType)!;
        mapField.SetValue(part, componentMap);
    }

    object NewTopologyManager()
    {
        var types = new[] { Game("Cosmoteer.Ships.Parts.Part"), Game("Cosmoteer.Ships.Parts.PartComponent") };
        var portType = Game("Cosmoteer.Ships.Networks.IRoutedPartNetworkPort`2").MakeGenericType(types);
        var managerType = Game("Cosmoteer.Ships.Networks.ShipPartNetworkManager`3").MakeGenericType(types[0], types[1], portType);
        var parts = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartsManager"));
        var manager = Activator.CreateInstance(managerType, All, null, new[] { ship, parts }, null)!;
        SetField(ship, "<PartNetworks>k__BackingField", manager);
        SetField(ship, "_parentIsActive", true);
        SetField(ship, "<SelfActive>k__BackingField", true);
        return manager;
    }

    void RegisterNode(object manager, object component)
    {
        var method = manager.GetType().GetMethod("RequestComponentRegistration", All)!.MakeGenericMethod(component.GetType());
        method.Invoke(manager, new[] { component });
    }

    object GraphIo(object manager, string template)
    {
        var io = Component(template, "Cosmoteer.Ships.Networks.PartNetworkResourceInputRules");
        RegisterNode(manager, io);
        return io;
    }
    object GraphStore(object manager, string template, object backing)
    {
        var rules = Rules(template, "Cosmoteer.Ships.Networks.PartNetworkResourceStoreRules");
        componentMap[Field(rules.GetType(), "ResourceStorage").GetValue(rules)!] = backing;
        var store = Component(rules);
        InitializeLinks(store);
        RegisterNode(manager, store);
        return store;
    }
    object GraphNode(object manager, string template)
    {
        var rules = Rules(template, "Cosmoteer.Ships.Networks.PartNetworkBasicNodeRules");
        Require(Flag(rules, "HasPhysicalComponent"), "Simulation routing node would be omitted by the native part constructor: " + template);
        var node = Component(rules);
        RegisterNode(manager, node);
        return node;
    }
    object GraphPort(string template)
    {
        var port = Component(template, "Cosmoteer.Ships.Networks.PartNetworkPortRules");
        // Full native operational lifecycle: resolves toggle, subscribes its events,
        // checks active ownership, then registers with the native port handler.
        Call(port, "OnAddedToPart", part);
        return port;
    }
    void SetToggle(object toggle, bool value) => SetProperty(toggle, "IsToggleOn", value);
    object? Peer(object port) => Field(port.GetType(), "_connectedPort").GetValue(port);
    void RequirePair(object a, object b, string channel)
    {
        Require(ReferenceEquals(Peer(a), b) && ReferenceEquals(Peer(b), a), $"Wrong native pairing: {channel}");
    }
    void RequireSeparated(object electric, object heat)
    {
        var electricNetwork = Property(Property(electric, "NetworkNode"), "Subnetwork");
        var heatNetwork = Property(Property(heat, "NetworkNode"), "Subnetwork");
        Require(!ReferenceEquals(electricNetwork, heatNetwork), "Thermal and electrical nodes merged into one subnetwork");
    }
    void RequireStock(object batterySource, object batteryTarget, object heatSource, object heatTarget, int sourceCells, int targetCells, int sourceHeat, int targetHeat)
    {
        Require(Count(batterySource, "Resources") == sourceCells, "Shared-port source battery mismatch");
        Require(Count(batteryTarget, "Resources") == targetCells, "Shared-port target battery mismatch");
        Require(Count(heatSource, "Resources") == sourceHeat, "Shared-port source heat mismatch");
        Require(Count(heatTarget, "Resources") == targetHeat, "Shared-port target heat mismatch");
    }

    void SharedPhysicalPortCase(int rotFlip, bool reverseOrder, bool inheritWrongToggle = false)
    {
        var manager = NewTopologyManager();
        // All parts use the same transform. A local +X chain remains adjacent.
        int rotation = rotFlip & 3;
        int sign = (rotFlip & 4) == 0 ? 1 : -1;
        var delta = rotation switch { 0 => (sign, 0), 1 => (0, sign), 2 => (-sign, 0), _ => (0, -sign) };
        NewTopologyPart(0, 0, rotFlip);
        var overclock = Component("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
        var batterySource = Storage("SourceStorage", 8000, 4500);
        var sourceView = Inline("SourceCells", batterySource);
        var heatSource = Storage("PortProbeHeatStorage", 100, 23);
        var electricInput = GraphIo(manager, "PortProbeElectricInput");
        var heatInput = GraphIo(manager, "PortProbeHeatInput");
        var electricPump = Pump(sourceView, electricInput);
        var heatPump = Pump(heatSource, heatInput);
        object sourceElectric, sourceHeat;
        if (reverseOrder) {
            sourceHeat = GraphPort("PortProbeSourceHeat");
            sourceElectric = GraphPort(inheritWrongToggle ? "PortProbeWrongElectric" : "PortProbeSourceElectric");
        } else {
            sourceElectric = GraphPort(inheritWrongToggle ? "PortProbeWrongElectric" : "PortProbeSourceElectric");
            sourceHeat = GraphPort("PortProbeSourceHeat");
        }

        NewTopologyPart(delta.Item1, delta.Item2, rotFlip);
        var electricNode = GraphNode(manager, "PortProbeElectricNode");
        var heatNode = GraphNode(manager, "PortProbeHeatNode");
        var cablePorts = new Dictionary<string, object>();
        var portTemplates = new[] { "PortProbeCableElectricLeft", "PortProbeCableElectricRight", "PortProbeCableHeatLeft", "PortProbeCableHeatRight" };
        foreach (var template in reverseOrder ? portTemplates.Reverse() : portTemplates) cablePorts.Add(template, GraphPort(template));

        NewTopologyPart(delta.Item1 * 2, delta.Item2 * 2, rotFlip);
        var batteryTarget = Storage("ReceiverStorage", 4000, 0);
        var receivingView = Receiver(batteryTarget, out var bias);
        var electricStore = GraphStore(manager, "PortProbeElectricStore", receivingView);
        var heatTarget = Storage("PortProbeHeatStorage", 100, 0);
        var heatStore = GraphStore(manager, "PortProbeHeatStore", heatTarget);
        object targetElectric, targetHeat;
        if (reverseOrder) {
            targetHeat = GraphPort("PortProbeTargetHeat");
            targetElectric = GraphPort("PortProbeTargetElectric");
        } else {
            targetElectric = GraphPort("PortProbeTargetElectric");
            targetHeat = GraphPort("PortProbeTargetHeat");
        }
        var cableElectricLeft = cablePorts["PortProbeCableElectricLeft"];
        var cableElectricRight = cablePorts["PortProbeCableElectricRight"];
        var cableHeatLeft = cablePorts["PortProbeCableHeatLeft"];
        var cableHeatRight = cablePorts["PortProbeCableHeatRight"];
        RequireSeparated(electricNode, heatNode);
        RequireSeparated(electricInput, heatInput);
        RequirePair(cableElectricRight, targetElectric, "electrical target");
        RequirePair(cableHeatRight, targetHeat, "thermal target");
        Require(!Flag(overclock, "IsToggleOn") && !Flag(sourceHeat, "IsOperational") && Peer(sourceHeat) == null, "Heat source port must start disabled without overclock");

        if (inheritWrongToggle) {
            Require(!Flag(sourceElectric, "IsOperational") && Peer(sourceElectric) == null, "Negative control must disable wrongly inherited electrical port");
            Pulse(electricPump);
            RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 4500, 0, 23, 0);
            scenarioResults.Add(new { name = "negative control: inherited overclock toggle blocks electricity", rotFlip, reverseOrder });
            Console.WriteLine("PASS: negative control detects electrical port wrongly inheriting overclock gating.");
            return;
        }

        Require(Flag(sourceElectric, "IsOperational"), "Electrical port is disabled without overclock");
        RequirePair(sourceElectric, cableElectricLeft, "electrical source without overclock");
        Require(Count(electricInput, "MaxResources") == 4 && Count(heatInput, "MaxResources") == 0, "Native queries must discover electrical capacity only while heat is off");
        Pulse(electricPump);
        Pulse(heatPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 3500, 1000, 23, 0);

        SetToggle(overclock, true);
        Require(Flag(sourceHeat, "IsOperational"), "Heat port did not register on native toggle event");
        RequirePair(sourceHeat, cableHeatLeft, "thermal source with overclock");
        RequirePair(sourceElectric, cableElectricLeft, "electrical source during overclock");
        RequireSeparated(electricInput, heatInput);
        Require(Count(electricInput, "MaxResources") == 4 && Count(heatInput, "MaxResources") == 100, "Native query capacities leaked across channel types");
        Pulse(heatPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 3500, 1000, 22, 1);
        Pulse(electricPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 2500, 2000, 22, 1);

        SetToggle(overclock, false);
        Require(Peer(sourceHeat) == null && Peer(cableHeatLeft) == null, "Disabling heat left a stale thermal connection");
        RequirePair(sourceElectric, cableElectricLeft, "electricity survives heat shutdown");
        Pulse(heatPump);
        Pulse(electricPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 1500, 3000, 22, 1);

        SetToggle(overclock, true);
        RequirePair(sourceHeat, cableHeatLeft, "thermal reconnect");
        Call(manager, "RequestPortDeregistration", cableElectricRight);
        Require(Peer(targetElectric) == null, "Electrical disconnection left a stale peer");
        RequirePair(cableHeatRight, targetHeat, "heat survives electrical removal");
        Require(Count(electricInput, "MaxResources") == 0, "Disconnected electrical capacity remains visible");
        Pulse(electricPump);
        Pulse(heatPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 1500, 3000, 21, 2);
        Call(manager, "RequestPortRegistration", cableElectricRight);
        RequirePair(cableElectricRight, targetElectric, "electrical reconnect");
        Pulse(electricPump);
        RequireStock(batterySource, batteryTarget, heatSource, heatTarget, 500, 4000, 21, 2);
        Require(Count(bias, "Resources") == 999, "Shared-port transfer changed rounding bias");
        RequireSeparated(electricStore, heatStore);
        scenarioResults.Add(new { name = "shared physical socket", rotFlip, reverseOrder, batterySource = 500, batteryTarget = 4000, heatSource = 21, heatTarget = 2 });
        Console.WriteLine($"PASS: shared socket rotFlip={rotFlip}, reverseOrder={reverseOrder}; native discovery, isolated transfers, overclock off/on/off, disconnect/reconnect.");
    }

    void SharedPhysicalPorts()
    {
        for (int rotFlip = 0; rotFlip < 8; rotFlip++)
            foreach (bool reverse in new[] { false, true }) SharedPhysicalPortCase(rotFlip, reverse);
        SharedPhysicalPortCase(0, false, inheritWrongToggle: true);
    }
}
