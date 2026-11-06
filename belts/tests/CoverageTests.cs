using CrewlessLogistics.Belts;
using Xunit;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>Pure coverage logic: perimeter sockets (cases ported from the retired PowerShell perimeter self-test) and structural classification.</summary>
public class CoverageTests
{
    static readonly string[] Default = { "default" };

    static List<PortEdge> Edges(int w, int h, CellRect? physical = null, (string, CellRect)[]? rects = null, int above = 0, int below = 0, int left = 0, int right = 0, string[]? prohibits = null, string[]? cableBy = null)
    {
        var all = new List<(string, CellRect)>(rects ?? Array.Empty<(string, CellRect)>());
        all.AddRange(PortGeometry.SideRects(w, h, left, right, above, below, prohibits ?? Default));
        return PortGeometry.Perimeter(w, h, physical, all, cableBy ?? Default);
    }

    [Theory]
    [InlineData("square", 8)]
    [InlineData("small laser", 5)]
    [InlineData("thruster", 6)]
    [InlineData("category-filtered custom part", 9)]
    public void PerimeterCountsAndRotationMirrorInvariance(string name, int expected)
    {
        var (w, h, physical, rects) = Case(name);
        var edges = PortGeometry.Perimeter(w, h, physical, rects, Default);
        Assert.Equal(expected, edges.Count);
        Assert.Equal(edges.Count, edges.Distinct().Count());
        Assert.All(edges, e => Assert.False(new CellRect(0, 0, w, h).Contains(Neighbor(e).X, Neighbor(e).Y)));
        for (int rot = 0; rot < 4; rot++)
        {
            foreach (bool flip in new[] { false, true })
            {
                int tw = rot % 2 == 1 ? h : w, th = rot % 2 == 1 ? w : h;
                CellRect? tp = physical is { } p ? TransformRect(p, w, h, rot, flip) : null;
                var tr = rects.Select(x => (x.Category, TransformRect(x.Rect, w, h, rot, flip))).ToList();
                var actual = PortGeometry.Perimeter(tw, th, tp, tr, Default).Select(Key).Order().ToList();
                var want = edges.Select(e =>
                {
                    var loc = TransformCell(e.X, e.Y, w, h, rot, flip);
                    var n = Neighbor(e);
                    var nb = TransformCell(n.X, n.Y, w, h, rot, flip);
                    int dx = nb.X - loc.X, dy = nb.Y - loc.Y;
                    return Key(new PortEdge(loc.X, loc.Y, dx == -1 ? "Left" : dx == 1 ? "Right" : dy == -1 ? "Up" : "Down"));
                }).Order().ToList();
                Assert.Equal(want, actual);
            }
        }
    }

    /// <summary>Side prohibitions expanded to rectangles first, as PartRules does, so rotation transforms them like explicit rects.</summary>
    static (int W, int H, CellRect? Physical, List<(string Category, CellRect Rect)> Rects) Case(string name) => name switch
    {
        "square" => (2, 2, null, new()),
        "small laser" => (1, 3, new CellRect(0, 1, 1, 2), PortGeometry.SideRects(1, 3, 0, 0, 1, 0, Default)),
        "thruster" => (2, 3, new CellRect(0, 0, 2, 2), PortGeometry.SideRects(2, 3, 0, 0, 0, 7, Default)),
        _ => (3, 2, null, new() { ("default", new CellRect(-1, 0, 1, 1)), ("special", new CellRect(3, 0, 1, 2)) }),
    };

    static string Key(PortEdge e) => $"{e.X},{e.Y}/{e.Direction}";

    static (int X, int Y) Neighbor(PortEdge e) => e.Direction switch
    {
        "Up" => (e.X, e.Y - 1),
        "Down" => (e.X, e.Y + 1),
        "Left" => (e.X - 1, e.Y),
        _ => (e.X + 1, e.Y),
    };

    static (int X, int Y) TransformCell(int x, int y, int w, int h, int rot, bool flip)
    {
        if (flip)
        {
            x = w - 1 - x;
        }
        return rot switch
        {
            0 => (x, y),
            1 => (h - 1 - y, x),
            2 => (w - 1 - x, h - 1 - y),
            _ => (y, w - 1 - x),
        };
    }

    static CellRect TransformRect(CellRect r, int w, int h, int rot, bool flip)
    {
        var pts = new[] { (r.X, r.Y), (r.X + r.W - 1, r.Y), (r.X, r.Y + r.H - 1), (r.X + r.W - 1, r.Y + r.H - 1) }.Select(p => TransformCell(p.Item1, p.Item2, w, h, rot, flip)).ToList();
        int x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y);
        return new CellRect(x0, y0, pts.Max(p => p.X) - x0 + 1, pts.Max(p => p.Y) - y0 + 1);
    }

    [Fact]
    public void CableProhibitionCategoriesAndSides()
    {
        var special = new[] { ("special", new CellRect(0, -1, 2, 1)) };
        Assert.Equal(6, Edges(2, 2, rects: special, cableBy: new[] { "special" }).Count);
        Assert.Equal(8, Edges(2, 2, rects: special, cableBy: Array.Empty<string>()).Count);
        Assert.Empty(Edges(2, 2, left: 1, right: 2, above: 3, below: 4));
    }

    [Fact]
    public void InvalidGeometryRejected()
    {
        Assert.Throws<ArgumentException>(() => Edges(2, 2, physical: new CellRect(1, 0, 2, 2)));
        Assert.Throws<ArgumentException>(() => PortGeometry.SideRects(2, 2, 0, 0, 1, 0, null));
        Assert.Throws<ArgumentException>(() => Edges(0, 2));
    }

    // ---- classification ----

    static DeviceComponent C(string id, string type, string? resource = "battery", int? max = null, string[]? storages = null, int[]? at = null, double? scale = null, params (string, string)[] refs)
    {
        var c = new DeviceComponent { Id = id, Type = type, ResourceType = resource, MaxResources = max, ResourceStorages = storages, PartLocation = at, QuantityScale = scale };
        foreach (var (k, v) in refs)
        {
            c.Refs[k] = v;
        }
        return c;
    }

    static DeviceDecision Classify(string[] categories, params DeviceComponent[] c) => DeviceClassifier.Classify(categories, c.ToDictionary(x => x.Id), null);

    static DeviceComponent Consumer(string storage) => C("BatteryConsumer", "ResourceConsumer", refs: ("Storage", storage));

    [Fact]
    public void ConsumerUsesUniqueVerifiedLocalStorage()
    {
        var d = Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 2000), Consumer("BatteryStorage"));
        Assert.Equal(new DeviceDecision(DeviceKind.Consumer, null, "BatteryStorage", "BatteryStorage"), d);
        Assert.Equal(DeviceKind.None, Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 2000)).Kind);
        Assert.NotNull(Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 1500), Consumer("BatteryStorage")).Skip);
        Assert.NotNull(Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 2000), Consumer("BatteryStorage"), C("Second", "ResourceConsumer", refs: ("Storage", "BatteryStorage"))).Skip);
        Assert.NotNull(Classify(Array.Empty<string>(), C("S", "ResourceStorage", max: 2000), C("P", "ResourceStorageProxy", scale: 2, refs: ("ComponentID", "S")), Consumer("P")).Skip);
        var unreadable = C("P", "ResourceStorageProxy", refs: ("ComponentID", "S"));
        unreadable.QuantityScaleUnreadable = true;
        Assert.NotNull(Classify(Array.Empty<string>(), C("S", "ResourceStorage", max: 2000), unreadable, Consumer("P")).Skip);
        Assert.NotNull(DeviceClassifier.Classify(Array.Empty<string>(), new[] { C("S", "ResourceStorage", max: 2000), Consumer("S") }.ToDictionary(x => x.Id), "bad schema").Skip);
    }

    [Fact]
    public void AcceptGateMirrorsCrewDeliveryRule()
    {
        var consumer = Consumer("BatteryStorage");
        consumer.AllowNonOperationalMinPriorityDelivery = false;
        consumer.Refs["OperationalToggle"] = "FtlDrive";
        var d = Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 6000), consumer, C("FtlDrive", "ModeToggle", null));
        Assert.Equal("FtlDrive", d.AcceptToggle);
        var (_, children) = DeviceClassifier.Adapter(d, new[] { new PortEdge(0, 0, "Up") }, p => p);
        Assert.Equal(new[] { "CrewlessLogisticsAcceptGate", "CrewlessLogisticsReceiver", "CrewlessLogisticsRoomInlet", "CrewlessLogisticsPort_Up_0_0" }, children.Select(c => c.Name));
        Assert.Contains("OperationalToggle = CrewlessLogisticsAcceptGate", children[1].Text);
        Assert.StartsWith("CrewlessLogisticsReceiver : <consumer_base.rules>/Components/CrewlessLogisticsReceiver", children[1].Text);
        Assert.Equal("CrewlessLogisticsPort_Up_0_0 : <common.rules>/ConsumerElectricPort { Location = [0, 0]; Direction = Up }", children[3].Text);
    }

    [Fact]
    public void RocketAggregateCollapsesToItsOneDirectReserve()
    {
        DeviceComponent[] rocket =
        {
            C("LocalBatteryStorage", "ResourceStorage", max: 2000),
            C("LeftBatteryStorage", "ResourceStorageProxy", at: new[] { -1, 0 }, refs: ("ComponentID", "LocalBatteryStorage")),
            C("ExtendableBatteryStorage", "MultiResourceStorage", storages: new[] { "LocalBatteryStorage", "LeftBatteryStorage" }),
            Consumer("ExtendableBatteryStorage"),
        };
        var d = Classify(Array.Empty<string>(), rocket);
        Assert.Equal((DeviceKind.Consumer, null, "LocalBatteryStorage", "LocalBatteryStorage"), (d.Kind, d.Skip, d.Backing, d.Receiver));
        var (template, children) = DeviceClassifier.Adapter(d, Array.Empty<PortEdge>(), p => p);
        Assert.Equal("<consumer_base.rules>/Components", template);
        Assert.Equal("CrewlessLogisticsReceivingAggregate : <consumer_base.rules>/Components/CrewlessLogisticsReceivingAggregate { ResourceStorages = [LocalBatteryStorage, CrewlessLogisticsReceivingBias] }", children.Single().Text);
        // Ambiguous: two direct reserves -> no collapse; the neighbor proxy then fails local-storage verification.
        rocket[2] = C("ExtendableBatteryStorage", "MultiResourceStorage", storages: new[] { "LocalBatteryStorage", "Other", "LeftBatteryStorage" });
        Assert.NotNull(Classify(Array.Empty<string>(), rocket.Append(C("Other", "ResourceStorage", max: 1000)).ToArray()).Skip);
    }

    [Fact]
    public void EngineRoomContractIsStructural()
    {
        DeviceComponent[] room =
        {
            C("Reserve", "ResourceStorage", max: 4000),
            C("Hub", "GridBuffProvider", null, refs: ("OperationalToggle", "CanShare")),
            C("Combined", "MultiResourceStorage", storages: new[] { "Reserve" }),
            Consumer("Reserve"),
            C("CanShare", "MultiToggle", null),
        };
        room[1].BuffType = DeviceClassifier.EngineBuff;
        room[2].OutgoingBuffProviders = new[] { "Hub" };
        var d = Classify(Array.Empty<string>(), room);
        Assert.Equal((DeviceKind.EngineRoom, (string?)null, "Reserve", "Hub", "CanShare"), (d.Kind, d.Skip, d.Backing, d.Provider, d.ProviderToggle));
        var names = DeviceClassifier.Adapter(d, Array.Empty<PortEdge>(), p => p).Children.Select(c => c.Name);
        Assert.Equal(new[] { "CrewlessLogisticsReceivingAggregate", "CrewlessLogisticsEngineRoomSupplyCells", "CrewlessLogisticsEngineRoomRecipients", "CrewlessLogisticsEngineRoomCanDistribute" }, names);
        room[2].OutgoingBuffProviders = null;
        Assert.NotNull(Classify(Array.Empty<string>(), room).Skip);
    }

    /// <summary>Codex review regressions: conditional branch members, dynamic ViaBuffs pools, and the reactor's own producer toggle.</summary>
    [Fact]
    public void AdapterReferencesMustBeStaticAndPresent()
    {
        var nested = C("BatteryStorage", "ResourceStorage", max: 2000);
        nested.Conditional = true;
        Assert.Contains("ToggledComponents", Classify(Array.Empty<string>(), nested, Consumer("BatteryStorage")).Skip);
        var conditionalConsumer = Consumer("BatteryStorage");
        conditionalConsumer.Conditional = true; // vanilla thrusters: the crew consumer sits in a branch, the adapter never references it
        Assert.Null(Classify(Array.Empty<string>(), C("BatteryStorage", "ResourceStorage", max: 2000), conditionalConsumer).Skip);
        var pooled = C("Combined", "MultiResourceStorage", storages: new[] { "Reserve" });
        pooled.HasViaBuffs = true;
        pooled.OutgoingBuffProviders = new[] { "Provider" };
        var d = Classify(Array.Empty<string>(), C("Reserve", "ResourceStorage", max: 2000), pooled, Consumer("Combined"));
        Assert.Equal(DeviceKind.Consumer, d.Kind);
        Assert.Contains("ViaBuffs", d.Skip);
        // A reactor's producer gate must be its own present, unconditional toggle.
        var gate = C("IsOperational", "MultiToggle", null);
        gate.Conditional = true;
        Assert.Contains("ToggledComponents branch: IsOperational", Classify(new[] { "reactor" }, Reactor(("OperationalToggle", "IsOperational")).Append(gate).ToArray()).Skip);
        Assert.Contains("missing component: Gone", Classify(new[] { "reactor" }, Reactor(("OperationalToggle", "Gone"))).Skip);
        var unreadable = Reactor();
        unreadable[1].UnreadableRefs.Add("OperationalToggle");
        Assert.Contains("not a single component reference", Classify(new[] { "reactor" }, unreadable).Skip);
    }

    /// <summary>Battery store + producer; <paramref name="producerRefs"/> beyond ToStorage.</summary>
    static DeviceComponent[] Reactor(params (string, string)[] producerRefs)
    {
        var store = C("BatteryStorage", "ResourceStorage", max: 3000);
        store.SuppliesResources = true;
        return new[] { store, C("Producer", "ResourceConverter", null, refs: producerRefs.Append(("ToStorage", "BatteryStorage")).ToArray()) };
    }

    static string Text(DeviceDecision d, string name) => DeviceClassifier.Adapter(d, Array.Empty<PortEdge>(), p => p).Children.Single(c => c.Name == name).Text;

    [Fact]
    public void ReactorGatesOnItsOwnToggles()
    {
        var vanilla = Classify(new[] { "reactor", "provides_power" }, Reactor(("OperationalToggle", "IsOperational")).Append(C("IsOperational", "MultiToggle", null)).ToArray());
        Assert.Equal((DeviceKind.Reactor, (string?)null, "BatteryStorage", "IsOperational"), (vanilla.Kind, vanilla.Skip, vanilla.Backing, vanilla.Gates));
        Assert.Equal("CrewlessLogisticsCanTransfer : <source-components.rules>/Components/CrewlessLogisticsCanTransfer { Toggles = [IsOperational, CrewlessLogisticsToggle] }", Text(vanilla, "CrewlessLogisticsCanTransfer"));
        // No producer toggle at all (Extended Tech Tree reactors): the network switch alone, template default.
        var bare = Classify(new[] { "reactor" }, Reactor());
        Assert.Equal((DeviceKind.Reactor, (string?)null, (string?)null), (bare.Kind, bare.Skip, bare.Gates));
        Assert.Empty(DeviceClassifier.Adapter(bare, Array.Empty<PortEdge>(), p => p).Children);
        Assert.NotNull(Classify(new[] { "reactor" }, C("BatteryStorage", "ResourceStorage", max: 3000)).Skip);
        // A store nobody may pick up from never supplies the network either.
        var silent = Reactor();
        silent[0].SuppliesResources = null;
        Assert.Contains("supplied by no storage", Classify(new[] { "reactor" }, silent).Skip);
    }

    static DeviceComponent Logic(string id, string mode, params (string Toggle, bool Invert)[] entries)
    {
        var t = C(id, "MultiToggle", null);
        t.Mode = mode;
        t.Entries = entries.ToList();
        return t;
    }

    static DeviceComponent Supplier(string id, string type, string? toggle, params string[] members)
    {
        var s = C(id, type, max: type == "ResourceStorage" ? 2000 : null, storages: members.Length > 0 ? members : null);
        s.SuppliesResources = true;
        if (toggle != null)
        {
            s.Refs["SuppliesResourcesToggle"] = toggle;
        }
        return s;
    }

    [Fact]
    public void ReactorRespectsSupplyToggles()
    {
        DeviceComponent[] Parts(string? storeToggle, params DeviceComponent[] extra)
        {
            var p = Reactor(("OperationalToggle", "IsOperational"));
            p[0] = Supplier("BatteryStorage", "ResourceStorage", storeToggle);
            return p.Append(C("IsOperational", "UIToggle", null)).Concat(extra).ToArray();
        }
        string?[] R(DeviceDecision d) => new[] { d.Skip == null ? null : "skip", d.Gates };
        Assert.Equal("IsOperational, Discharge", Classify(new[] { "reactor" }, Parts("Discharge", C("Discharge", "UIToggle", null))).Gates);
        Assert.Contains("missing component: Discharge", Classify(new[] { "reactor" }, Parts("Discharge")).Skip);
        // Vanilla large reactor: normal pickup while not overclocked, the overclock provider aliasing the same store otherwise.
        var provider = Supplier("Overclock_BatteryProviderStorage", "MultiResourceStorage", "IsOverclocked", "BatteryStorage");
        var oc = C("IsOverclocked", "UIToggle", null);
        Assert.Equal(new string?[] { null, "IsOperational" }, R(Classify(new[] { "reactor" }, Parts("Normal", provider, Logic("Normal", "None", ("IsOverclocked", false)), oc))));
        // Codex P2 1: Invert (entry or top level) or ViaBuffs inputs break the complement; never drop the gate then.
        Assert.Contains("not implied", Classify(new[] { "reactor" }, Parts("Normal", provider, Logic("Normal", "None", ("IsOverclocked", true)), oc)).Skip);
        var inverted = Logic("Normal", "None", ("IsOverclocked", false));
        inverted.Invert = true;
        Assert.Contains("not implied", Classify(new[] { "reactor" }, Parts("Normal", provider, inverted, oc)).Skip);
        var buffed = Logic("Normal", "None", ("IsOverclocked", false));
        buffed.HasViaBuffs = true;
        Assert.Contains("not implied", Classify(new[] { "reactor" }, Parts("Normal", provider, buffed, oc)).Skip);
        Assert.Contains("not implied", Classify(new[] { "reactor" }, Parts("Normal", provider, Logic("Normal", "All", ("IsOverclocked", false)), oc)).Skip);
        // Codex P2 2: a branch supplier never lifts or completes the permanent store's gate.
        var branchFree = Supplier("Extra", "MultiResourceStorage", null, "BatteryStorage");
        branchFree.Conditional = true;
        Assert.Contains("ToggledComponents branch: Extra", Classify(new[] { "reactor" }, Parts("Discharge", C("Discharge", "UIToggle", null), branchFree)).Skip);
        provider.Conditional = true;
        Assert.Contains("ToggledComponents branch: Overclock_BatteryProviderStorage", Classify(new[] { "reactor" }, Parts("Normal", provider, Logic("Normal", "None", ("IsOverclocked", false)), oc)).Skip);
        var branchToggle = Logic("Normal", "None", ("IsOverclocked", false));
        branchToggle.Conditional = true;
        provider.Conditional = false;
        Assert.Contains("ToggledComponents branch: Normal", Classify(new[] { "reactor" }, Parts("Normal", provider, branchToggle, oc)).Skip);
    }

    [Fact]
    public void CapacitorContracts()
    {
        // Vanilla Power Capacitor: normal and overclock pickup toggles, each ConsumptionToggle and an overclock state.
        var capacitor = new List<DeviceComponent>
        {
            Supplier("BatteryStorage", "ResourceStorage", "PowerToggleNonOverclocked"),
            C("BatteryReceiverStorage", "MultiResourceStorage", storages: new[] { "BatteryStorage" }),
            Supplier("Overclock_BatteryProviderStorage", "MultiResourceStorage", "PowerToggleOverclocked", "BatteryStorage"),
            Consumer("BatteryReceiverStorage"),
            C("IsOverclocked", "UIToggle", null),
            Logic("ConsumptionToggle", "All", ("PowerToggle", false), ("ScorchedToggle", false)),
            Logic("PowerToggleNonOverclocked", "All", ("ConsumptionToggle", false), ("IsOverclocked", true)),
            Logic("PowerToggleOverclocked", "All", ("ConsumptionToggle", false), ("IsOverclocked", false)),
        };
        capacitor[3].Refs["OperationalToggle"] = "ConsumptionToggle";
        // Overclock mechanic without its delivery heat producer: skipped.
        Assert.Contains("missing component: Overclock_DeliveryHeatProducer", Classify(new[] { "provides_power" }, capacitor.ToArray()).Skip);
        capacitor.Add(C(DeviceClassifier.HeatProducer, "ResourceChange", null));
        var vanilla = Classify(new[] { "provides_power" }, capacitor.ToArray());
        Assert.Equal(new DeviceDecision(DeviceKind.Capacitor, null, "BatteryReceiverStorage", "BatteryReceiverStorage", Gates: "ConsumptionToggle", Heat: DeviceClassifier.HeatProducer), vanilla);
        Assert.Equal(new[] { "CrewlessLogisticsCanTransfer", "CrewlessLogisticsDeliveryTrigger", "CrewlessLogisticsDeliveryHeat" }, DeviceClassifier.Adapter(vanilla, Array.Empty<PortEdge>(), p => p).Children.Select(c => c.Name));
        Assert.StartsWith("CrewlessLogisticsDeliveryHeat : Overclock_DeliveryHeatProducer { Trigger = CrewlessLogisticsDeliveryTrigger; Amount : ^/0/Amount", Text(vanilla, "CrewlessLogisticsDeliveryHeat"));
        // Simple storage (Extended Tech Tree _q/a parts): consumer straight into a physical store, no overclock.
        var simpleConsumer = Consumer("BatteryStorage");
        simpleConsumer.Refs["OperationalToggle"] = "PowerToggle";
        var simple = Classify(new[] { "provides_power" }, Supplier("BatteryStorage", "ResourceStorage", "PowerToggle"), simpleConsumer, C("PowerToggle", "UIToggle", null));
        Assert.Equal((DeviceKind.Capacitor, (string?)null, "PowerToggle", (string?)null), (simple.Kind, simple.Skip, simple.Gates, simple.Heat));
        Assert.Equal(new[] { "CrewlessLogisticsCells", "CrewlessLogisticsReceivingAggregate", "CrewlessLogisticsCanTransfer" }, DeviceClassifier.Adapter(simple, Array.Empty<PortEdge>(), p => p).Children.Select(c => c.Name));
        Assert.Contains("ResourceStorages = [BatteryStorage, CrewlessLogisticsReceivingBias]", Text(simple, "CrewlessLogisticsReceivingAggregate"));
        Assert.Contains("missing component: PowerToggle", Classify(new[] { "provides_power" }, Supplier("BatteryStorage", "ResourceStorage", "PowerToggle"), simpleConsumer).Skip);
        // Codex P2 3: may charge but must not discharge (supply toggle beyond the consumer gate): skipped, never discharged.
        Assert.Contains("not implied", Classify(new[] { "provides_power" }, Supplier("BatteryStorage", "ResourceStorage", "Discharge"), simpleConsumer, C("PowerToggle", "UIToggle", null), C("Discharge", "UIToggle", null)).Skip);
    }

    /// <summary>Codex P2: suppliers of different stores are never ORed; each store read needs its own permission.</summary>
    [Fact]
    public void EveryReadStoreNeedsItsOwnSupplyPermission()
    {
        var consumer = Consumer("Pool");
        consumer.Refs["OperationalToggle"] = "Charge";
        DeviceComponent[] Pool(params DeviceComponent[] extra) => new[]
        {
            Supplier("A", "ResourceStorage", null),
            Supplier("B", "ResourceStorage", "DischargeB"),
            C("Pool", "MultiResourceStorage", storages: new[] { "A", "B" }),
            consumer, C("Charge", "UIToggle", null), C("DischargeB", "UIToggle", null),
        }.Concat(extra).ToArray();
        var d = Classify(new[] { "provides_power" }, Pool());
        Assert.Contains("battery store B are not implied", d.Skip);
        Assert.Contains("battery store B are not implied", Classify(new[] { "reactor" }, Pool(C("Producer", "ResourceConverter", null, refs: ("ToStorage", "Pool")))).Skip);
        // An unconditional alternative supplier of B itself permits it.
        Assert.Null(Classify(new[] { "provides_power" }, Pool(Supplier("BPickup", "MultiResourceStorage", null, "B"))).Skip);
        // Vanilla overclock reserve, read through a proxy only while overclocked, is supplied through the overclock provider.
        var proxy = C("OcProxy", "ResourceStorageProxy", refs: new[] { ("ComponentID", "Oc"), ("ProxyToggle", "IsOverclocked") });
        var vanilla = new[]
        {
            Supplier("A", "ResourceStorage", "Charge"), C("Oc", "ResourceStorage", max: 1000), proxy,
            C("Pool", "MultiResourceStorage", storages: new[] { "A", "OcProxy" }),
            Supplier("Provider", "MultiResourceStorage", "OcSupply", "A", "OcProxy"),
            consumer, C("Charge", "UIToggle", null), C("IsOverclocked", "UIToggle", null),
            Logic("OcSupply", "All", ("Charge", false), ("IsOverclocked", false)), C(DeviceClassifier.HeatProducer, "ResourceChange", null),
        };
        Assert.Null(Classify(new[] { "provides_power" }, vanilla).Skip);
        vanilla[4] = Supplier("Provider", "MultiResourceStorage", "OcSupply", "A");
        Assert.Contains("supplied by no storage: Oc", Classify(new[] { "provides_power" }, vanilla).Skip);
    }

    /// <summary>Codex P2: a supplier proxy grants permission only on a verified path (local, unconditional, readable static toggle).</summary>
    [Fact]
    public void SupplierProxyPathIsVerified()
    {
        DeviceComponent[] Parts(string[] categories, Action<DeviceComponent> broken)
        {
            var proxy = C("Remote", "ResourceStorageProxy", refs: new[] { ("ComponentID", "Reserve"), ("ProxyToggle", "Link") });
            broken(proxy);
            var parts = new List<DeviceComponent>
            {
                C("Reserve", "ResourceStorage", max: 2000), proxy, C("Link", "UIToggle", null),
                Supplier("Pickup", "MultiResourceStorage", null, "Remote"),
            };
            if (categories.Contains("reactor"))
            {
                parts.Add(C("Producer", "ResourceConverter", null, refs: ("ToStorage", "Reserve")));
            }
            else
            {
                var consumer = Consumer("Reserve");
                consumer.Refs["OperationalToggle"] = "Link";
                parts.Add(consumer);
            }
            return parts.ToArray();
        }
        foreach (string[] kind in new[] { new[] { "reactor" }, new[] { "provides_power" } })
        {
            Assert.Equal(new string?[] { null, "Link" }, Classify(kind, Parts(kind, _ => { })) is var ok ? new[] { ok.Skip, ok.Gates } : null); // local proxy: valid supplier path, gated by its toggle
            Assert.Contains("targets another part", Classify(kind, Parts(kind, p => p.PartLocation = new[] { 1, 0 })).Skip);
            Assert.Contains("ToggledComponents branch: Remote", Classify(kind, Parts(kind, p => p.Conditional = true)).Skip);
            Assert.Contains("not a single component reference: Remote", Classify(kind, Parts(kind, p => { p.Refs.Remove("ProxyToggle"); p.UnreadableRefs.Add("ProxyToggle"); })).Skip);
            Assert.Contains("missing component: Gone", Classify(kind, Parts(kind, p => p.Refs["ProxyToggle"] = "Gone")).Skip);
        }
    }
}