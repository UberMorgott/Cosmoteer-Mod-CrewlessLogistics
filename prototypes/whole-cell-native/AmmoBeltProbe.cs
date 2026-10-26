using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

// Crewless ammo belts. Package belt components come from the modded registry and run
// on native storage/proxy/criteria/toggle/converter code. Real cross-part binding:
// native ProxyHandler resolves neighbours through a native PartsManager cell index.
// Harness-owned: part placement (cell index + add events), logical clock, and the
// converter schedule (gate = the converter's own native OperationalToggle).
sealed partial class NativeProbe
{
    static readonly string[] BeltIds = {
        "morgott.crewless_logistics.ammo_belt", "morgott.crewless_logistics.ammo_belt_corner_left",
        "morgott.crewless_logistics.ammo_belt_corner_right", "morgott.crewless_logistics.ammo_belt_merger",
        "morgott.crewless_logistics.ammo_belt_splitter" };
    const double BeltDt = 1.0 / 30;
    sealed class BeltHop { public object Converter = null!; public object Gate = null!; public double Next; public bool WasOn; }
    sealed record PlacedPart(object Part, Dictionary<string, object> Components)
    {
        public object this[string name] => Components[name];
    }

    object beltRoot = null!, beltFixture = null!, simRoot = null!;
    readonly Dictionary<string, string> beltRegistry = new();
    readonly Dictionary<string, object> beltRuleCache = new();
    IDictionary beltCells = null!;
    List<BeltHop> beltHops = new();
    List<PlacedPart> beltParts = new();
    double beltTime;
    long beltPulses; // conversion ticks actually scheduled (operational converters only)
    Random beltOrder = new(20261027);
    Func<double, object> toTime = null!;

    void AmmoBeltProbe(object root)
    {
        beltRoot = root;
        beltFixture = Activator.CreateInstance(Core("Halfling.ObjectText.OTFile"), new object[] { Path.Combine(projectRoot, "prototypes", "whole-cell-native", "ammo-belt.rules") })!;
        const string registry = "<ships/terran/terran.rules>/Terran/Parts/";
        const string heatPipe = "<ships/terran/heat_pipe_adaptive/heat_pipe_adaptive.rules>/Part/";
        for (int i = 0; i < BeltIds.Length; i++) {
            string prefix = registry + (99 + i);
            Require((string)ReadRuleObject(root, prefix + "/ID", typeof(string)) == BeltIds[i], "Ammo belt not appended to the vanilla registry: " + BeltIds[i]);
            beltRegistry[BeltIds[i]] = prefix;
            Require((bool)ReadRuleObject(root, prefix + "/IsRotateable", typeof(bool)) && !(bool)ReadRuleObject(root, prefix + "/IsFlippable", typeof(bool)), "Belts must rotate and never flip (criteria Rot is a raw rotation difference)");
            Require((int)ReadRuleObject(root, prefix + "/MaxHealth", typeof(int)) == (int)ReadRuleObject(root, heatPipe + "MaxHealth", typeof(int)), "Belt must keep heat pipe durability");
            Require(((int[])ReadRuleObject(root, prefix + "/Size", typeof(int[]))).SequenceEqual(new[] { 1, 1 }), "Belts are 1x1");
            foreach (string lane in new[] { "AmmoLaneBullet", "AmmoLaneEmp" }) {
                // Native ResourceStorage.OnDestroying / OnRemovedFromPart2 drop nuggets only with these flags.
                var laneRules = Rules(prefix + "/Components/" + lane, "Cosmoteer.Ships.Parts.PartComponentRules", root);
                Require((bool)Field(laneRules.GetType(), "DropResourcesWhenDestroyed").GetValue(laneRules)! && (bool)Field(laneRules.GetType(), "DropResourcesWhenRemoved").GetValue(laneRules)!, "Lane would delete rounds on destroy/salvage: " + BeltIds[i] + "/" + lane);
            }
        }
        foreach (string id in BeltIds.Take(4))
            Require((string)ReadRuleObject(root, beltRegistry[id] + "/Components/AmmoLaneBulletOut/Type", typeof(string)) == "ResourceStorageProxy", "Single-output bullet lane must push straight into its proxy: " + id);
        Console.WriteLine("PASS: single-output belts push bullets directly into the AmmoLaneBulletOut proxy (no 1-element MultiResourceStorage).");
        Console.WriteLine("PASS: ammo belt lanes: all 10 lane storages (5 parts x bullet/EMP) parse with native DropResourcesWhenDestroyed=true and DropResourcesWhenRemoved=true.");
        for (int i = 0; i < 6; i++) {
            string toggle = (string)ReadRuleObject(root, "<gui/game/parts/part_toggles.rules>/PartToggles/" + (Nodes(root, "<gui/game/parts/part_toggles.rules>/PartToggles").Length - 6 + i) + "/ToggleID", typeof(string));
            Require(toggle.StartsWith("morgott.crewless_logistics.belt_"), "Splitter filter GUI toggle not registered: " + toggle);
        }

        simRoot = Field(ship.GetType(), "_root").GetValue(ship)!;
        var logical = simRoot.GetType().GetProperty("LogicalTime", All)!;
        var timeType = logical.PropertyType;
        var implicitTime = timeType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == timeType && m.GetParameters()[0].ParameterType == typeof(double))
            ?? timeType.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "op_Implicit" && m.ReturnType == timeType && m.GetParameters()[0].ParameterType == typeof(float));
        toTime = seconds => implicitTime.Invoke(null, new object[] { implicitTime.GetParameters()[0].ParameterType == typeof(double) ? seconds : (object)(float)seconds })!;
        SetField(ship, "_parentIsActive", true);
        SetField(ship, "<SelfActive>k__BackingField", true);
        var parts = RuntimeHelpers.GetUninitializedObject(Game("Cosmoteer.Ships.Parts.PartsManager"));
        foreach (string name in new[] { "_partsByCell", "_cellAddHandlers", "_cellRemovingHandlers" }) {
            var field = Field(parts.GetType(), name);
            field.SetValue(parts, Activator.CreateInstance(field.FieldType)!);
        }
        beltCells = (IDictionary)Field(parts.GetType(), "_partsByCell").GetValue(parts)!;
        SetField(ship, "<Parts>k__BackingField", parts);
        try {
            BeltHopScenario();
            BeltBackpressureAndCycling();
            BeltSplitterFilter();
            BeltNeighbourMatrix();
            BeltFactoryPullMatrix();
            BeltTypeSafety();
        } finally {
            SetField(ship, "<Parts>k__BackingField", null);
            SetTime(0);
        }
    }

    // ---- harness: grid, parts, clock, schedule ----
    void SetTime(double seconds) => Field(simRoot.GetType(), "<LogicalTime>k__BackingField").SetValue(simRoot, toTime(seconds));
    void NewBeltGrid()
    {
        beltCells.Clear();
        foreach (string name in new[] { "_cellAddHandlers", "_cellRemovingHandlers" }) ((IDictionary)Field(Property(ship, "Parts").GetType(), name).GetValue(Property(ship, "Parts"))!).Clear();
        beltHops = new(); beltParts = new(); beltTime = 0; SetTime(0);
    }
    static (int X, int Y) Rot(int r, int x, int y) => (r & 3) switch { 0 => (x, y), 1 => (-y, x), 2 => (-x, -y), _ => (y, -x) };
    object PlacePart(string id, int x, int y, int rotFlip, int w, int h)
    {
        NewTopologyPart(x, y, rotFlip);
        var rules = Property(part, "Rules");
        SetField(rules, "Size", Vector(w, h));
        SetField(rules, "ID", Id(Field(rules.GetType(), "ID").FieldType, id));
        bool swap = (rotFlip & 1) == 1;
        for (int dx = 0; dx < (swap ? h : w); dx++)
            for (int dy = 0; dy < (swap ? w : h); dy++) {
                var cell = Vector(x + dx, y + dy);
                Require(!beltCells.Contains(cell), "Fixture parts overlap");
                beltCells[cell] = part;
            }
        return part;
    }
    object CachedRules(string path, object source, string type)
    {
        string key = path + "|" + type;
        if (!beltRuleCache.TryGetValue(key, out var rules)) beltRuleCache[key] = rules = Rules(path, type, source);
        return rules;
    }
    bool HasRule(object source, string path)
    {
        var method = serializer.GetType().GetMethods(All).Where(m => m.Name == "HasPath" && !m.IsGenericMethod).OrderBy(m => m.GetParameters().Length).First();
        var parameters = method.GetParameters(); var args = new object?[parameters.Length]; args[0] = source; args[1] = path;
        for (int i = 2; i < args.Length; i++) args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        return (bool)method.Invoke(serializer, args)!;
    }
    object[] Nodes(object source, string path) => ((IEnumerable)ReadRuleObject(source, path, Core("Halfling.ObjectText.OTNode").MakeArrayType())).Cast<object>().ToArray();
    readonly Dictionary<string, string[]> beltComponentNames = new();
    static readonly HashSet<string> BeltLogicTypes = new() { "ResourceStorage", "ResourceStorageProxy", "MultiResourceStorage", "ResourceConverter", "MultiToggle", "ToggleProxy", "UIToggle", "StaticValue", "ValueOperation", "ThresholdToggle" };

    PlacedPart Belt(string id, int x, int y, int rot)
    {
        var p = PlacePart(id, x, y, rot, 1, 1);
        string prefix = beltRegistry[id] + "/Components";
        if (!beltComponentNames.TryGetValue(id, out var names)) {
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), Core("Halfling.ObjectText.OTNode"));
            names = ((IDictionary)ReadRuleObject(beltRoot, prefix, dictType)).Keys.Cast<string>()
                .Where(name => HasRule(beltRoot, prefix + "/" + name + "/Type") && BeltLogicTypes.Contains((string)ReadRuleObject(beltRoot, prefix + "/" + name + "/Type", typeof(string)))).ToArray();
            beltComponentNames[id] = names;
        }
        var components = new Dictionary<string, object>();
        foreach (string name in names) {
            var rules = CachedRules(prefix + "/" + name, beltRoot, "Cosmoteer.Ships.Parts.PartComponentRules");
            if (rules.GetType().Name == "PartUIToggleRules" && (bool)Field(rules.GetType(), "RememberValue").GetValue(rules)!) {
                Require((int)Field(rules.GetType(), "Default").GetValue(rules)! == 1 && !(bool)Field(rules.GetType(), "RequiresCommand").GetValue(rules)!, "Splitter filters must default to allowed without command");
                // No save metadata in the fixture; only persistence is bypassed.
                SetField(rules, "RememberValue", false);
            }
            components[name] = Component(rules);
        }
        return Link(p, components);
    }
    PlacedPart Endpoint(string id, int x, int y, int rotFlip, int w, int h, params (string Path, object Source)[] storages)
    {
        var p = PlacePart(id, x, y, rotFlip, w, h);
        var components = new Dictionary<string, object>();
        foreach (var (path, source) in storages)
            components[path.Split('/')[^1]] = Component(CachedRules(path, source, "Cosmoteer.Ships.Parts.PartComponentRules"));
        return Link(p, components);
    }
    static bool Is(object component, string typeName) { for (var t = component.GetType(); t != null; t = t.BaseType) if (t.Name == typeName) return true; return false; }
    PlacedPart Link(object p, Dictionary<string, object> components)
    {
        int Order(object c) => Is(c, "PartUIToggle") || Is(c, "PartDebugToggle") || Is(c, "PartStaticValue") ? 0 : Is(c, "PartToggleProxy") ? 1 : Is(c, "ResourceStorageProxy") || c.GetType().Name == "ResourceStorage" ? 2 : Is(c, "MultiResourceStorage") ? 3 : Is(c, "PartValueOperation") ? 4 : Is(c, "PartThresholdToggle") ? 5 : Is(c, "PartMultiToggle") ? 6 : 7;
        foreach (var c in components.Values.OrderBy(Order)) {
            // Converters/storages: link only (no native operational scheduling).
            if (Is(c, "ResourceConverter") || Is(c, "BaseResourceStorage")) Call(c, "OnAddedToPart2", p);
            else Call(c, "OnAddedToPart", p);
        }
        foreach (var c in components.Values.Where(c => Is(c, "ResourceConverter"))) {
            var rules = RulesOf(c);
            var gateId = Field(rules.GetType(), "OperationalToggle").GetValue(rules)!.ToString()!;
            beltHops.Add(new BeltHop { Converter = c, Gate = components[gateId] });
        }
        var placed = new PlacedPart(p, components);
        beltParts.Add(placed);
        return placed;
    }
    // Native attach: proxies resolve their neighbour cell via PartsManager and criteria.
    void AttachAll()
    {
        foreach (var placed in beltParts) foreach (var c in placed.Components.Values.Where(c => Is(c, "PartToggleProxy"))) Call(c, "OnPartAttached");
        foreach (var placed in beltParts) foreach (var c in placed.Components.Values.Where(c => Is(c, "ResourceStorageProxy"))) Call(c, "OnPartAttached2");
    }
    void BeltTicks(int ticks, Action? afterTick = null)
    {
        for (int t = 0; t < ticks; t++) {
            beltTime += BeltDt;
            SetTime(beltTime);
            // Equal-time converters have no guaranteed native order: shuffle.
            foreach (var hop in beltHops.OrderBy(_ => beltOrder.Next())) {
                bool on = Flag(hop.Gate, "IsToggleOn");
                if (on && !hop.WasOn) hop.Next = beltTime + BeltDt; // native OnOperational: first tick one Interval later
                hop.WasOn = on;
                if (on && beltTime >= hop.Next - 1e-9) { Pulse(hop.Converter); beltPulses++; hop.Next = beltTime + BeltDt; }
            }
            afterTick?.Invoke();
        }
    }
    static object? Proxied(object proxy)
    {
        var handler = Field(proxy.GetType(), "_proxy").GetValue(proxy)!;
        return handler.GetType().GetProperty("ProxiedComponent", All)!.GetValue(handler);
    }
    // Most-derived "new Rules" property; GetProperty("Rules") is ambiguous.
    static object RulesOf(object c) => c.GetType().GetProperties(All).First(p => p.Name == "Rules").GetValue(c)!;
    static int Rounds(object storage) => Count(storage, "Resources");
    (string, object) Fixture(string path) => (path, beltFixture);
    (string, object) Vanilla(string path) => (path, beltRoot);
    const string CannonAmmo = "<ships/terran/cannon_med/cannon_med.rules>/Part/Components/AmmoStorage";
    // Belt lanes plus endpoints hold every round; nothing may appear or vanish.
    int Total(string lane, params object[] extra) => beltParts.Sum(p => p.Components.TryGetValue(lane, out var s) ? Rounds(s) : 0) + extra.Sum(Rounds);

    // ---- (a) hop transfer, (f) layout rotations ----
    void BeltHopScenario()
    {
        foreach (int r in new[] { 0, 1, 2, 3 }) {
            NewBeltGrid();
            (int, int) At(int x, int y) => Rot(r, x, y);
            // Multi-cell part: rotate its rect and use the new upper-left cell.
            (int, int) Corner(int x, int y, int w, int h) { var a = Rot(r, x, y); var b = Rot(r, x + w - 1, y + h - 1); return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)); }
            var (fx, fy) = Corner(0, 0, 2, 2);
            var factory = Endpoint("cosmoteer.factory_ammo", fx, fy, r, 2, 2, Fixture("AmmoFactory/AmmoStorage"));
            SetCount(factory["AmmoStorage"], 40);
            var belts = new List<PlacedPart>();
            for (int i = 0; i < 5; i++) { var (bx, by) = At(2 + i, 1); belts.Add(Belt(BeltIds[0], bx, by, (1 + r) & 3)); }
            var (cx, cy) = Corner(7, 0, 2, 2);
            var cannon = Endpoint("cosmoteer.cannon_med", cx, cy, r, 2, 2, Vanilla(CannonAmmo));
            AttachAll();
            Require(Proxied(belts[0]["AmmoLaneBulletIn"]) == factory["AmmoStorage"], "Belt did not bind the factory_ammo output behind it");
            Require(Proxied(belts[4]["AmmoLaneBulletOut"]) == cannon["AmmoStorage"], "Belt did not bind cannon_med AmmoStorage");
            for (int i = 0; i < 4; i++) Require(Proxied(belts[i]["AmmoLaneBulletOut"]) == belts[i + 1]["AmmoLaneBullet"], "Straight belt chain not linked");
            var firstArrival = new double[6]; Array.Fill(firstArrival, -1);
            BeltTicks(900, () => {
                Require(Total("AmmoLaneBullet", factory["AmmoStorage"], cannon["AmmoStorage"]) == 40, "Bullets not conserved on belt hop");
                for (int i = 0; i < 5; i++) if (firstArrival[i] < 0 && Rounds(belts[i]["AmmoLaneBullet"]) > 0) firstArrival[i] = beltTime;
                if (firstArrival[5] < 0 && Rounds(cannon["AmmoStorage"]) > 0) firstArrival[5] = beltTime;
            });
            for (int i = 0; i < 5; i++) Require(firstArrival[i + 1] - firstArrival[i] >= 2 * BeltDt - 1e-9 && firstArrival[i + 1] - firstArrival[i] >= 0.2 - 1e-9, "Round crossed a tile within one tick / before its travel delay");
            Require(Rounds(cannon["AmmoStorage"]) == 15 && Rounds(factory["AmmoStorage"]) == 20 && belts.All(b => Rounds(b["AmmoLaneBullet"]) == 1), "Belt did not fill cannon and stop with full lanes");
            Require(belts.All(b => Rounds(b["AmmoLaneEmp"]) == 0), "EMP lane received bullets");
            if (r == 0) {
                double perTile = (firstArrival[5] - firstArrival[0]) / 5;
                Console.WriteLine($"PASS: ammo belt hop: 5 tiles factory_ammo->cannon_med, 40 bullets conserved every tick, first round {firstArrival[0]:0.000}s->{firstArrival[5]:0.000}s ({perTile:0.000}s/tile = {perTile / BeltDt:0.0} ticks), final cannon 15/15, lanes 1x5, factory 20.");
                scenarioResults.Add(new { name = "ammo belt hop transfer", tiles = 5, conserved = 40, secondsPerTile = Math.Round(perTile, 3), ticksPerTile = Math.Round(perTile / BeltDt, 1), cannon = 15, factory = 20 });
            }
        }
        Console.WriteLine("PASS: ammo belt hop layout in all 4 ship rotations (factory, belts, cannon rotated together).");
        scenarioResults.Add(new { name = "ammo belt layout rotations", rotations = 4 });
    }

    // Splitter at (3,1) facing right: left output up (3,0), right output down (3,2), front (4,1).
    (PlacedPart Factory, PlacedPart Splitter, PlacedPart LeftBelt, PlacedPart RightBelt, PlacedPart CannonA, PlacedPart CannonB, PlacedPart? FrontBelt, PlacedPart? CannonC) SplitterLayout(bool front)
    {
        NewBeltGrid();
        var factory = Endpoint("cosmoteer.factory_ammo", 0, 0, 0, 2, 2, Fixture("AmmoFactory/AmmoStorage"));
        Belt(BeltIds[0], 2, 1, 1);
        var splitter = Belt(BeltIds[4], 3, 1, 1);
        var left = Belt(BeltIds[0], 3, 0, 0);
        var a = Endpoint("cosmoteer.cannon_med", 3, -2, 0, 2, 2, Vanilla(CannonAmmo));
        var right = Belt(BeltIds[0], 3, 2, 2);
        var b = Endpoint("cosmoteer.cannon_med", 3, 3, 0, 2, 2, Vanilla(CannonAmmo));
        PlacedPart? f = null, c = null;
        if (front) { f = Belt(BeltIds[0], 4, 1, 1); c = Endpoint("cosmoteer.cannon_med", 5, 0, 0, 2, 2, Vanilla(CannonAmmo)); }
        AttachAll();
        Require(Proxied(splitter["AmmoLaneBulletLeft"]) == left["AmmoLaneBullet"] && Proxied(splitter["AmmoLaneBulletRight"]) == right["AmmoLaneBullet"], "Splitter side outputs not bound to side belts");
        Require(front ? Proxied(splitter["AmmoLaneBulletFront"]) == f!["AmmoLaneBullet"] : Proxied(splitter["AmmoLaneBulletFront"]) == null, "Splitter front binding mismatch");
        return (factory, splitter, left, right, a, b, f, c);
    }

    // ---- (b) backpressure + native cycling ----
    void BeltBackpressureAndCycling()
    {
        var l = SplitterLayout(true);
        SetCount(l.Factory["AmmoStorage"], 60);
        var cannons = new[] { l.CannonA["AmmoStorage"], l.CannonB["AmmoStorage"], l.CannonC!["AmmoStorage"] };
        BeltTicks(900, () => Require(Total("AmmoLaneBullet", l.Factory["AmmoStorage"], cannons[0], cannons[1], cannons[2]) == 60, "Bullets not conserved through splitter"));
        Require(cannons.All(c => Rounds(c) == 15), "Splitter did not fill all three outputs");
        SplitterFairness();

        l = SplitterLayout(false);
        SetCount(l.Factory["AmmoStorage"], 40);
        SetCount(l.CannonA["AmmoStorage"], 15);
        var full = l.CannonA["AmmoStorage"]; var open = l.CannonB["AmmoStorage"];
        BeltTicks(900, () => Require(Total("AmmoLaneBullet", l.Factory["AmmoStorage"], full, open) == 55, "Bullets not conserved under backpressure"));
        Require(Rounds(full) == 15 && Rounds(l.LeftBelt["AmmoLaneBullet"]) == 1, "Full cannon branch did not stop with one round waiting");
        Require(Rounds(open) == 15, "Open branch starved by full branch");
        long before = beltPulses;
        BeltTicks(300);
        long idlePulses = beltPulses - before;
        Require(idlePulses == 0, "Full/blocked lanes keep polling: " + idlePulses + " conversion ticks in 10 s");
        int factoryLeft = Rounds(l.Factory["AmmoStorage"]);
        SetCount(full, 10);
        BeltTicks(300);
        Require(Rounds(full) == 15, "Drained branch did not resume");
        Console.WriteLine($"PASS: backpressure: full cannon_med branch holds (15, lane 1) while other branch fills 0->15; branch resumes after drain; factory left {factoryLeft}. Saturated 10 s: {idlePulses} converter ticks (gate = has round AND output RemainingCapacity>=1).");
        scenarioResults.Add(new { name = "ammo belt splitter backpressure", fullBranch = 15, openBranch = 15, resumed = true, saturatedConverterTicks = idlePulses });
    }

    // ---- (b2) splitter fairness: every filter combination, both lanes, long run ----
    // Sinks are drained every tick and the splitter lane is refilled when empty, so
    // only the native output choice decides the shares.
    void SplitterFairness()
    {
        var sides = new[] { "Front", "Left", "Right" };
        int combos = 0; double worst = 0;
        foreach (bool emp in new[] { false, true }) foreach (int mask in Enumerable.Range(0, 8)) {
            NewBeltGrid();
            string type = emp ? "Emp" : "Bullet", lane = "AmmoLane" + type;
            var splitter = Belt(BeltIds[4], 3, 1, 1);
            var belts = new[] { Belt(BeltIds[0], 4, 1, 1), Belt(BeltIds[0], 3, 0, 0), Belt(BeltIds[0], 3, 2, 2) };
            object[] sinks = emp
                ? new[] {
                    Endpoint("cosmoteer.missile_launcher", 5, 0, 1, 2, 4, Fixture("Launcher/EMPMissilesToggle"), Fixture("EmpLauncher/MissileStorage")),
                    Endpoint("cosmoteer.missile_launcher", 3, -4, 0, 2, 4, Fixture("Launcher/EMPMissilesToggle"), Fixture("EmpLauncher/MissileStorage")),
                    Endpoint("cosmoteer.missile_launcher", 3, 3, 0, 2, 4, Fixture("Launcher/EMPMissilesToggle"), Fixture("EmpLauncher/MissileStorage")) }
                    .Select(p => { SetToggle(p["EMPMissilesToggle"], true); return p["MissileStorage"]; }).ToArray()
                : new[] { Endpoint("cosmoteer.cannon_med", 5, 0, 0, 2, 2, Vanilla(CannonAmmo)), Endpoint("cosmoteer.cannon_med", 3, -2, 0, 2, 2, Vanilla(CannonAmmo)), Endpoint("cosmoteer.cannon_med", 3, 3, 0, 2, 2, Vanilla(CannonAmmo)) }
                    .Select(p => p["AmmoStorage"]).ToArray();
            for (int s = 0; s < 3; s++) SetProperty(splitter["AmmoFilter" + type + sides[s]], "Mode", (mask >> s) & 1);
            AttachAll();
            var delivered = new long[3];
            BeltTicks(mask == 0 ? 300 : 9000, () => {
                if (Rounds(splitter[lane]) == 0) SetCount(splitter[lane], 1);
                for (int s = 0; s < 3; s++) { delivered[s] += Rounds(sinks[s]); SetCount(sinks[s], 0); }
            });
            var allowed = Enumerable.Range(0, 3).Where(s => ((mask >> s) & 1) == 1).ToArray();
            for (int s = 0; s < 3; s++) if (!allowed.Contains(s)) Require(delivered[s] == 0 && Rounds(belts[s][lane]) == 0, $"{type} left by blocked {sides[s]} output (mask {mask})");
            if (allowed.Length > 0) {
                double mean = allowed.Average(s => (double)delivered[s]);
                double deviation = allowed.Max(s => Math.Abs(delivered[s] - mean) / mean);
                Console.WriteLine($"  splitter {type} allowed={string.Join("+", allowed.Select(s => sides[s]))}: delivered F:L:R = {delivered[0]}:{delivered[1]}:{delivered[2]} (max deviation {deviation:P1})");
                Require(deviation <= 0.15, $"Splitter unfair for {type} mask {mask}: {string.Join(":", delivered)}");
                worst = Math.Max(worst, deviation);
            }
            combos++;
        }
        Console.WriteLine($"PASS: splitter fairness: {combos} filter combos (bullet+EMP x 8 masks), 9000 ticks each, allowed outputs within {worst:P1} of mean (limit 15%), blocked outputs 0.");
        scenarioResults.Add(new { name = "ammo belt splitter fairness", combos, ticks = 9000, worstDeviation = Math.Round(worst, 4), limit = 0.15 });
    }
    // ---- (c) splitter filter (actual package UIToggle Mode setter) ----
    void BeltSplitterFilter()
    {
        var l = SplitterLayout(true);
        SetCount(l.Factory["AmmoStorage"], 30);
        SetProperty(l.Splitter["AmmoFilterBulletFront"], "Mode", 0);
        Require(Proxied(l.Splitter["AmmoLaneBulletFront"]) == null && Proxied(l.Splitter["AmmoLaneEmpFront"]) == l.FrontBelt!["AmmoLaneEmp"], "Bullet front filter must unbind only the bullet front output");
        BeltTicks(900, () => Require(Rounds(l.CannonC!["AmmoStorage"]) == 0 && Rounds(l.FrontBelt!["AmmoLaneBullet"]) == 0, "Bullet went forward through a blocked filter"));
        int sides = Rounds(l.CannonA["AmmoStorage"]) + Rounds(l.CannonB["AmmoStorage"]);
        Require(Rounds(l.CannonA["AmmoStorage"]) > 0 && Rounds(l.CannonB["AmmoStorage"]) > 0 && Total("AmmoLaneBullet", l.Factory["AmmoStorage"], l.CannonA["AmmoStorage"], l.CannonB["AmmoStorage"], l.CannonC!["AmmoStorage"]) == 30, "Side outputs did not take the bullets");
        SetProperty(l.Splitter["AmmoFilterBulletFront"], "Mode", 1);
        BeltTicks(300);
        Require(Rounds(l.CannonC!["AmmoStorage"]) > 0 || Rounds(l.FrontBelt!["AmmoLaneBullet"]) > 0 || Rounds(l.Factory["AmmoStorage"]) == 0, "Re-allowed front output stayed blocked");
        Console.WriteLine($"PASS: splitter filter: bullets front=blocked -> front cannon 0, front lane 0 for 900 ticks; sides got {sides} of 30 (rest on lanes); re-allow reconnects.");
        scenarioResults.Add(new { name = "ammo belt splitter filter", blockedOutput = "bullet/front", forwardRounds = 0, sideRounds = sides });
    }

    // ---- (d)+(f) every source part/rotation/output x every neighbour part/rotation ----
    static readonly Dictionary<string, (int X, int Y)[]> BeltOutputs = new() {
        [BeltIds[0]] = new[] { (0, -1) }, [BeltIds[1]] = new[] { (-1, 0) }, [BeltIds[2]] = new[] { (1, 0) },
        [BeltIds[3]] = new[] { (0, -1) }, [BeltIds[4]] = new[] { (0, -1), (-1, 0), (1, 0) } };
    void BeltNeighbourMatrix()
    {
        int cases = 0, linked = 0;
        foreach (var source in BeltIds) foreach (int sr in new[] { 0, 1, 2, 3 }) foreach (var output in BeltOutputs[source])
            foreach (var target in BeltIds) foreach (int tr in new[] { 0, 1, 2, 3 }) {
                NewBeltGrid();
                var dir = Rot(sr, output.X, output.Y);
                var s = Belt(source, 0, 0, sr);
                var t = Belt(target, dir.X, dir.Y, tr);
                AttachAll();
                SetCount(s["AmmoLaneBullet"], 1); SetCount(s["AmmoLaneEmp"], 1);
                // Oracle: a neighbour input side (back; merger also left/right) faces the source.
                var inputs = target == BeltIds[3] ? new[] { (0, 1), (-1, 0), (1, 0) } : new[] { (0, 1) };
                bool expected = inputs.Any(d => Rot(tr, d.Item1, d.Item2) == (-dir.X, -dir.Y));
                BeltTicks(20);
                foreach (var lane in new[] { "AmmoLaneBullet", "AmmoLaneEmp" }) {
                    Require(Rounds(t[lane]) == (expected ? 1 : 0) && Rounds(s[lane]) == (expected ? 0 : 1), $"Neighbour rule failed: {source} r{sr} out{output} -> {target} r{tr} {lane} expected {expected}");
                }
                cases++; if (expected) linked++;
            }
        Console.WriteLine($"PASS: belt neighbour matrix {cases} cases (5 parts x 4 rot x outputs x 5 parts x 4 rot), {linked} input-facing links, {cases - linked} passing/away/output-facing neighbours received nothing; bullet and EMP lanes.");
        scenarioResults.Add(new { name = "ammo belt neighbour orientation matrix", cases, linked, rejected = cases - linked });
    }

    // ---- (f) rear pull: every factory rot/flip x every belt cell on the ring x every belt rot ----
    void BeltFactoryPullMatrix()
    {
        int cases = 0, pulls = 0;
        foreach (var (factoryId, w, h, outputs, storage, lane, otherLane) in new[] {
            ("cosmoteer.factory_ammo", 2, 2, new[] { ((1, 1), (2, 1)), ((1, 1), (1, 2)) }, "AmmoFactory/AmmoStorage", "AmmoLaneBulletIn", "AmmoLaneEmpIn"),
            ("cosmoteer.factory_emp", 3, 4, new[] { ((0, 3), (0, 4)), ((1, 3), (1, 4)), ((2, 3), (2, 4)) }, "EmpFactory/MissileStorage", "AmmoLaneEmpIn", "AmmoLaneBulletIn") })
        foreach (int rotFlip in Enumerable.Range(0, 8)) {
            int fr = rotFlip & 3; bool flip = rotFlip >= 4;
            int rw = (fr & 1) == 1 ? h : w, rh = (fr & 1) == 1 ? w : h;
            // Independent oracle: vanilla flip-then-rotate placement of local cells.
            (int, int) Ship((int X, int Y) c)
            {
                int x = flip ? w - 1 - c.X : c.X, y = c.Y;
                return fr switch { 0 => (x, y), 1 => (h - 1 - y, x), 2 => (w - 1 - x, h - 1 - y), _ => (y, w - 1 - x) };
            }
            var faces = outputs.Select(o => (Out: Ship(o.Item1), Belt: Ship(o.Item2))).ToArray();
            for (int x = -1; x <= rw; x++) for (int y = -1; y <= rh; y++) {
                if (x >= 0 && x < rw && y >= 0 && y < rh) continue;
                if ((x < 0 || x >= rw) && (y < 0 || y >= rh)) continue; // corners touch no face
                foreach (int br in new[] { 0, 1, 2, 3 }) {
                    NewBeltGrid();
                    var factory = Endpoint(factoryId, 0, 0, rotFlip, w, h, Fixture(storage));
                    var belt = Belt(BeltIds[0], x, y, br);
                    AttachAll();
                    var back = Rot(br, 0, 1);
                    bool expected = faces.Any(f => f.Belt == (x, y) && f.Out == (x + back.X, y + back.Y));
                    Require((Proxied(belt[lane]) != null) == expected, $"Factory pull rule failed: {factoryId} rotFlip{rotFlip} belt ({x},{y}) r{br} expected {expected}");
                    Require(Proxied(belt[otherLane]) == null, "Lane pulled from the wrong factory type");
                    cases++; if (expected) pulls++;
                }
            }
        }
        Console.WriteLine($"PASS: rear pull matrix {cases} cases (factory_ammo, factory_emp x 4 rot x flip x ring cells x 4 belt rot): {pulls} binds exactly on output faces; never a wrong-type lane.");
        scenarioResults.Add(new { name = "ammo belt factory pull matrix", cases, pulls });
    }

    // ---- (e) type safety ----
    void BeltTypeSafety()
    {
        // EMP never enters cannon_med; bullets do.
        NewBeltGrid();
        var b = Belt(BeltIds[0], 0, 0, 0);
        var cannon = Endpoint("cosmoteer.cannon_med", 0, -2, 0, 2, 2, Vanilla(CannonAmmo));
        AttachAll();
        SetCount(b["AmmoLaneBullet"], 1); SetCount(b["AmmoLaneEmp"], 1);
        BeltTicks(60);
        Require(Rounds(cannon["AmmoStorage"]) == 1 && Rounds(b["AmmoLaneEmp"]) == 1 && Proxied(b["AmmoLaneEmpFront"]) == null, "EMP lane must never bind cannon_med");

        // Launcher in EMP mode takes EMP parts, never bullets.
        NewBeltGrid();
        b = Belt(BeltIds[0], 0, 0, 0);
        var launcher = Endpoint("cosmoteer.missile_launcher", 0, -4, 0, 2, 4, Fixture("Launcher/EMPMissilesToggle"), Fixture("EmpLauncher/MissileStorage"));
        SetToggle(launcher["EMPMissilesToggle"], true);
        AttachAll();
        SetCount(b["AmmoLaneBullet"], 1); SetCount(b["AmmoLaneEmp"], 1);
        BeltTicks(60);
        Require(Rounds(launcher["MissileStorage"]) == 1 && Rounds(b["AmmoLaneBullet"]) == 1 && Proxied(b["AmmoLaneBulletOut"]) == null, "Bullets must never bind missile_launcher; EMP mode must accept EMP parts");

        // Launcher in nuke mode: the shared MissileStorage ID is the nuke storage.
        NewBeltGrid();
        b = Belt(BeltIds[0], 0, 0, 0);
        launcher = Endpoint("cosmoteer.missile_launcher", 0, -4, 0, 2, 4, Fixture("Launcher/EMPMissilesToggle"), Fixture("NukeLauncher/MissileStorage"));
        AttachAll();
        var nuke = launcher["MissileStorage"];
        SetCount(b["AmmoLaneEmp"], 1);
        BeltTicks(60);
        Require(Rounds(nuke) == 0 && Rounds(b["AmmoLaneEmp"]) == 1 && Proxied(b["AmmoLaneEmpLauncherFront"]) == null, "EMP entered a nuke-mode launcher");

        // Mode nuke->EMP, worst case: EMP toggle turns on while nuke components still exist.
        // Native: one synchronous missile_type Mode event; nuke set removal is immediate,
        // EMP set addition is deferred (PartToggledComponents.OnToggleChanged).
        // Created unregistered: the EMP set does not exist yet (Component() would map it).
        var empRules = CachedRules("EmpLauncher/MissileStorage", beltFixture, "Cosmoteer.Ships.Parts.PartComponentRules");
        var empStorage = empRules.GetType().GetMethod("CreateComponent", All)!.Invoke(empRules, null)!;
        SetToggle(launcher["EMPMissilesToggle"], true);
        bool transientNuke = Proxied(b["AmmoLaneEmpLauncherFront"]) == nuke;
        RaiseComponentChange(launcher.Part, "MissileStorage", nuke, false);
        Require(Proxied(b["AmmoLaneEmpLauncherFront"]) == null, "Proxy stayed on nuke storage after nuke components were removed");
        BeltTicks(30);
        Require(Rounds(nuke) == 0 && Rounds(b["AmmoLaneEmp"]) == 1, "EMP moved during mode switch");
        RaiseComponentChange(launcher.Part, "MissileStorage", empStorage, true);
        Require(Proxied(b["AmmoLaneEmpLauncherFront"]) == empStorage, "Proxy did not bind the deferred EMP storage");
        BeltTicks(60);
        Require(Rounds(empStorage) == 1 && Rounds(b["AmmoLaneEmp"]) == 0 && Rounds(nuke) == 0, "EMP mode did not receive");

        // Mode EMP->nuke: EMP toggle off first; nuke set added later.
        SetToggle(launcher["EMPMissilesToggle"], false);
        RaiseComponentChange(launcher.Part, "MissileStorage", empStorage, false);
        RaiseComponentChange(launcher.Part, "MissileStorage", nuke, true);
        SetCount(b["AmmoLaneEmp"], 1);
        BeltTicks(90);
        Require(Rounds(nuke) == 0 && Rounds(b["AmmoLaneEmp"]) == 1 && Proxied(b["AmmoLaneEmpLauncherFront"]) == null, "EMP entered nuke storage after EMP->nuke switch");

        // Factory type: an EMP factory output never feeds the bullet lane.
        NewBeltGrid();
        var empFactory = Endpoint("cosmoteer.factory_emp", 0, 0, 0, 3, 4, Fixture("EmpFactory/MissileStorage"));
        SetCount(empFactory["MissileStorage"], 5);
        b = Belt(BeltIds[0], 1, 4, 2);
        AttachAll();
        BeltTicks(30);
        Require(Rounds(b["AmmoLaneEmp"]) == 1 && Rounds(b["AmmoLaneBullet"]) == 0 && Rounds(empFactory["MissileStorage"]) == 4, "EMP factory pull mismatch");
        Console.WriteLine($"PASS: type safety: EMP never binds cannon_med; bullets never bind missile_launcher; nuke-mode MissileStorage never bound; nuke->EMP transient nuke binding={transientNuke} cleared by immediate nuke removal before any tick, EMP storage bound after deferred add; EMP->nuke stays unbound; factory_emp feeds EMP lane only.");
        scenarioResults.Add(new { name = "ammo belt type safety", empIntoCannon = 0, bulletsIntoLauncher = 0, empIntoNuke = 0, transientNukeBindingInSyncModeEvent = transientNuke });
    }

    // Emulates Part.AddComponents/RemoveComponent bookkeeping + native ComponentAdded/Removed events.
    void RaiseComponentChange(object p, string id, object component, bool added)
    {
        var map = (IDictionary)Field(p.GetType(), "_componentsByID").GetValue(p)!;
        var key = Id(Field(RulesOf(component).GetType(), "ID").FieldType, id);
        if (added) { map[key] = component; SetField(component, "<Part>k__BackingField", p); if (Is(component, "BaseResourceStorage")) Call(component, "OnAddedToPart2", p); }
        else map.Remove(key);
        var partType = Game("Cosmoteer.Ships.Parts.Part");
        var eventKey = partType.GetField(added ? "E_COMPONENT_ADDED" : "E_COMPONENT_REMOVED", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null);
        var events = Field(partType, "_events").GetValue(p)!;
        var handlerType = typeof(Action<,>).MakeGenericType(partType, Game("Cosmoteer.Ships.Parts.PartComponent"));
        var get = events.GetType().GetMethods(All).First(m => m.Name == "Get" && m.IsGenericMethod).MakeGenericMethod(handlerType);
        var handler = (Delegate?)get.Invoke(events, new[] { eventKey });
        handler?.DynamicInvoke(p, component);
    }
}
