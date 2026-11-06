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
    /// <summary>Single-name references: Storage, ResourceStorage, FromStorage, ToStorage, ComponentID, ProxyToggle, OperationalToggle, SuppliesResourcesToggle.</summary>
    public Dictionary<string, string> Refs = new(StringComparer.Ordinal);
    /// <summary>Reference fields that are declared but do not read as one component name.</summary>
    public HashSet<string> UnreadableRefs = new(StringComparer.Ordinal);
    public bool? SuppliesResources;
    /// <summary>MultiToggle Mode, top-level Invert and Toggles entries (null when absent or unreadable).</summary>
    public string? Mode;
    public bool Invert;
    public List<(string Toggle, bool Invert)>? Entries;
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

/// <summary>
/// Classification result. <see cref="Skip"/> is the logged reason an electrical-looking part gets no adapter. <see cref="Gates"/>:
/// the part's own toggles (comma-separated) its source transfer needs besides the network switch; <see cref="Heat"/>: its overclock delivery heat producer.
/// </summary>
internal sealed record DeviceDecision(DeviceKind Kind, string? Skip = null, string? Backing = null, string? Receiver = null, string? AcceptToggle = null, string? Provider = null, string? ProviderToggle = null, string? Gates = null, string? Heat = null);

/// <summary>
/// Structural classification of a part's battery graph (pure; ported from prototypes/whole-cell-native
/// NativeDeviceCatalog.cs). Contracts, never part IDs: unique crew battery consumer -> verified local storage;
/// engine room = EnginePowerDistribution grid buff provider + battery aggregate reaching it via ViaBuffs; reactor =
/// reactor category + battery-producing converter; capacitor = provides_power + crew battery receiver, either the native
/// overclock contract (IsOverclocked + delivery heat producer) or a simple storage without overclock.
/// </summary>
internal static class DeviceClassifier
{
    public const string EngineBuff = "EnginePowerDistribution";

    /// <summary>Native overclock storage heat per crew-delivered battery (base_part_overclock power storage contract).</summary>
    public const string HeatProducer = "Overclock_DeliveryHeatProducer";

    public static DeviceDecision Classify(string[] categories, IReadOnlyDictionary<string, DeviceComponent> c, string? scanError)
    {
        DeviceComponent[] receivers = c.Values.Where(x => x.Type == "ResourceConsumer" && x.ResourceType == "battery" && x.Refs.ContainsKey("Storage")).ToArray();
        string? receiving = receivers.Length > 0 ? receivers[0].Refs["Storage"] : null;
        DeviceComponent? provider = c.Values.FirstOrDefault(x => x.Type == "GridBuffProvider" && x.BuffType == EngineBuff);
        DeviceKind kind;
        string? backing = null, receiver = null, providerToggle = null, heat = null;
        var gates = new List<string>();
        string? gateError = null;
        if (categories.Contains("reactor"))
        {
            kind = DeviceKind.Reactor;
            DeviceComponent? producer = c.Values.FirstOrDefault(x => x.Type == "ResourceConverter" && x.Refs.TryGetValue("ToStorage", out string? s) && c.TryGetValue(s, out var v) && v.ResourceType == "battery");
            if (producer == null)
            {
                return Skip(kind, scanError ?? "No native battery-producing converter target was found.");
            }
            backing = producer.Refs["ToStorage"];
            gateError = Gate(producer, "OperationalToggle", gates);
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
            backing = receiver = receiving;
            gateError = Gate(receivers[0], "OperationalToggle", gates);
            // Overclock mechanic present: keep its limits and heat via the part's own producer; absent: simple storage.
            heat = c.ContainsKey("IsOverclocked") || c.ContainsKey(HeatProducer) ? HeatProducer : null;
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
        var physicalIds = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        string? error = gateError ?? Resolve(c, backing, physicalIds, new HashSet<string>(StringComparer.Ordinal), Array.Empty<string>());
        if (error == null && receivers.Length > 1 && kind is not DeviceKind.Reactor)
        {
            error = "Multiple distinct crew battery receivers need explicit selection.";
        }
        if (error == null && kind is DeviceKind.Consumer or DeviceKind.EngineRoom && physicalIds.Count != 1)
        {
            error = "Consumer requires exactly one independent physical battery store.";
        }
        // Every component the adapter template or its overrides reference must exist on the part at all times.
        error ??= Required(kind, receiver, backing, provider?.Id, providerToggle).Concat(gates).Concat(heat != null ? new[] { "IsOverclocked", heat } : Array.Empty<string>())
            .Select(n => Static(c, n)).FirstOrDefault(m => m != null);
        if (error == null && kind is DeviceKind.Reactor or DeviceKind.Capacitor)
        {
            // A capacitor charges and discharges through one transfer gate: a discharge-only toggle cannot join it.
            error = SupplyGate(c, physicalIds, gates, kind == DeviceKind.Reactor);
        }
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
        return new DeviceDecision(kind, null, backing, receiver, accept, provider?.Id, providerToggle, gates.Count > 0 ? string.Join(", ", gates) : null, heat);
    }

    private static DeviceDecision Skip(DeviceKind kind, string reason) => new(kind, reason);

    /// <summary>Adds the toggle <paramref name="field"/> names; truly absent adds nothing, declared but unreadable is an error.</summary>
    private static string? Gate(DeviceComponent x, string field, List<string> gates)
    {
        if (x.Refs.TryGetValue(field, out string? toggle))
        {
            gates.Add(toggle);
        }
        return x.UnreadableRefs.Contains(field) ? $"{x.Id}.{field} is not a single component reference." : null;
    }

    private static string? Static(IReadOnlyDictionary<string, DeviceComponent> c, string n) => !c.TryGetValue(n, out var x) ? "Adapter needs missing component: " + n
        : x.Conditional ? "Adapter needs a component that exists only inside a ToggledComponents branch: " + n : null;

    /// <summary>
    /// Generation or charging allowed is not output allowed. The adapter reads every physical store of the backing graph,
    /// so each store needs its own crew supply permission implied by the transfer gates plus the proxy toggles on its read
    /// path: alternative suppliers of the same store are ORed (vanilla: normal vs overclock pickup), different stores never.
    /// A supplier without SuppliesResourcesToggle permits its store. Where <paramref name="canGate"/> and the graph is one
    /// store with one direct supply toggle, that toggle joins the gates; anything else is skipped.
    /// </summary>
    private static string? SupplyGate(IReadOnlyDictionary<string, DeviceComponent> c, IReadOnlyDictionary<string, string[]> physical, List<string> gates, bool canGate)
    {
        foreach (var (store, path) in physical)
        {
            // shortcut: the store itself, an aggregate listing it, or an aggregate listing a proxy of it; deeper chains are not seen.
            var terms = new List<string[]>();
            foreach (DeviceComponent x in c.Values.Where(x => x.ResourceType == "battery" && x.SuppliesResources == true))
            {
                DeviceComponent? proxy = x.ResourceStorages?.Select(m => c.GetValueOrDefault(m)).FirstOrDefault(p => p?.Type == "ResourceStorageProxy" && p.Refs.GetValueOrDefault("ComponentID") == store);
                if (x.Id != store && x.ResourceStorages?.Contains(store) != true && proxy == null)
                {
                    continue;
                }
                string[] via = Array.Empty<string>();
                if (proxy != null)
                {
                    // The supplier path must be as verified as the read path: same proxy checks, unconditional, static toggle.
                    if ((ProxyError(proxy) ?? (proxy.Conditional ? "Battery supplier proxy exists only inside a ToggledComponents branch: " + proxy.Id : null)
                        ?? (proxy.Refs.GetValueOrDefault("ProxyToggle") is { } toggle ? Static(c, toggle) : null)) is { } bad)
                    {
                        return "Unsupported battery supplier path via " + proxy.Id + ": " + bad;
                    }
                    via = proxy.Refs.GetValueOrDefault("ProxyToggle") is { } pt ? new[] { pt } : via;
                }
                if (x.Conditional)
                {
                    return "Battery supplier exists only inside a ToggledComponents branch: " + x.Id;
                }
                if (x.UnreadableRefs.Contains("SuppliesResourcesToggle"))
                {
                    return x.Id + ".SuppliesResourcesToggle is not a single component reference.";
                }
                if (x.Refs.TryGetValue("SuppliesResourcesToggle", out string? t) && Static(c, t) is { } missing)
                {
                    return missing;
                }
                terms.Add(t != null ? via.Append(t).ToArray() : via);
            }
            if (terms.Count == 0)
            {
                return "Battery store is supplied by no storage: " + store;
            }
            if (Implies(c, gates.Concat(path).ToArray(), terms) == true)
            {
                continue;
            }
            if (canGate && physical.Count == 1 && terms is [[var only]])
            {
                gates.Add(only);
                continue;
            }
            return "Supply toggles of battery store " + store + " are not implied by the transfer gates; needs explicit mapping.";
        }
        return null;
    }
    /// <summary>
    /// Whether All(<paramref name="premise"/>) implies Any over the All-terms of <paramref name="conclusion"/> in every state, evaluating local
    /// MultiToggles natively (Mode, per-entry and top-level Invert). Any other toggle, a MultiToggle with ViaBuffs inputs,
    /// unreadable entries or a branch component is an independent free input. Null when undecidable (cycle, too many inputs).
    /// </summary>
    internal static bool? Implies(IReadOnlyDictionary<string, DeviceComponent> c, IReadOnlyList<string> premise, IReadOnlyList<IReadOnlyList<string>> conclusion)
    {
        DeviceComponent? Logic(string n) => c.TryGetValue(n, out var t) && t.Type == "MultiToggle" && !t.HasViaBuffs && !t.Conditional && t.Entries != null && t.Mode is "All" or "Any" or "One" or "None" ? t : null;
        var inputs = new List<string>();
        var path = new HashSet<string>(StringComparer.Ordinal);
        bool Collect(string n)
        {
            if (Logic(n) is not { } g)
            {
                if (!inputs.Contains(n))
                {
                    inputs.Add(n);
                }
                return true;
            }
            bool ok = path.Add(n) && g.Entries!.All(e => Collect(e.Toggle));
            path.Remove(n);
            return ok;
        }
        if (!premise.Concat(conclusion.SelectMany(t => t)).All(Collect) || inputs.Count > 16)
        {
            return null;
        }
        for (int state = 0; state < 1 << inputs.Count; state++)
        {
            bool On(string n)
            {
                if (Logic(n) is not { } g)
                {
                    return (state >> inputs.IndexOf(n) & 1) == 1;
                }
                int on = g.Entries!.Count(e => On(e.Toggle) != e.Invert);
                bool v = g.Mode switch { "All" => on == g.Entries!.Count, "Any" => on > 0, "One" => on == 1, _ => on == 0 };
                return v != g.Invert;
            }
            if (premise.All(On) && !conclusion.Any(t => t.All(On)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Part components each adapter references by name (templates in mod/, overrides in <see cref="Adapter"/>), besides gates and heat.</summary>
    private static IEnumerable<string> Required(DeviceKind kind, string? receiver, string? backing, string? provider, string? providerToggle) => kind switch
    {
        DeviceKind.Consumer => new[] { receiver! },
        DeviceKind.EngineRoom => new[] { receiver!, provider!, providerToggle! },
        DeviceKind.Reactor => new[] { backing! },
        _ => new[] { receiver! },
    };

    private static string[] DirectPhysical(IReadOnlyDictionary<string, DeviceComponent> c, DeviceComponent aggregate) =>
        (aggregate.ResourceStorages ?? Array.Empty<string>()).Where(n => c.TryGetValue(n, out var x) && x.Type == "ResourceStorage" && x.ResourceType == "battery").ToArray();

    /// <summary>A battery proxy is verified only when local (PartLocation 0,0), unscaled and with a readable ProxyToggle.</summary>
    private static string? ProxyError(DeviceComponent x) =>
        x.PartLocation?.Any(v => v != 0) == true ? "Battery proxy targets another part and requires explicit local-storage mapping: " + x.Id
        : x.UnreadableRefs.Contains("ProxyToggle") ? "Battery proxy toggle is not a single component reference: " + x.Id
        : x.QuantityScaleUnreadable ? "Battery proxy scale could not be resolved: " + x.Id
        : x.QuantityScale is { } scale && scale != 1 ? "Scaled battery proxy requires explicit energy-safe mapping: " + x.Id
        : null;

    private static string? Resolve(IReadOnlyDictionary<string, DeviceComponent> c, string? name, IDictionary<string, string[]> physical, ISet<string> seen, string[] path)
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
                physical[name] = path; // proxy toggles the adapter's read passes through
                if (x.MaxResources is not { } units)
                {
                    return "Physical battery capacity could not be resolved as an integer: " + name;
                }
                return units >= 1000 && units % 1000 == 0 ? null : "Battery capacity requires a separate whole-cell mapping (not a positive multiple of 1000): " + name;
            case "ResourceStorageProxy":
                if (ProxyError(x) is { } invalid)
                {
                    return invalid;
                }
                return x.Refs.TryGetValue("ComponentID", out string? target) ? Resolve(c, target, physical, seen, x.Refs.TryGetValue("ProxyToggle", out string? pt) ? path.Append(pt).ToArray() : path) : "Dynamic multi-target battery proxy requires explicit mapping: " + name;
            case "MultiResourceStorage" when x.HasViaBuffs:
                return "Aggregate with dynamic ViaBuffs members can include other parts' storage: " + name;
            case "MultiResourceStorage" when x.ResourceStorages != null:
                foreach (string member in x.ResourceStorages)
                {
                    if (Resolve(c, member, physical, seen, path) is { } error)
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
        if (d.Kind == DeviceKind.Reactor && d.Backing != "BatteryStorage" || d.Kind == DeviceKind.Capacitor && d.Backing != "BatteryReceiverStorage")
        {
            Add("CrewlessLogisticsCells", $" : {t}/CrewlessLogisticsCells {{ FromStorage = {d.Backing} }}");
        }
        if (d.Kind == DeviceKind.Capacitor && d.Receiver != "BatteryReceiverStorage")
        {
            Add("CrewlessLogisticsReceivingAggregate", $" : {t}/CrewlessLogisticsReceivingAggregate {{ ResourceStorages = [{d.Receiver}, CrewlessLogisticsReceivingBias] }}");
        }
        if (d.Kind is DeviceKind.Reactor or DeviceKind.Capacitor && d.Gates != null)
        {
            Add("CrewlessLogisticsCanTransfer", $" : {t}/CrewlessLogisticsCanTransfer {{ Toggles = [{d.Gates}, CrewlessLogisticsToggle] }}");
        }
        if (d.Heat != null)
        {
            // Network charges skip the crew-only receiving trigger: count each network-delivered cell while overclocked
            // with the part's own per-battery heat. Converter trigger events have effect scale zero: normalize to one battery.
            Add("CrewlessLogisticsDeliveryTrigger", " { Type = MultiTrigger; OperationalToggle = IsOverclocked; Triggers = [CrewlessLogisticsCharge] }");
            Add("CrewlessLogisticsDeliveryHeat", $" : {d.Heat} {{ Trigger = CrewlessLogisticsDeliveryTrigger; Amount : ^/0/Amount {{ Modifiers [ {{ Type = ScaledEffectScale; ModificationMode = Multiply; Scale = 0; Basis = 1 }} ] }} }}");
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
