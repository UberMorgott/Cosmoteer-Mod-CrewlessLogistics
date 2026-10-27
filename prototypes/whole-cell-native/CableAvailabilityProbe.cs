using System.Collections;

sealed partial class NativeProbe
{
    void CableAvailabilityAndGraphics(object root, string reactorPrefix)
    {
        var modDirectory = Path.Combine(projectRoot, "mod");
        object OpenCable(string name) => Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), Path.Combine(modDirectory, name))!;
        var cableFile = OpenCable("cable.rules");
        var graphicsFile = OpenCable("cable_graphics.rules");
        foreach (var prefix in new[] { "CrossingComponents/", "AdaptiveComponents/" }) {
            foreach (var name in new[] { "CableBody", "CableGlow", prefix.StartsWith("Crossing") ? "Blueprints" : "CableBlueprints" })
                Rules(prefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", graphicsFile);
        }
        var manager = NewTopologyManager();
        NewTopologyPart(0, 0, 0);
        SetField(Property(part, "Rules"), "Size", Vector(2, 2));
        object ReadSource(string name) => Rules(reactorPrefix + name, "Cosmoteer.Ships.Parts.PartComponentRules", root);
        object AddSource(string name, bool resourceIo = false)
        {
            var result = Component(ReadSource(name));
            if (resourceIo) { InitializeLinks(result); RegisterNode(manager, result); } else Call(result, "OnAddedToPart", part);
            return result;
        }
        var sourceToggleRules = ReadSource("CrewlessLogisticsToggle");
        SetField(sourceToggleRules, "RememberValue", false);
        var sourceToggle = Component(sourceToggleRules);
        Call(sourceToggle, "OnAddedToPart", part);
        var operational = Component("PortProbeOverclock", "Cosmoteer.Ships.Parts.Logic.PartDebugToggleRules");
        SetToggle(operational, true);
        var sourceGateRules = ReadSource("CrewlessLogisticsCanTransfer");
        // MultiToggle stores toggle-reference rules rather than IDs; map the named
        // native operational component using the ID type from the source dictionary.
        var componentIdType = Field(ReadSource("CrewlessLogisticsToggle").GetType(), "ID").FieldType;
        componentMap[Id(componentIdType, "IsOperational")] = operational;
        var sourceGate = Component(sourceGateRules);
        Call(sourceGate, "OnAddedToPart", part);
        var sourceBattery = Storage("SourceStorage", 8000, 999);
        var cellRules = ReadSource("CrewlessLogisticsCells");
        componentMap[Field(cellRules.GetType(), "FromStorage").GetValue(cellRules)!] = sourceBattery;
        var cells = Component(cellRules);
        InitializeLinks(cells);
        var sourceNode = Component(ReadSource("CrewlessLogisticsNode"));
        RegisterNode(manager, sourceNode);
        AddSource("CrewlessLogisticsInput", true);
        var sourceStore = AddSource("CrewlessLogisticsSource");
        AddSource("CrewlessLogisticsSourceRouter");
        var sourcePortRules = ReadSource(Socket("Right", 1, 0));
        var sourcePort = Component(sourcePortRules);
        Call(sourcePort, "OnAddedToPart", part);

        var observations = new List<object>();
        var cablePorts = new List<(object Left, object Right)>();
        foreach (int x in new[] { 2, 3 }) {
            NewTopologyPart(x, 0, 0);
            object ReadCable(string name) => Rules("Part/Components/" + name, "Cosmoteer.Ships.Parts.PartComponentRules", cableFile);
            var node = Component(ReadCable("CrewlessLogisticsNode"));
            RegisterNode(manager, node);
            var signal = Component(ReadCable("CrewlessLogisticsAvailable"));
            InitializeLinks(signal);
            RegisterNode(manager, signal);
            var router = Component(ReadCable("CrewlessLogisticsAvailabilityRouter"));
            Call(router, "OnAddedToPart", part);
            var left = Component(ReadCable("Port_Left"));
            Call(left, "OnAddedToPart", part);
            var right = Component(ReadCable("Port_Right"));
            Call(right, "OnAddedToPart", part);
            observations.Add(signal);
            cablePorts.Add((left, right));
        }
        void RequireVoltage(bool on, int cellsAvailable, string context)
        {
            foreach (var signal in observations) {
                Require(Flag(signal, "IsToggleOn") == on, "Cable voltage toggle mismatch: " + context);
                Require(Count(signal, "Resources") == cellsAvailable, "Cable source availability mismatch: " + context);
            }
        }
        RequireVoltage(false, 0, "default disabled");
        SetProperty(sourceToggle, "Mode", 1);
        RequirePair(sourcePort, cablePorts[0].Left, "source cable voltage");
        RequirePair(cablePorts[0].Right, cablePorts[1].Left, "continuous cable voltage");
        RequireVoltage(false, 0, "999 units is below one cell");
        Add(sourceBattery, 1);
        RequireVoltage(true, 1, "one complete cell");
        for (int i = 0; i < 100; i++) RequireVoltage(true, 1, "idle query preserves energy");
        Require(Count(sourceBattery, "Resources") == 1000, "Voltage indication consumed or created energy");
        SetProperty(sourceToggle, "Mode", 0);
        RequireVoltage(false, 0, "source switch off");
        Require(Count(sourceBattery, "Resources") == 1000, "Source shutdown discarded stored energy");
        SetProperty(sourceToggle, "Mode", 1);
        RequireVoltage(true, 1, "source reconnect");
        Add(sourceBattery, -1);
        RequireVoltage(false, 0, "last full cell becomes fractional");
        Add(sourceBattery, 1001);
        RequireVoltage(true, 2, "two whole cells");
        SetToggle(operational, false);
        RequireVoltage(false, 0, "source operational shutdown");
        SetToggle(operational, true);
        RequireVoltage(true, 2, "source operation restored");
        var severedPort = cablePorts[1].Left;
        var severedPart = Property(severedPort, "Part");
        Call(severedPort, "OnRemovedFromPart", severedPart);
        Require(Flag(observations[0], "IsToggleOn") && !Flag(observations[1], "IsToggleOn"), "Breaking cable topology did not isolate the downstream voltage indication");
        Require(Count(observations[0], "Resources") == 2 && Count(observations[1], "Resources") == 0, "Disconnected cable retained remote source availability");
        Call(severedPort, "OnAddedToPart", severedPart);
        RequireVoltage(true, 2, "cable topology reconnect");
        Require(Count(sourceBattery, "Resources") == 2000, "Availability queries changed source reserve");
        scenarioResults.Add(new { name = "native cable source voltage events", segments = 2, states = "disabled,999,1000,off,on,999,2000,unavailable,available", idleQueries = 100, finalReserve = 2000, rendered = false });
        Console.WriteLine("PASS: native cable graphics rules deserialize; actual topology/resource events light both segments only for available full source cells without energy transfer.");
    }
}










