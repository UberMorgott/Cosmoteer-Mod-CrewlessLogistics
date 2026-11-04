using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CrewlessLogistics.Belts;

/// <summary>Effective (inheritance-resolved) facts of one part component that classification needs.</summary>
internal sealed class DeviceComponent
{
    public string Id = "";
    public string Type = "";
    public string? ResourceType;
    /// <summary>Single-name references: Storage, ResourceStorage, FromStorage, ToStorage, ComponentID, ProxyToggle, OperationalToggle.</summary>
    public Dictionary<string, string> Refs = new(StringComparer.Ordinal);
    public string[]? ResourceStorages;
    public int[]? PartLocation;
    public double? QuantityScale;
    public bool QuantityScaleUnreadable;
    public bool? AllowNonOperationalMinPriorityDelivery;
    public int? MaxResources;
    public string? BuffType;
    public string[]? OutgoingBuffProviders;
    /// <summary>Has a ViaBuffs block: an aggregate whose members are discovered at run time on other parts.</summary>
    public bool HasViaBuffs;
    /// <summary>Declared only inside a ToggledComponents branch: absent from the part while the branch is off.</summary>
    public bool Conditional;
}

internal enum DeviceKind
{
    None,
    Consumer,
    EngineRoom,
    Reactor,
    Capacitor,
}

/// <summary>Classification result. <see cref="Skip"/> is the logged reason an electrical-looking part gets no adapter.</summary>
internal sealed record DeviceDecision(DeviceKind Kind, string? Skip = null, string? Backing = null, string? Receiver = null, string? AcceptToggle = null, string? Provider = null, string? ProviderToggle = null);

/// <summary>
/// Structural classification of a part's battery graph (pure; ported from prototypes/whole-cell-native
/// NativeDeviceCatalog.cs). Contracts, never part IDs: unique crew battery consumer -> verified local storage;
/// engine room = EnginePowerDistribution grid buff provider + battery aggregate reaching it via ViaBuffs; reactor =
/// reactor category + battery-producing converter; capacitor = the vanilla Power Capacitor contract only.
/// </summary>
internal static class DeviceClassifier
{
    public const string EngineBuff = "EnginePowerDistribution";

    /// <summary>Components the capacitor adapter (mod/capacitor.rules) references by name.</summary>
    public static readonly string[] CapacitorContract = { "ConsumptionToggle", "BatteryReceiverStorage", "IsOverclocked", "Overclock_DeliveryHeatProducer" };

    public static DeviceDecision Classify(string[] categories, IReadOnlyDictionary<string, DeviceComponent> c, string? scanError)
    {
        DeviceComponent[] receivers = c.Values.Where(x => x.Type == "ResourceConsumer" && x.ResourceType == "battery" && x.Refs.ContainsKey("Storage")).ToArray();
        string? receiving = receivers.Length > 0 ? receivers[0].Refs["Storage"] : null;
        DeviceComponent? provider = c.Values.FirstOrDefault(x => x.Type == "GridBuffProvider" && x.BuffType == EngineBuff);
        DeviceKind kind;
        string? backing = null, receiver = null, providerToggle = null;
        if (categories.Contains("reactor"))
        {
            kind = DeviceKind.Reactor;
            backing = c.Values.Where(x => x.Type == "ResourceConverter" && x.Refs.ContainsKey("ToStorage")).Select(x => x.Refs["ToStorage"])
                .FirstOrDefault(s => c.TryGetValue(s, out var v) && v.ResourceType == "battery");
            if (backing == null)
            {
                return Skip(kind, scanError ?? "No native battery-producing converter target was found.");
            }
        }
        else if (provider != null)
        {
            kind = DeviceKind.EngineRoom;
            DeviceComponent[] aggregates = c.Values.Where(x => x.Type == "MultiResourceStorage" && x.ResourceType == "battery" && x.OutgoingBuffProviders?.Contains(provider.Id) == true).ToArray();
            string[] physical = aggregates.Length == 1 ? DirectPhysical(c, aggregates[0]) : Array.Empty<string>();
            if (physical.Length != 1 || !provider.Refs.TryGetValue("OperationalToggle", out providerToggle))
            {
                return Skip(kind, scanError ?? "Engine power distribution needs one battery aggregate via its provider with one local physical battery and a provider OperationalToggle.");
            }
            backing = receiver = physical[0];
        }
        else if (receiving != null && categories.Contains("provides_power"))
        {
            kind = DeviceKind.Capacitor;
            backing = receiving;
            string? missing = CapacitorContract.FirstOrDefault(id => !c.ContainsKey(id));
            if (missing != null || receiving != "BatteryReceiverStorage")
            {
                return Skip(kind, scanError ?? "provides_power device without the vanilla Power Capacitor contract (missing " + (missing ?? "BatteryReceiverStorage receiver") + ").");
            }
        }
        else if (receiving != null)
        {
            kind = DeviceKind.Consumer;
            backing = receiver = receiving;
            // Rocket-style extendable stores alias neighboring parts: expose the unique directly-owned physical
            // reserve once, not the aggregate's overlapping capacity or scaled proxies.
            if (c.TryGetValue(receiving, out var aggregate) && aggregate.Type == "MultiResourceStorage" && !aggregate.HasViaBuffs && aggregate.ResourceStorages is { } members)
            {
                string[] direct = DirectPhysical(c, aggregate);
                var others = members.Where(n => !direct.Contains(n)).Select(n => c.GetValueOrDefault(n)).ToArray();
                if (direct.Length == 1 && others.Length > 0 && others.All(x => x?.Type == "ResourceStorageProxy" && x.PartLocation != null))
                {
                    backing = receiver = direct[0];
                }
            }
        }
        else
        {
            return new DeviceDecision(DeviceKind.None);
        }
        if (scanError != null)
        {
            return Skip(kind, scanError);
        }
        var physicalIds = new SortedSet<string>(StringComparer.Ordinal);
        string? error = Resolve(c, backing, physicalIds, new HashSet<string>(StringComparer.Ordinal));
        if (error == null && receivers.Length > 1 && kind is DeviceKind.Consumer or DeviceKind.EngineRoom)
        {
            error = "Multiple distinct crew battery receivers need explicit selection.";
        }
        if (error == null && kind is DeviceKind.Consumer or DeviceKind.EngineRoom && physicalIds.Count != 1)
        {
            error = "Consumer requires exactly one independent physical battery store.";
        }
        // Every component the adapter template or its overrides reference must exist on the part at all times.
        error ??= Required(kind, receiver, backing, provider?.Id, providerToggle).Select(n => !c.TryGetValue(n, out var x) ? "Adapter needs missing component: " + n
            : x.Conditional ? "Adapter needs a component that exists only inside a ToggledComponents branch: " + n : null).FirstOrDefault(m => m != null);
        if (error != null)
        {
            return Skip(kind, error);
        }
        // Mirror the vanilla crew consumer: AllowNonOperationalMinPriorityDelivery = false means crew deliver only
        // while its OperationalToggle is on (hyperdrives: FtlDrive spool-up).
        string? accept = null;
        if (kind is DeviceKind.Consumer or DeviceKind.EngineRoom && receivers[0].AllowNonOperationalMinPriorityDelivery == false)
        {
            if (!receivers[0].Refs.TryGetValue("OperationalToggle", out accept) || !c.TryGetValue(accept, out var gate) || gate.Conditional)
            {
                return Skip(kind, "Crew battery consumer gates delivery on a missing or conditional OperationalToggle.");
            }
        }
        return new DeviceDecision(kind, null, backing, receiver, accept, provider?.Id, providerToggle);
    }

    private static DeviceDecision Skip(DeviceKind kind, string reason) => new(kind, reason);

    /// <summary>Part components each adapter references by name (templates in mod/, overrides in <see cref="Adapter"/>).</summary>
    private static IEnumerable<string> Required(DeviceKind kind, string? receiver, string? backing, string? provider, string? providerToggle) => kind switch
    {
        DeviceKind.Consumer => new[] { receiver! },
        DeviceKind.EngineRoom => new[] { receiver!, provider!, providerToggle! },
        DeviceKind.Reactor => new[] { backing!, "IsOperational" }, // source-components.rules CrewlessLogisticsCanTransfer
        _ => CapacitorContract,
    };

    private static string[] DirectPhysical(IReadOnlyDictionary<string, DeviceComponent> c, DeviceComponent aggregate) =>
        (aggregate.ResourceStorages ?? Array.Empty<string>()).Where(n => c.TryGetValue(n, out var x) && x.Type == "ResourceStorage" && x.ResourceType == "battery").ToArray();

    private static string? Resolve(IReadOnlyDictionary<string, DeviceComponent> c, string? name, ISet<string> physical, ISet<string> seen)
    {
        if (name == null || !c.TryGetValue(name, out var x))
        {
            return "Referenced battery storage is absent: " + name;
        }
        if (!seen.Add(name))
        {
            return "Cyclic or overlapping storage alias: " + name;
        }
        if (x.ResourceType != "battery")
        {
            return "Storage is not standard battery resource: " + name;
        }
        switch (x.Type)
        {
            case "ResourceStorage":
                physical.Add(name);
                if (x.MaxResources is not { } units)
                {
                    return "Physical battery capacity could not be resolved as an integer: " + name;
                }
                return units >= 1000 && units % 1000 == 0 ? null : "Battery capacity requires a separate whole-cell mapping (not a positive multiple of 1000): " + name;
            case "ResourceStorageProxy":
                if (x.PartLocation?.Any(v => v != 0) == true)
                {
                    return "Receiving proxy targets another part and requires explicit local-storage mapping: " + name;
                }
                if (x.QuantityScaleUnreadable)
                {
                    return "Battery proxy scale could not be resolved: " + name;
                }
                if (x.QuantityScale is { } scale && scale != 1)
                {
                    return "Scaled battery proxy requires explicit energy-safe mapping: " + name;
                }
                return x.Refs.TryGetValue("ComponentID", out string? target) ? Resolve(c, target, physical, seen) : "Dynamic multi-target battery proxy requires explicit mapping: " + name;
            case "MultiResourceStorage" when x.HasViaBuffs:
                return "Aggregate with dynamic ViaBuffs members can include other parts' storage: " + name;
            case "MultiResourceStorage" when x.ResourceStorages != null:
                foreach (string member in x.ResourceStorages)
                {
                    if (Resolve(c, member, physical, seen) is { } error)
                    {
                        return error;
                    }
                }
                return null;
            default:
                return "Unsupported battery storage type: " + x.Type + " (" + name + ")";
        }
    }

    /// <summary>
    /// Adapter for a classified part: the template group (relative to the mod folder) and the local children added to
    /// the part's own Components overlay, in fixed order. <paramref name="path"/> maps a mod-relative rules path to the
    /// absolute OT path the game root resolves. Overrides inherit explicit template children, never <c>^/0</c>: the
    /// overlay's own inheritance index 0 is the part's original base.
    /// </summary>
    public static (string Template, List<(string Name, string Text)> Children) Adapter(DeviceDecision d, IReadOnlyList<PortEdge> ports, Func<string, string> path)
    {
        string template = d.Kind switch
        {
            DeviceKind.Consumer => "<consumer_base.rules>/Components",
            DeviceKind.EngineRoom => "<engine_room_power.rules>/Components",
            DeviceKind.Reactor => "<source-components.rules>/Components",
            DeviceKind.Capacitor => "<capacitor.rules>/Components",
            _ => throw new ArgumentException("No adapter for " + d.Kind),
        };
        string t = path(template);
        var children = new List<(string, string)>();
        void Add(string name, string body) => children.Add((name, name + body));
        if (d.Kind is DeviceKind.Consumer or DeviceKind.EngineRoom && d.Receiver != "BatteryStorage")
        {
            Add("CrewlessLogisticsReceivingAggregate", $" : {t}/CrewlessLogisticsReceivingAggregate {{ ResourceStorages = [{d.Receiver}, CrewlessLogisticsReceivingBias] }}");
        }
        if (d.Kind == DeviceKind.EngineRoom)
        {
            if (d.Backing != "BatteryStorage")
            {
                Add("CrewlessLogisticsEngineRoomSupplyCells", $" : {path("<common.rules>/CellView")} {{ FromStorage = {d.Backing} }}");
            }
            if (d.Provider != "PowerDistributionBuffProvider")
            {
                Add("CrewlessLogisticsEngineRoomRecipients", $" : {t}/CrewlessLogisticsEngineRoomRecipients {{ ViaBuffs {{ OutgoingBuffProviders = [{d.Provider}]; ComponentIDs = [CrewlessLogisticsRoomInlet] }} }}");
            }
            if (d.ProviderToggle != "DistributePowerToggle")
            {
                Add("CrewlessLogisticsEngineRoomCanDistribute", $" : {t}/CrewlessLogisticsEngineRoomCanDistribute {{ Toggles = [CrewlessLogisticsToggle, {d.ProviderToggle}] }}");
            }
        }
        if (d.Kind == DeviceKind.Reactor && d.Backing != "BatteryStorage")
        {
            Add("CrewlessLogisticsCells", $" : {t}/CrewlessLogisticsCells {{ FromStorage = {d.Backing} }}");
        }
        if (d.AcceptToggle != null)
        {
            Add("CrewlessLogisticsAcceptGate", $" {{ Type = MultiToggle; Mode = All; Toggles = [CrewlessLogisticsToggle, {d.AcceptToggle}] }}");
            Add("CrewlessLogisticsReceiver", $" : {t}/CrewlessLogisticsReceiver {{ OperationalToggle = CrewlessLogisticsAcceptGate }}");
            Add("CrewlessLogisticsRoomInlet", $" : {t}/CrewlessLogisticsRoomInlet {{ ProxyToggle = CrewlessLogisticsAcceptGate }}");
        }
        string port = path(d.Kind is DeviceKind.Consumer or DeviceKind.EngineRoom ? "<common.rules>/ConsumerElectricPort" : "<common.rules>/BusElectricPort");
        foreach (PortEdge e in ports)
        {
            Add(e.Name, string.Create(CultureInfo.InvariantCulture, $" : {port} {{ Location = [{e.X}, {e.Y}]; Direction = {e.Direction} }}"));
        }
        return (t, children);
    }
}
