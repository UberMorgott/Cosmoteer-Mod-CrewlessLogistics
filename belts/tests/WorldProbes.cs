using System.Text.RegularExpressions;
using CrewlessLogistics.Belts;

/// <summary>
/// Headless v1 behaviour probes on the pure belt world (topology, splitter, connector, save payload).
/// Rooms are fakes with the vanilla endpoint semantics that matter: FlexResourceGrid cargo (per-tile type, stack cap,
/// empty tile takes any type), TypedResourceGrid factory (typed input sink + typed output source), weapon ammo sink.
/// The real source/sink adapter (PartRoom) is game glue, exercised in game only.
/// </summary>
static class WorldProbes
{
    const int Up = 0, Right = 1, Down = 2, Left = 3;
    static readonly (int X, int Y)[] Dir = { (0, -1), (1, 0), (0, 1), (-1, 0) };
    static int T(string id) => Array.IndexOf(BeltTypes.Ids, id);
    static readonly int Sulfur = T("sulfur"), Iron = T("iron"), Bullet = T("bullet");

    sealed class Grid
    {
        public readonly List<Node> Nodes = new();
        readonly Dictionary<(int, int), int> _cells = new();
        readonly Dictionary<(int, int), IRoom> _rooms = new();

        public Node Add(NodeKind kind, int x, int y, int dir, int sx = 0, int sy = 0, NodeState? state = null)
        {
            var n = new Node(state ?? new NodeState(kind), x, y, Dir[dir].X, Dir[dir].Y, sx, sy);
            _cells[(x, y)] = Nodes.Count;
            if (kind == NodeKind.Splitter)
            {
                _cells[(x + sx, y + sy)] = Nodes.Count;
            }
            Nodes.Add(n);
            return n;
        }

        public Node Belt(int x, int y, int dir) => Add(NodeKind.Belt, x, y, dir);

        public Node Connector(int x, int y, int dir, int filter)
        {
            Node n = Add(NodeKind.Connector, x, y, dir);
            n.Filter = filter;
            return n;
        }

        public T Room<T>(int x, int y, T room) where T : IRoom
        {
            _rooms[(x, y)] = room;
            return room;
        }

        public BeltWorld Build() => new(Nodes, (x, y) => _cells.TryGetValue((x, y), out int i) ? i : -1, (x, y) => _rooms.GetValueOrDefault((x, y)));
    }

    /// <summary>FlexResourceGrid-like cargo hold: tiles hold one type each, up to Stack; empty tiles accept any type.</summary>
    sealed class Cargo : IRoom
    {
        public readonly int[] Type, Count;
        public const int Stack = 5;

        public Cargo(int tiles)
        {
            Type = Enumerable.Repeat(-1, tiles).ToArray();
            Count = new int[tiles];
        }

        public Cargo With(int type, int n)
        {
            while (n-- > 0 && Put(type))
            {
            }
            return this;
        }

        public int Total(int type) => Enumerable.Range(0, Type.Length).Where(i => Type[i] == type).Sum(i => Count[i]);

        public int FindSupply(int filter, IRoom? acceptor)
        {
            for (int i = 0; i < Type.Length; i++)
            {
                if (Count[i] > 0 && (filter < 0 || Type[i] == filter) && (acceptor == null || acceptor.CanAccept(Type[i])))
                {
                    return Type[i];
                }
            }
            return -1;
        }

        public bool Take(int type)
        {
            for (int i = 0; i < Type.Length; i++)
            {
                if (Type[i] == type && Count[i] > 0)
                {
                    if (--Count[i] == 0)
                    {
                        Type[i] = -1;
                    }
                    return true;
                }
            }
            return false;
        }

        int Slot(int type)
        {
            for (int i = 0; i < Type.Length; i++)
            {
                if (Type[i] == type && Count[i] < Stack)
                {
                    return i;
                }
            }
            return Array.IndexOf(Type, -1);
        }

        public bool CanAccept(int type) => Slot(type) >= 0;

        public bool Put(int type)
        {
            int i = Slot(type);
            if (i < 0)
            {
                return false;
            }
            Type[i] = type;
            Count[i]++;
            return true;
        }
    }

    /// <summary>TypedResourceGrid-like part: typed input sinks with capacity, typed output stock (factory, weapon).</summary>
    sealed class Typed : IRoom
    {
        public readonly int[] InCap = new int[BeltTypes.Ids.Length], In = new int[BeltTypes.Ids.Length], Out = new int[BeltTypes.Ids.Length];

        public int FindSupply(int filter, IRoom? acceptor)
        {
            for (int t = 0; t < Out.Length; t++)
            {
                if (Out[t] > 0 && (filter < 0 || t == filter) && (acceptor == null || acceptor.CanAccept(t)))
                {
                    return t;
                }
            }
            return -1;
        }

        public bool Take(int type) => Out[type] > 0 && Out[type]-- > 0;

        public bool CanAccept(int type) => In[type] < InCap[type];

        public bool Put(int type) => CanAccept(type) && ++In[type] > 0;
    }

    public static void Run(Action<bool, string> check)
    {
        AutoCurve(check);
        ItemRender(check);
        Sideload(check);
        SplitterSplitMerge(check);
        Connectors(check);
        SaveRoundTrip(check);
        FilterChoicesMatchRules(check);
    }

    /// <summary>Drains both lanes of a belt end once (outside a connector), returning taken types per lane.</summary>
    static void Step(BeltWorld w, int tick, int drainTile, int[] got, List<int>? types = null)
    {
        if (drainTile >= 0)
        {
            w.Sim.OpenDrain(drainTile, 0);
            w.Sim.OpenDrain(drainTile, 1);
        }
        w.Tick(tick);
        for (int lane = 0; lane < 2 && drainTile >= 0; lane++)
        {
            if (w.Sim.TryTakeDrained(drainTile, lane, out int type))
            {
                got[lane]++;
                types?.Add(type);
            }
        }
    }

    static void AutoCurve(Action<bool, string> check)
    {
        // A at (0,1) points right into B's left side; B points up. One side feeder, no rear -> curve, lanes kept.
        var g = new Grid();
        Node a = g.Belt(0, 1, Right);
        Node b = g.Belt(1, 1, Up);
        BeltWorld w = g.Build();
        check(b.Shape == Shape.CurveLeft, $"curve: single left side feeder, no rear -> {b.Shape} (expect CurveLeft)");
        var g2 = new Grid();
        g2.Belt(2, 1, Left);
        Node b2 = g2.Belt(1, 1, Up);
        g2.Build();
        check(b2.Shape == Shape.CurveRight, $"curve: single right side feeder -> {b2.Shape} (expect CurveRight)");
        var g3 = new Grid();
        g3.Belt(0, 1, Right);
        g3.Belt(1, 2, Up);
        Node b3 = g3.Belt(1, 1, Up);
        g3.Build();
        check(b3.Shape == Shape.Straight, $"curve: side feeder + rear feeder -> {b3.Shape} (expect Straight, sideload)");
        var g4 = new Grid();
        g4.Belt(0, 1, Right);
        g4.Belt(2, 1, Left);
        Node b4 = g4.Belt(1, 1, Up);
        g4.Build();
        check(b4.Shape == Shape.Straight, $"curve: two side feeders -> {b4.Shape} (expect Straight)");
        // Lane preservation through the curve: only lane R fed on A arrives only on B lane R.
        int[] got = new int[2];
        int fed = 0;
        for (int t = 0; t < 900; t++)
        {
            fed += w.Sim.TryPutBack(a.Tile0, 1, Sulfur) ? 1 : 0;
            Step(w, t, b.Tile0, got);
        }
        check(got[0] == 0 && got[1] > 200 && got[1] <= fed, $"curve keeps lanes: fed R={fed} out L={got[0]} R={got[1]}");
    }

    /// <summary>
    /// Render math: every item point of each lane, for all 4 rotations x {straight, curve L, curve R}, stays inside its
    /// tile and inside the belt plate; straight lanes sit at +-LaneOffset (lane 0 left of travel), curve lanes on arcs
    /// of radius 0.5 -+ LaneOffset around the inner corner.
    /// </summary>
    static void ItemRender(Action<bool, string> check)
    {
        const float eps = 1e-4f, itemHalf = 0.11f; // ~0.22 tile nugget (BeltManager.ItemShrink)
        float o = BeltWorld.LaneOffset;
        check(o + itemHalf <= BeltWorld.SurfaceWidth / 2 + eps && o - itemHalf >= -eps,
            $"item render: lane offset {o} +- item {itemHalf} inside belt plate half {BeltWorld.SurfaceWidth / 2}");
        int bad = 0, total = 0;
        string first = "";
        for (int dir = 0; dir < 4; dir++)
        {
            var (dx, dy) = Dir[dir];
            int lx = dy, ly = -dx;
            for (int shape = 0; shape < 3; shape++)
            {
                var g = new Grid();
                Node b = g.Belt(5, 5, dir);
                int side = shape == 1 ? 1 : -1; // feeder sits on +left (CurveLeft) or -left (CurveRight)
                if (shape > 0)
                {
                    g.Belt(5 + lx * side, 5 + ly * side, side > 0 ? (dir + 1) % 4 : (dir + 3) % 4);
                }
                g.Build();
                Shape want = shape == 0 ? Shape.Straight : shape == 1 ? Shape.CurveLeft : Shape.CurveRight;
                if (b.Shape != want)
                {
                    bad++;
                    first = first.Length > 0 ? first : $"dir {dir} shape {b.Shape} != {want}";
                    continue;
                }
                for (int lane = 0; lane < 2; lane++)
                {
                    for (int pos = 0; pos <= LaneSim.TileLength; pos++)
                    {
                        total++;
                        var (x, y) = BeltWorld.ItemPoint(b, 0, lane, pos);
                        float rx = x - 5.5f, ry = y - 5.5f;
                        bool ok = Math.Abs(rx) <= 0.5f + eps && Math.Abs(ry) <= 0.5f + eps;
                        if (shape == 0)
                        {
                            float lateral = rx * lx + ry * ly;
                            ok &= Math.Abs(lateral - (lane == 0 ? o : -o)) < eps;
                        }
                        else
                        {
                            float px = 0.5f * (lx * side + dx), py = 0.5f * (ly * side + dy);
                            bool inner = (lane == 0) == (shape == 1);
                            float r = MathF.Sqrt((rx - px) * (rx - px) + (ry - py) * (ry - py));
                            ok &= Math.Abs(r - (inner ? 0.5f - o : 0.5f + o)) < eps;
                            // Arc points stay on the plate: lateral distance from the bend's centerline arc (radius 0.5) <= plate half.
                            ok &= Math.Abs(r - 0.5f) + itemHalf <= BeltWorld.SurfaceWidth / 2 + eps;
                        }
                        if (!ok)
                        {
                            bad++;
                            first = first.Length > 0 ? first : $"dir {dir} {want} lane {lane} pos {pos} -> ({rx:F3},{ry:F3})";
                        }
                    }
                }
            }
        }
        check(bad == 0, $"item render: {total} lane points (4 rotations x straight/curveL/curveR) inside tile and lane, bad={bad} {first}");
    }

    static void Sideload(Action<bool, string> check)
    {
        foreach (bool fromLeft in new[] { true, false })
        {
            // Main line (1,3)->(1,0) up; feeder from the left (0,1 pointing right) or right (2,1 pointing left).
            var g = new Grid();
            g.Belt(1, 3, Up);
            g.Belt(1, 2, Up);
            g.Belt(1, 1, Up);
            Node end = g.Belt(1, 0, Up);
            Node f = fromLeft ? g.Belt(0, 1, Right) : g.Belt(2, 1, Left);
            BeltWorld w = g.Build();
            int[] got = new int[2];
            for (int t = 0; t < 900; t++)
            {
                w.Sim.TryPutBack(f.Tile0, 0, Iron);
                w.Sim.TryPutBack(f.Tile0, 1, Iron);
                Step(w, t, end.Tile0, got);
            }
            int near = fromLeft ? 0 : 1;
            check(got[near] > 200 && got[near ^ 1] == 0, $"sideload from {(fromLeft ? "left" : "right")}: both feeder lanes -> near lane only: L={got[0]} R={got[1]}");
        }
    }

    static void SplitterSplitMerge(Action<bool, string> check)
    {
        // Splitter halves at (0,1),(1,1) pointing up; input belt behind half 0; output belts in front of both halves.
        {
            var g = new Grid();
            Node input = g.Belt(0, 2, Up);
            Node sp = g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0);
            Node o0 = g.Belt(0, 0, Up);
            Node o1 = g.Belt(1, 0, Up);
            BeltWorld w = g.Build();
            int[] g0 = new int[2], g1 = new int[2];
            int fed = 0;
            for (int t = 0; t < 3000; t++)
            {
                fed += w.Sim.TryPutBack(input.Tile0, 0, Sulfur) ? 1 : 0; // lane L only
                w.Sim.OpenDrain(o1.Tile0, 0);
                w.Sim.OpenDrain(o1.Tile0, 1);
                Step(w, t, o0.Tile0, g0);
                for (int lane = 0; lane < 2; lane++)
                {
                    g1[lane] += w.Sim.TryTakeDrained(o1.Tile0, lane, out _) ? 1 : 0;
                }
            }
            int a = g0[0], b = g1[0];
            check(Math.Abs(a - b) <= 1 && a + b > 600 && g0[1] + g1[1] == 0, $"splitter even split, lane kept: out0 L={a} out1 L={b} R={g0[1] + g1[1]} fed={fed}");
        }
        {
            // Merge: two inputs (one per half), only output 0 connected; output 1 cell empty.
            var g = new Grid();
            Node in0 = g.Belt(0, 2, Up);
            Node in1 = g.Belt(1, 2, Up);
            g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0);
            Node o0 = g.Belt(0, 0, Up);
            BeltWorld w = g.Build();
            int[] got = new int[2];
            var types = new List<int>();
            int fed0 = 0, fed1 = 0;
            for (int t = 0; t < 3000; t++)
            {
                if (t % 8 == 0)
                {
                    fed0 += w.Sim.TryPutBack(in0.Tile0, 0, Sulfur) ? 1 : 0;
                    fed1 += w.Sim.TryPutBack(in1.Tile0, 0, Iron) ? 1 : 0;
                }
                Step(w, t, o0.Tile0, got, types);
            }
            int s = types.Count(x => x == Sulfur), i = types.Count(x => x == Iron);
            check(s > 300 && i > 300 && Math.Abs(s - i) <= 2 && got[1] == 0, $"splitter merge into one output: sulfur={s} iron={i} (fed {fed0}/{fed1}) laneR={got[1]}");
        }
        {
            // Blocked output: output 1 belt is full and stuck, everything goes to output 0.
            var g = new Grid();
            Node input = g.Belt(0, 2, Up);
            g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0);
            Node o0 = g.Belt(0, 0, Up);
            Node o1 = g.Belt(1, 0, Up);
            BeltWorld w = g.Build();
            int[] got = new int[2];
            for (int t = 0; t < 3000; t++)
            {
                w.Sim.TryPutBack(input.Tile0, 0, Sulfur);
                Step(w, t, o0.Tile0, got);
            }
            check(w.Sim.Count(o1.Tile0, 0) == LaneSim.K && got[0] > 700, $"splitter blocked output -> free one: out0 L={got[0]}, out1 full={w.Sim.Count(o1.Tile0, 0)}");
        }
    }

    static void Connectors(Action<bool, string> check)
    {
        // Extractor: cargo (0,3) -> connector (0,2 up) -> belt (0,1),(0,0) -> loader connector (0,-1 up) -> weapon (0,-2).
        {
            var g = new Grid();
            Cargo cargo = g.Room(0, 3, new Cargo(8).With(Sulfur, 10).With(Iron, 10).With(Bullet, 10));
            Node cin = g.Connector(0, 2, Up, Bullet);
            g.Belt(0, 1, Up);
            g.Belt(0, 0, Up);
            Node cout = g.Connector(0, -1, Up, BeltTypes.FilterAny);
            var weapon = g.Room(0, -2, new Typed());
            weapon.InCap[Bullet] = 6;
            BeltWorld w = g.Build();
            check(cin.Mode == "room>belt" && cout.Mode == "belt>room", $"connector modes: {cin.Mode}, {cout.Mode}");
            for (int t = 0; t < 600; t++)
            {
                w.Tick(t);
            }
            int onBelt = Enumerable.Range(0, 2).Sum(n => w.Sim.Count(n, 0) + w.Sim.Count(n, 1) + (w.Sim.PeekDrained(n, 0) >= 0 ? 1 : 0) + (w.Sim.PeekDrained(n, 1) >= 0 ? 1 : 0));
            check(weapon.In[Bullet] == 6 && cargo.Total(Bullet) + onBelt + 6 == 10 && cargo.Total(Sulfur) == 10 && cargo.Total(Iron) == 10,
                $"cargo -(bullet)-> belt -> weapon: weapon={weapon.In[Bullet]}/6, cargo bullet={cargo.Total(Bullet)} onBelt={onBelt}, sulfur/iron untouched {cargo.Total(Sulfur)}/{cargo.Total(Iron)}");
        }
        // Loader filter blocks: belt carries iron then sulfur; loader filter sulfur -> iron head blocks, nothing delivered.
        {
            var g = new Grid();
            Node b0 = g.Belt(0, 1, Up);
            g.Belt(0, 0, Up);
            g.Connector(0, -1, Up, Sulfur);
            var factory = g.Room(0, -2, new Typed());
            factory.InCap[Sulfur] = 50;
            BeltWorld w = g.Build();
            w.Sim.TryInsert(b0.Tile0, 0, 200, Iron);
            w.Sim.TryInsert(b0.Tile0, 0, 100, Sulfur);
            w.Sim.TryInsert(b0.Tile0, 1, 100, Sulfur);
            for (int t = 0; t < 300; t++)
            {
                w.Tick(t);
            }
            check(factory.In[Sulfur] == 1, $"loader filter sulfur: lane R sulfur delivered, lane L blocked behind iron head: delivered={factory.In[Sulfur]} (expect 1)");
        }
        // Room to room with filter + rate: cargo -> factory (sulfur), factory bullets -> weapon (any).
        {
            var g = new Grid();
            Cargo cargo = g.Room(0, 2, new Cargo(4).With(Iron, 5).With(Sulfur, 15));
            g.Connector(0, 1, Up, Sulfur);
            var factory = g.Room(0, 0, new Typed());
            factory.InCap[Sulfur] = 10;
            factory.Out[Bullet] = 40;
            g.Connector(0, -1, Up, BeltTypes.FilterAny);
            var weapon = g.Room(0, -2, new Typed());
            weapon.InCap[Bullet] = 30;
            BeltWorld w = g.Build();
            for (int t = 0; t < 40; t++)
            {
                w.Tick(t);
            }
            check(factory.In[Sulfur] == 10 && cargo.Total(Sulfur) == 5 && cargo.Total(Iron) == 5 && weapon.In[Bullet] == 20,
                $"room->room: cargo->factory sulfur {factory.In[Sulfur]}/10 cap (iron kept {cargo.Total(Iron)}), factory->weapon any {weapon.In[Bullet]} in 40 ticks (expect 20 = 1/{BeltWorld.ConnectorPeriod} ticks)");
            for (int t = 40; t < 200; t++)
            {
                w.Tick(t);
            }
            check(weapon.In[Bullet] == 30 && factory.Out[Bullet] == 10, $"room->room respects sink capacity: weapon {weapon.In[Bullet]}/30, factory stock {factory.Out[Bullet]}");
        }
        // Belt to belt pass-through and side put -> far lane.
        {
            var g = new Grid();
            Cargo cargo = g.Room(-1, 1, new Cargo(4).With(Iron, 20));
            g.Connector(0, 1, Right, Iron); // on the main belt's left side, pushing right
            Node main = g.Belt(1, 1, Up);
            Node end = g.Belt(1, 0, Up);
            BeltWorld w = g.Build();
            int[] got = new int[2];
            for (int t = 0; t < 600; t++)
            {
                Step(w, t, end.Tile0, got);
            }
            check(got[1] == 20 && got[0] == 0 && cargo.Total(Iron) == 0, $"side connector -> far lane: L={got[0]} R={got[1]} (expect 0/20)");
        }
        // Owner layout (live bug): cargo -> connector(sulfur) -> splitter -> connectors(sulfur) -> 2x factory_ammo, no belt
        // tiles between. Factory input = vanilla SulfurStorage TypedResourceGrid 1x1 x sulfur MaxStackSize 5.
        foreach (bool ironOnB in new[] { false, true })
        {
            var g = new Grid();
            Cargo cargo = g.Room(0, 3, new Cargo(8).With(Sulfur, 20).With(Iron, 10));
            Node cin = g.Connector(0, 2, Up, Sulfur);
            g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0);
            Node ca = g.Connector(0, 0, Up, Sulfur);
            Node cb = g.Connector(1, 0, Up, ironOnB ? Iron : Sulfur);
            var fa = g.Room(0, -1, new Typed());
            var fb = g.Room(1, -1, new Typed());
            fa.InCap[Sulfur] = fb.InCap[Sulfur] = 5;
            BeltWorld w = g.Build();
            int maxPerWindow = 0;
            for (int t = 0; t < 600; t++)
            {
                int before = fa.In[Sulfur];
                w.Tick(t);
                maxPerWindow = Math.Max(maxPerWindow, fa.In[Sulfur] - before);
            }
            bool modes = cin.Mode == "room>splitter" || cin.Mode == "room>belt";
            check(modes && ca.Mode == "splitter>room" && cb.Mode == "splitter>room"
                && fa.In[Sulfur] == 5 && fb.In[Sulfur] == (ironOnB ? 0 : 5) && cargo.Total(Iron) == 10 && maxPerWindow <= 1,
                $"cargo -(sulfur)-> splitter -> connectors -> factories ({(ironOnB ? "B filter iron" : "both sulfur")}): A={fa.In[Sulfur]}/5 B={fb.In[Sulfur]}/{(ironOnB ? 0 : 5)}, iron kept {cargo.Total(Iron)}, modes {cin.Mode},{ca.Mode},{cb.Mode}");
        }
        // Splitter-fed connector rate: 1 item / ConnectorPeriod ticks.
        {
            var g = new Grid();
            Node input = g.Belt(0, 2, Up);
            g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0);
            g.Connector(0, 0, Up, BeltTypes.FilterAny);
            g.Connector(1, 0, Up, BeltTypes.FilterAny);
            var fa = g.Room(0, -1, new Typed());
            var fb = g.Room(1, -1, new Typed());
            fa.InCap[Iron] = fb.InCap[Iron] = 1000;
            BeltWorld w = g.Build();
            for (int t = 0; t < 2000; t++)
            {
                w.Sim.TryPutBack(input.Tile0, 0, Iron);
                w.Sim.TryPutBack(input.Tile0, 1, Iron);
                w.Tick(t);
            }
            int a = fa.In[Iron], b = fb.In[Iron];
            check(a > 300 && b > 300 && a <= 2000 / BeltWorld.ConnectorPeriod + 1 && b <= 2000 / BeltWorld.ConnectorPeriod + 1,
                $"splitter-fed connectors: A={a} B={b} in 2000 ticks (each <= {2000 / BeltWorld.ConnectorPeriod + 1})");
        }
        // Filter none = idle, in every mode.
        {
            var g = new Grid();
            Cargo cargo = g.Room(0, 3, new Cargo(4).With(Sulfur, 10));
            g.Connector(0, 2, Up, BeltTypes.FilterNone);
            Node b = g.Belt(0, 1, Up);
            g.Connector(0, 0, Up, BeltTypes.FilterNone);
            var sink = g.Room(0, -1, new Typed());
            sink.InCap[Sulfur] = 10;
            BeltWorld w = g.Build();
            w.Sim.TryInsert(b.Tile0, 0, 10, Sulfur);
            for (int t = 0; t < 300; t++)
            {
                w.Tick(t);
            }
            check(cargo.Total(Sulfur) == 10 && sink.In[Sulfur] == 0 && w.Sim.Count(b.Tile0, 0) == 1, $"filter none: idle (cargo {cargo.Total(Sulfur)}/10, sink {sink.In[Sulfur]}, belt item parked)");
            check(BeltTypes.FilterFromMode(0) == BeltTypes.FilterNone && BeltTypes.FilterFromMode(1) == BeltTypes.FilterAny && BeltTypes.FilterFromMode(2) == 0
                && BeltTypes.FilterFromMode(99) == BeltTypes.FilterNone, "filter toggle mode map: 0 none (default), 1 any, 2+i type i, out of range none");
        }
    }

    /// <summary>Busy network: cargo -> connector -> belt with sideload feeder -> splitter -> two loaders.</summary>
    static (Grid G, Cargo Src, Typed A, Typed B) Busy(Func<int, NodeState?> state, (Cargo, Typed, Typed)? rooms = null)
    {
        var g = new Grid();
        int k = 0;
        var (src, a, b) = rooms ?? (new Cargo(8).With(Sulfur, 20).With(Iron, 20), new Typed(), new Typed());
        g.Room(0, 6, src);
        g.Add(NodeKind.Connector, 0, 5, Up, state: state(k++)).Filter = BeltTypes.FilterAny;
        g.Add(NodeKind.Belt, 0, 4, Up, state: state(k++));
        g.Add(NodeKind.Belt, 0, 3, Up, state: state(k++));
        g.Add(NodeKind.Belt, -1, 3, Right, state: state(k++)); // side feeder; (0,3) also has a rear feeder -> sideload
        g.Add(NodeKind.Belt, 0, 2, Up, state: state(k++));
        g.Add(NodeKind.Splitter, 0, 1, Up, 1, 0, state(k++));
        g.Add(NodeKind.Belt, 0, 0, Up, state: state(k++));
        g.Add(NodeKind.Belt, 1, 0, Up, state: state(k++));
        g.Add(NodeKind.Connector, 0, -1, Up, state: state(k++)).Filter = BeltTypes.FilterAny;
        g.Add(NodeKind.Connector, 1, -1, Up, state: state(k++)).Filter = BeltTypes.FilterAny;
        g.Room(0, -2, a);
        g.Room(1, -2, b);
        a.InCap[Sulfur] = a.InCap[Iron] = 100;
        b.InCap[Sulfur] = b.InCap[Iron] = 100;
        return (g, src, a, b);
    }

    static int Digest(Grid g, Cargo src, Typed a, Typed b) =>
        g.Nodes.Aggregate(17, (h, n) => h * 31 + n.State.Digest()) * 31 + src.Total(Sulfur) * 7 + src.Total(Iron) + a.In.Sum() * 13 + b.In.Sum() * 29;

    static void SaveRoundTrip(Action<bool, string> check)
    {
        const int saveAt = 47;
        var (ga, sa, aa, ba) = Busy(_ => null);
        BeltWorld wa = ga.Build();
        for (int t = 0; t < saveAt; t++)
        {
            wa.Tick(t);
        }
        int[][] payloads = ga.Nodes.Select(n => n.State.Encode()).ToArray();
        NodeState?[] loaded = ga.Nodes.Select((n, i) => NodeState.Decode(n.Kind, payloads[i])).ToArray();
        bool allLoaded = loaded.All(s => s != null);
        bool sameDigest = allLoaded && ga.Nodes.Select((n, i) => n.State.Digest() == loaded[i]!.Digest()).All(x => x);
        int items = ga.Nodes.Sum(n => n.State.Tiles.Sum(t => t.Count[0] + t.Count[1]));
        check(allLoaded && sameDigest && items > 0, $"save round-trip: {ga.Nodes.Count} payloads decode, digests equal, {items} items on belts at tick {saveAt}");
        // Rooms are vanilla-saved; reproduce them at saveAt by a deterministic replay, then run original and reloaded in lockstep.
        var (gr, sr, ar, br) = Busy(_ => null);
        BeltWorld wr = gr.Build();
        for (int t = 0; t < saveAt; t++)
        {
            wr.Tick(t);
        }
        var (gb, sb, ab, bb) = Busy(i => loaded[i], (sr, ar, br));
        BeltWorld wb = gb.Build();
        int diverged = -1;
        for (int t = saveAt; t < 1200 && diverged < 0; t++)
        {
            wa.Tick(t);
            wb.Tick(t);
            if (Digest(ga, sa, aa, ba) != Digest(gb, sb, ab, bb))
            {
                diverged = t;
            }
        }
        check(diverged < 0 && aa.In.Sum() + ba.In.Sum() == 40, $"save round-trip: reloaded world matches original for 1153 ticks (diverged={diverged}), all 40 items delivered ({aa.In.Sum()}+{ba.In.Sum()})");
        int[] truncated = payloads[1][..^1];
        int[] badPos = payloads[1].ToArray();
        bool hasItem = badPos[2] > 0;
        if (hasItem)
        {
            badPos[4] = 9999;
        }
        check(NodeState.Decode(NodeKind.Belt, truncated) == null && NodeState.Decode(NodeKind.Splitter, payloads[1]) == null
            && (!hasItem || NodeState.Decode(NodeKind.Belt, badPos) == null) && NodeState.Decode(NodeKind.Belt, null) == null,
            $"save payload validation rejects truncated, wrong-kind, out-of-range (checked={hasItem}) and missing payloads");
    }
    static void FilterChoicesMatchRules(Action<bool, string> check)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "mod", "belt_filter_toggle.rules");
        if (!File.Exists(path))
        {
            check(false, "filter toggle rules not found: " + Path.GetFullPath(path));
            return;
        }
        var ids = Regex.Matches(File.ReadAllText(path), "ChoiceID\\s*=\\s*\"morgott\\.crewless_logistics\\.belt_filter\\.([a-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToArray();
        bool ok = ids.Length == BeltTypes.Ids.Length + 2 && ids[0] == "none" && ids[1] == "any" && ids.Skip(2).SequenceEqual(BeltTypes.Ids);
        check(ok, $"filter toggle rules: {ids.Length} choices none, any, then BeltTypes.Ids in order");
    }
}
