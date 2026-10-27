using System.Text.RegularExpressions;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>
/// §23 Factorio-style settings on the pure belt world: splitter input / output priority and filter (4 rotations x flip,
/// sides relative to travel), connector source priority, defaults = pre-§23 behaviour, settings outside the payload.
/// </summary>
static partial class WorldProbes
{
    /// <summary>A splitter at the origin in one rotation / flip, belts on its inputs and outputs, outputs into rooms.</summary>
    sealed class SplitRig
    {
        public readonly Grid G = new();
        public readonly Node Sp;
        public readonly Node[] In = new Node[2];
        public readonly Node[]?[] Out = new Node[2][];
        public readonly Typed?[] Sink = new Typed?[2];
        public readonly int Left;
        readonly (int X, int Y) _d, _s;

        /// <param name="outLen">Output belt length per half; 0 = output not connected, negative = belt chain with no taker.</param>
        public SplitRig(int dir, bool flip, int outLen0, int outLen1)
        {
            _d = Dir[dir];
            var (lx, ly) = LeftOf(dir);
            _s = flip ? (lx, ly) : (-lx, -ly);
            Sp = G.Add(NodeKind.Splitter, 0, 0, dir, _s.X, _s.Y);
            Left = BeltWorld.LeftHalf(Sp);
            int[] len = { outLen0, outLen1 };
            for (int h = 0; h < 2; h++)
            {
                var (x, y) = At(h, -1);
                In[h] = G.Belt(x, y, dir);
                int n = Math.Abs(len[h]);
                if (n == 0)
                {
                    continue;
                }
                Out[h] = new Node[n];
                for (int b = 0; b < n; b++)
                {
                    (x, y) = At(h, b + 1);
                    Out[h]![b] = G.Belt(x, y, dir);
                }
                if (len[h] > 0)
                {
                    (x, y) = At(h, n + 1);
                    G.Connector(x, y, dir, BeltTypes.FilterAny);
                    (x, y) = At(h, n + 2);
                    Typed sink = G.Room(x, y, new Typed());
                    sink.InCap[Iron] = sink.InCap[Sulfur] = 100000;
                    Sink[h] = sink;
                }
            }
        }

        (int X, int Y) At(int a, int b) => (a * _s.X + b * _d.X, a * _s.Y + b * _d.Y);

        /// <summary>Items of type on output side h: its belts (lanes + held) and its sink room.</summary>
        public int OutCount(BeltWorld w, int h, int type)
        {
            int n = Sink[h]?.In[type] ?? 0;
            foreach (Node b in Out[h] ?? Array.Empty<Node>())
            {
                for (int lane = 0; lane < 2; lane++)
                {
                    for (int i = 0; i < w.Sim.Count(b.Tile0, lane); i++)
                    {
                        n += w.Sim.Type(b.Tile0, lane, i) == type ? 1 : 0;
                    }
                    n += w.Sim.PeekDrained(b.Tile0, lane) == type ? 1 : 0;
                }
            }
            return n;
        }
    }

    static IEnumerable<(int Dir, bool Flip)> Orientations() => Enumerable.Range(0, 4).SelectMany(d => new[] { (d, false), (d, true) });

    /// <summary>Input priority: one output, both inputs fed compressed; the priority input alone flows, the other only after it stops.</summary>
    internal static void SplitterInputPriority(Action<bool, string> check)
    {
        int bad = 0, cases = 0;
        string first = "";
        foreach (var (dir, flip) in Orientations())
        {
            foreach (int prio in new[] { 1, 2 })
            {
                var r = new SplitRig(dir, flip, 3, 0);
                r.Sp.State.InPriority = prio;
                int ph = prio == 1 ? r.Left : r.Left ^ 1;
                BeltWorld w = r.G.Build();
                int warm = 0, otherBefore = -1;
                for (int t = 0; t < 600; t++)
                {
                    if (t < 300)
                    {
                        w.Sim.TryPutBack(r.In[ph].Tile0, t & 1, Iron);
                    }
                    w.Sim.TryPutBack(r.In[ph ^ 1].Tile0, t & 1, Sulfur);
                    w.Tick(t);
                    // Until the first priority items reach the splitter the priority input has a gap (Factorio: other input flows).
                    warm = t == 59 ? r.OutCount(w, 0, Sulfur) : warm;
                    otherBefore = t == 299 ? r.OutCount(w, 0, Sulfur) - warm : otherBefore;
                }
                int prioItems = r.OutCount(w, 0, Iron), otherAfter = r.OutCount(w, 0, Sulfur) - otherBefore;
                cases++;
                if (otherBefore != 0 || prioItems < 100 || otherAfter < 100)
                {
                    first = first.Length > 0 ? first : $" first: dir {dir} flip {flip} prio {prio}: other before {otherBefore}, priority {prioItems}, other after {otherAfter}";
                    bad++;
                }
            }
        }
        check(bad == 0, $"splitter input priority: {cases} cases (4 rotations x flip x left/right), other input 0 items while the priority input flows (after a 60-tick fill), then takes over, bad={bad}{first}");
    }

    /// <summary>Output priority: the priority output takes everything until its chain is full, then the other side gets the overflow.</summary>
    internal static void SplitterOutputPriority(Action<bool, string> check)
    {
        int bad = 0, cases = 0;
        string first = "";
        foreach (var (dir, flip) in Orientations())
        {
            foreach (int prio in new[] { 1, 2 })
            {
                // Priority side: 2 belts with no taker (fills after 16 items); other side: open chain into a room.
                int left = new SplitRig(dir, flip, 1, 1).Left, ph = prio == 1 ? left : left ^ 1;
                var r = new SplitRig(dir, flip, ph == 0 ? -2 : 3, ph == 1 ? -2 : 3);
                r.Sp.State.OutPriority = prio;
                BeltWorld w = r.G.Build();
                int prioAtFirstOverflow = -1;
                for (int t = 0; t < 600; t++)
                {
                    w.Sim.TryPutBack(r.In[0].Tile0, t & 1, Iron);
                    w.Tick(t);
                    if (prioAtFirstOverflow < 0 && r.OutCount(w, ph ^ 1, Iron) > 0)
                    {
                        prioAtFirstOverflow = r.OutCount(w, ph, Iron);
                    }
                }
                int full = r.OutCount(w, ph, Iron), other = r.OutCount(w, ph ^ 1, Iron);
                cases++;
                if (full != 16 || prioAtFirstOverflow < 15 || other < 100)
                {
                    first = first.Length > 0 ? first : $" first: dir {dir} flip {flip} prio {prio}: priority side {full} (at first overflow {prioAtFirstOverflow}), other {other}";
                    bad++;
                }
            }
        }
        check(bad == 0, $"splitter output priority: {cases} cases, priority output filled (16) before any overflow, rest to the other side, bad={bad}{first}");
    }

    /// <summary>Filter: matching items only to the output priority side (left when none), the rest only to the other side; a blocked side holds its items.</summary>
    internal static void SplitterFilter(Action<bool, string> check)
    {
        int bad = 0, cases = 0;
        string first = "";
        foreach (var (dir, flip) in Orientations())
        {
            foreach (int outPrio in new[] { 0, 1, 2 })
            {
                foreach (bool blockOther in new[] { false, true })
                {
                    int left = new SplitRig(dir, flip, 1, 1).Left, fs = outPrio == 2 ? left ^ 1 : left;
                    // blockOther: the non-filter side is a 1-belt chain with no taker (fills after 8 items).
                    var r = new SplitRig(dir, flip, fs == 0 || !blockOther ? 3 : -1, fs == 1 || !blockOther ? 3 : -1);
                    r.Sp.State.OutPriority = outPrio;
                    r.Sp.State.SplitFilter = Iron;
                    BeltWorld w = r.G.Build();
                    int fed = 0;
                    for (int t = 0; t < 600; t++)
                    {
                        fed += w.Sim.TryPutBack(r.In[t & 1].Tile0, (t >> 1) & 1, (t >> 2 & 1) == 0 ? Iron : Sulfur) ? 1 : 0;
                        w.Tick(t);
                    }
                    int ironF = r.OutCount(w, fs, Iron), sulfurF = r.OutCount(w, fs, Sulfur), ironO = r.OutCount(w, fs ^ 1, Iron), sulfurO = r.OutCount(w, fs ^ 1, Sulfur);
                    bool conserved = Held(w, r.Sink.Where(s => s != null).Cast<IRoom>()) == fed;
                    bool ok = sulfurF == 0 && ironO == 0 && conserved && (blockOther ? sulfurO == 8 && ironF > 0 : sulfurO > 20 && ironF > 20);
                    cases++;
                    if (!ok)
                    {
                        first = first.Length > 0 ? first : $" first: dir {dir} flip {flip} out {outPrio} blockOther {blockOther}: filter side iron {ironF} sulfur {sulfurF}, other iron {ironO} sulfur {sulfurO}, conserved {conserved}";
                        bad++;
                    }
                }
            }
        }
        check(bad == 0, $"splitter filter: {cases} cases (4 rotations x flip x output none/left/right x other side open/blocked), iron only to the filter side, sulfur only to the other, blocked side holds its lanes (head-of-line, Factorio), nothing lost, bad={bad}{first}");
    }

    /// <summary>
    /// Golden run recorded on the pre-§23 code (fcda794): splitters, a three-room connector and a belt into a connector,
    /// all settings 0. Any change of the default behaviour changes the digest.
    /// </summary>
    internal static void DefaultsUnchanged(Action<bool, string> check)
    {
        var g = new Grid();
        Node a = g.Belt(0, 3, Up), b = g.Belt(1, 3, Up);
        g.Add(NodeKind.Splitter, 0, 2, Up, 1, 0);
        g.Belt(0, 1, Up);
        g.Connector(0, 0, Up, BeltTypes.FilterAny);
        Typed sink = g.Room(0, -1, new Typed());
        sink.InCap[Iron] = sink.InCap[Sulfur] = 100000;
        Node side = g.Belt(1, 1, Up);
        g.Belt(1, 0, Left); // into the (0, 0) connector's right side
        Cargo back = g.Room(4, 1, new Cargo(4).With(Iron, 30));
        Cargo left = g.Room(3, 0, new Cargo(4).With(Sulfur, 30));
        Cargo right = g.Room(5, 0, new Cargo(4).With(Iron, 30));
        g.Connector(4, 0, Up, BeltTypes.FilterAny);
        Typed sink2 = g.Room(4, -1, new Typed());
        sink2.InCap[Iron] = sink2.InCap[Sulfur] = 100000;
        BeltWorld w = g.Build();
        for (int t = 0; t < 900; t++)
        {
            w.Sim.TryPutBack(a.Tile0, t & 1, Iron);
            w.Sim.TryPutBack(b.Tile0, t & 1, Sulfur);
            w.Tick(t);
        }
        uint h = 2166136261;
        NodeState.Add(ref h, w.Sim.Digest());
        foreach (Node n in g.Nodes)
        {
            NodeState.Add(ref h, n.State.RoundRobin[0]);
            NodeState.Add(ref h, n.State.RoundRobin[1]);
            NodeState.Add(ref h, n.State.Cooldown);
            NodeState.Add(ref h, n.State.Alternate);
        }
        foreach (int v in new[] { sink.In[Iron], sink.In[Sulfur], sink2.In[Iron], sink2.In[Sulfur], back.Total(Iron), left.Total(Sulfur), right.Total(Iron), side.Events[0] })
        {
            NodeState.Add(ref h, v);
        }
        const int Golden = -409863548;
        Probe.Info($"defaults digest {(int)h} sinks {sink.In[Iron]}/{sink.In[Sulfur]} {sink2.In[Iron]}/{sink2.In[Sulfur]}");
        check((int)h == Golden, $"all settings 0 reproduce the pre-§23 run: digest {(int)h} (golden {Golden})");
    }

    /// <summary>
    /// Connector source priority (4 rotations): the priority room gives everything while it has supply, the others only after;
    /// a belt at the priority side beats rooms and vice versa; round-robin default stays fair; a room on two sides counts on
    /// both; Codex counterexample (A feeds B's priority side from A's fallback room) in both node orders.
    /// </summary>
    internal static void ConnectorSourcePriority(Action<bool, string> check)
    {
        for (int dir = 0; dir < 4; dir++)
        {
            var (dx, dy) = Dir[dir];
            var (lx, ly) = LeftOf(dir);
            (int X, int Y)[] side = { (-dx, -dy), (lx, ly), (-lx, -ly) }; // back, left, right
            for (int p = 0; p <= 3; p++)
            {
                var g = new Grid();
                var rooms = new Cargo[3];
                for (int k = 0; k < 3; k++)
                {
                    rooms[k] = g.Room(side[k].X, side[k].Y, new Cargo(4).With(Iron, 10));
                }
                Node c = g.Connector(0, 0, dir, BeltTypes.FilterAny);
                c.State.SourcePriority = p;
                Typed sink = g.Room(dx, dy, new Typed());
                sink.InCap[Iron] = 1000;
                BeltWorld w = g.Build();
                int othersWhilePrio = 0;
                for (int t = 0; t < 80; t++)
                {
                    w.Tick(t);
                    if (p > 0 && rooms[p - 1].Total(Iron) > 0)
                    {
                        othersWhilePrio = Enumerable.Range(0, 3).Where(k => k != p - 1).Sum(k => 10 - rooms[k].Total(Iron));
                    }
                }
                int[] taken = rooms.Select(r => 10 - r.Total(Iron)).ToArray();
                bool ok = p == 0 ? sink.In[Iron] == 30 && taken.Max() - taken.Min() <= 0 : othersWhilePrio == 0 && sink.In[Iron] == 30;
                check(ok, $"connector source priority dir {dir} mode {p}: taken back/left/right {string.Join("/", taken)}, others while priority had supply {othersWhilePrio}, delivered {sink.In[Iron]}/30");
            }
            foreach (bool beltPrio in new[] { true, false })
            {
                // Belt (compressed iron) at the back, sulfur room at the left: priority back -> no sulfur while the belt feeds.
                var g = new Grid();
                Node belt = g.Belt(-dx, -dy, dir);
                Cargo room = g.Room(lx, ly, new Cargo(4).With(Sulfur, 20));
                Node c = g.Connector(0, 0, dir, BeltTypes.FilterAny);
                c.State.SourcePriority = beltPrio ? 1 : 2;
                Typed sink = g.Room(dx, dy, new Typed());
                sink.InCap[Iron] = sink.InCap[Sulfur] = 1000;
                BeltWorld w = g.Build();
                // Other source while the priority one has supply: belt priority after its first items reach the end
                // (t 30..200, fed compressed), room priority while the room is not empty.
                int warm = 0, other = 0;
                for (int t = 0; t < 400; t++)
                {
                    if (t < 200)
                    {
                        w.Sim.TryPutBack(belt.Tile0, t & 1, Iron);
                    }
                    w.Tick(t);
                    warm = t == 29 ? sink.In[Sulfur] : warm;
                    if (beltPrio && t == 199)
                    {
                        other = sink.In[Sulfur] - warm;
                    }
                    else if (!beltPrio && room.Total(Sulfur) > 0)
                    {
                        other = sink.In[Iron];
                    }
                }
                check(other == 0 && sink.In[Iron] > 50 && sink.In[Sulfur] == 20,
                    $"connector priority {(beltPrio ? "back belt over left room" : "left room over back belt")} dir {dir}: other source while priority had supply {other}, iron {sink.In[Iron]}, sulfur {sink.In[Sulfur]}/20");
            }
            {
                // One room at the back and the left (L-shaped neighbour), priority left: it is still the priority room.
                var g = new Grid();
                Cargo both = g.Room(-dx, -dy, new Cargo(4).With(Iron, 10));
                g.Room(lx, ly, both);
                Cargo right = g.Room(-lx, -ly, new Cargo(4).With(Sulfur, 10));
                Node c = g.Connector(0, 0, dir, BeltTypes.FilterAny);
                c.State.SourcePriority = 2;
                Typed sink = g.Room(dx, dy, new Typed());
                sink.InCap[Iron] = sink.InCap[Sulfur] = 1000;
                BeltWorld w = g.Build();
                for (int t = 0; t < 20; t++)
                {
                    w.Tick(t);
                }
                check(sink.In[Iron] == 10 && sink.In[Sulfur] == 0, $"connector priority dir {dir}: room on back+left is the left priority room ({sink.In[Iron]} iron, {sink.In[Sulfur]} sulfur in 20 ticks)");
            }
        }
        foreach (var (filtered, connFirst) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            // Codex review P1: a splitter (filter iron -> left, or output priority right) holds sulfur it will never hand to
            // its left output; that output feeds the back (priority side) of a connector with an iron room on its left. The
            // held sulfur must not claim the connector forever.
            var g = new Grid();
            Node? c = connFirst ? g.Connector(0, 1, Up, BeltTypes.FilterAny) : null; // node order: Extract before Route
            Node input = g.Belt(0, 3, Up);
            Node sp = g.Add(NodeKind.Splitter, 0, 2, Up, 1, 0);
            if (filtered)
            {
                sp.State.SplitFilter = Iron;
            }
            else
            {
                sp.State.OutPriority = 2;
                g.Belt(1, 1, Up); // right output open (no taker: fills, then the overflow goes left)
            }
            c ??= g.Connector(0, 1, Up, BeltTypes.FilterAny);
            c.State.SourcePriority = 1;
            Cargo room = g.Room(-1, 1, new Cargo(4).With(Iron, 20));
            Typed sink = g.Room(0, 0, new Typed());
            sink.InCap[Iron] = sink.InCap[Sulfur] = 1000;
            BeltWorld w = g.Build();
            int ironAt30 = 0;
            for (int t = 0; t < 100; t++)
            {
                room.With(Iron, 5); // endless side room (Codex review 2: an overflow must not starve behind it)
                w.Sim.TryPutBack(input.Tile0, t & 1, Sulfur);
                w.Tick(t);
                ironAt30 = t == 29 ? sink.In[Iron] : ironAt30;
            }
            // Filter: sulfur never goes left, the room gets every slot. Priority right: right output fills (8), then the
            // sulfur overflow claims the connector and the room only fills the gaps.
            bool ok = filtered ? sink.In[Iron] >= 45 && sink.In[Sulfur] == 0 : sink.In[Sulfur] >= 25 && sink.In[Iron] - ironAt30 <= 8;
            check(ok, $"connector priority: splitter ({(filtered ? "filter iron -> left" : "output priority right, overflow left")}, {(connFirst ? "connector" : "splitter")} first) into the priority side, endless side room: iron {sink.In[Iron]} (at t30 {ironAt30}), sulfur {sink.In[Sulfur]}");
        }
        {
            // Codex review 3: the splitter's priority output (right) is a connector claimed by its own endless room; the
            // overflow must claim the left connector (priority back) against its endless side room. Connectors run first.
            var g = new Grid();
            Node cl = g.Connector(0, 1, Up, BeltTypes.FilterAny), cr = g.Connector(1, 1, Up, BeltTypes.FilterAny);
            cl.State.SourcePriority = 1;
            cr.State.SourcePriority = 3;
            Node input = g.Belt(0, 3, Up);
            g.Add(NodeKind.Splitter, 0, 2, Up, 1, 0).State.OutPriority = 2;
            Cargo sideL = g.Room(-1, 1, new Cargo(4)), sideR = g.Room(2, 1, new Cargo(4));
            Typed sinkL = g.Room(0, 0, new Typed()), sinkR = g.Room(1, 0, new Typed());
            sinkL.InCap[Iron] = sinkL.InCap[Sulfur] = sinkR.InCap[Iron] = sinkR.InCap[Sulfur] = 1000;
            BeltWorld w = g.Build();
            for (int t = 0; t < 100; t++)
            {
                sideL.With(Iron, 5);
                sideR.With(Iron, 5);
                w.Sim.TryPutBack(input.Tile0, t & 1, Sulfur);
                w.Tick(t);
            }
            check(sinkL.In[Sulfur] >= 25 && sinkR.In[Sulfur] == 0,
                $"connector priority: overflow past a connector claimed by its own room reaches the left priority side: left sulfur {sinkL.In[Sulfur]} iron {sinkL.In[Iron]}, right sulfur {sinkR.In[Sulfur]} iron {sinkR.In[Iron]}");
        }
        {
            // Codex review 4: both inputs full, the priority output (right) is a connector that takes one item per period,
            // so the second held item overflows left every tick; the left connector (priority back, endless side room,
            // run first) must be claimed by that overflow.
            var g = new Grid();
            Node cl = g.Connector(0, 1, Up, BeltTypes.FilterAny);
            g.Connector(1, 1, Up, BeltTypes.FilterAny);
            cl.State.SourcePriority = 1;
            Node in0 = g.Belt(0, 3, Up), in1 = g.Belt(1, 3, Up);
            g.Add(NodeKind.Splitter, 0, 2, Up, 1, 0).State.OutPriority = 2;
            Cargo sideL = g.Room(-1, 1, new Cargo(4));
            Typed sinkL = g.Room(0, 0, new Typed()), sinkR = g.Room(1, 0, new Typed());
            sinkL.InCap[Iron] = sinkL.InCap[Sulfur] = sinkR.InCap[Sulfur] = 1000;
            BeltWorld w = g.Build();
            for (int t = 0; t < 100; t++)
            {
                sideL.With(Iron, 5);
                w.Sim.TryPutBack(in0.Tile0, t & 1, Sulfur);
                w.Sim.TryPutBack(in1.Tile0, t & 1, Sulfur);
                w.Tick(t);
            }
            check(sinkL.In[Sulfur] >= 25 && sinkR.In[Sulfur] >= 30,
                $"connector priority: a second held item overflows past a one-per-period priority output: left sulfur {sinkL.In[Sulfur]} iron {sinkL.In[Iron]}, right sulfur {sinkR.In[Sulfur]}");
        }
        {
            // Codex review 5: lane 0 carries sulfur, lane 1 iron; left connector (filter iron, priority back, endless iron
            // side room, run first) rejects sulfur, which falls back right and must reserve the right output in the trial.
            // Without that, held iron never claims the left connector and the iron lane stalls for good.
            var g = new Grid();
            Node cl = g.Connector(0, 1, Up, Iron);
            g.Connector(1, 1, Up, BeltTypes.FilterAny);
            cl.State.SourcePriority = 1;
            Node in0 = g.Belt(0, 3, Up), in1 = g.Belt(1, 3, Up);
            Node sp = g.Add(NodeKind.Splitter, 0, 2, Up, 1, 0);
            sp.State.RoundRobin[1] = 1;
            Cargo sideL = g.Room(-1, 1, new Cargo(4));
            Typed sinkL = g.Room(0, 0, new Typed()), sinkR = g.Room(1, 0, new Typed());
            sinkL.InCap[Iron] = sinkR.InCap[Iron] = sinkR.InCap[Sulfur] = 100000;
            BeltWorld w = g.Build();
            int fedIron = 0, fedAt100 = 0;
            for (int t = 0; t < 400; t++)
            {
                sideL.With(Iron, 5);
                w.Sim.TryPutBack(in0.Tile0, 0, Sulfur);
                w.Sim.TryPutBack(in1.Tile0, 0, Sulfur);
                fedIron += (w.Sim.TryPutBack(in0.Tile0, 1, Iron) ? 1 : 0) + (w.Sim.TryPutBack(in1.Tile0, 1, Iron) ? 1 : 0);
                w.Tick(t);
                fedAt100 = t == 99 ? fedIron : fedAt100;
            }
            check(fedIron - fedAt100 >= 120, $"connector priority: mixed lanes, filtered priority connector: iron lane keeps flowing (fed {fedAt100} by t100, {fedIron - fedAt100} after; sinks L iron {sinkL.In[Iron]}, R iron {sinkR.In[Iron]} sulfur {sinkR.In[Sulfur]})");
        }
        foreach (bool bFirst in new[] { false, true })
        {
            // Codex D6: A (priority back, back room empty, left room iron) feeds B's back; B (priority back) has a sulfur room
            // on its left. B must take A's iron, never its own room, while A has supply.
            var g = new Grid();
            Node? b = bFirst ? g.Connector(0, 0, Up, BeltTypes.FilterAny) : null;
            g.Room(0, 2, new Cargo(4));
            g.Room(-1, 1, new Cargo(4).With(Iron, 20));
            Node a = g.Connector(0, 1, Up, BeltTypes.FilterAny);
            b ??= g.Connector(0, 0, Up, BeltTypes.FilterAny);
            Cargo bLeft = g.Room(-1, 0, new Cargo(4).With(Sulfur, 20));
            a.State.SourcePriority = b.State.SourcePriority = 1;
            Typed sink = g.Room(0, -1, new Typed());
            sink.InCap[Iron] = sink.InCap[Sulfur] = 1000;
            BeltWorld w = g.Build();
            int sulfurWhileIron = 0;
            for (int t = 0; t < 120; t++)
            {
                w.Tick(t);
                if (sink.In[Iron] < 20)
                {
                    sulfurWhileIron = sink.In[Sulfur];
                }
            }
            check(sink.In[Iron] == 20 && sulfurWhileIron == 0 && bLeft.Total(Sulfur) == 0,
                $"connector chain A->B, B priority = A's side ({(bFirst ? "B" : "A")} first in node order): iron {sink.In[Iron]}/20, sulfur before A ran dry {sulfurWhileIron}, then B's own room {20 - bLeft.Total(Sulfur)}/20");
        }
    }

    /// <summary>
    /// Settings live in vanilla UIToggles (RememberValue: saved with the part, copied with blueprints, MP-synced), not in the
    /// payload: encode/decode ignores them and decodes to the defaults; out-of-range modes read as 0; they are hashed; rules
    /// declare the toggles on the parts with Default 0 and the choice order the sim expects.
    /// </summary>
    internal static void SettingsOutsidePayload(Action<bool, string> check)
    {
        var s = new NodeState(NodeKind.Splitter) { InPriority = 1, OutPriority = 2, SplitFilter = Iron };
        var plain = new NodeState(NodeKind.Splitter);
        NodeState? d = NodeState.Decode(NodeKind.Splitter, s.Encode());
        check(s.Encode().SequenceEqual(plain.Encode()) && d is { InPriority: 0, OutPriority: 0, SplitFilter: -1, SourcePriority: 0 } && s.Digest() != plain.Digest(),
            "settings not in the payload (v2 unchanged), decode -> defaults, digest covers them");
        check(NodeState.Setting(null, 3) == 0 && NodeState.Setting(3, 3) == 0 && NodeState.Setting(-1, 4) == 0 && NodeState.Setting(2, 3) == 2,
            "toggle mode -> setting: missing / out of range -> 0");
        string mod = Path.Combine(Env.ProjectRoot, "mod");
        (string File, string Id, string[] Choices)[] toggles =
        {
            ("belt_input_priority_toggle.rules", "morgott.crewless_logistics.belt_input_priority", new[] { "none", "left", "right" }),
            ("belt_output_priority_toggle.rules", "morgott.crewless_logistics.belt_output_priority", new[] { "none", "left", "right" }),
            ("belt_source_priority_toggle.rules", "morgott.crewless_logistics.belt_source_priority", new[] { "round_robin", "back", "left", "right" }),
            ("belt_splitter_filter_toggle.rules", "morgott.crewless_logistics.belt_splitter_filter", new[] { "none" }.Concat(BeltTypes.Ids).ToArray()),
        };
        string modRules = File.ReadAllText(Path.Combine(mod, "mod.rules"));
        foreach (var (file, id, choices) in toggles)
        {
            string text = File.ReadAllText(Path.Combine(mod, file));
            var ids = Regex.Matches(text, "ChoiceID\\s*=\\s*\"" + Regex.Escape(id) + "\\.([a-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToArray();
            check(text.Contains($"ToggleID = \"{id}\"") && ids.SequenceEqual(choices) && modRules.Contains($"&<{file}>/Toggle"),
                $"{file}: {ids.Length} choices in sim order, registered in mod.rules");
        }
        foreach (var (part, ids) in new[] { ("belt_splitter.rules", new[] { toggles[0].Id, toggles[1].Id, toggles[3].Id }), ("belt_connector.rules", new[] { "morgott.crewless_logistics.belt_filter", toggles[2].Id }) })
        {
            string text = File.ReadAllText(Path.Combine(mod, part));
            bool ok = ids.All(i => Regex.IsMatch(text, "Type = UIToggle\\s+ToggleID = \"" + Regex.Escape(i) + "\"\\s+Default = 0\\s+RequiresCommand = false\\s+RememberValue = true"));
            check(ok, $"{part}: UIToggle components {string.Join(", ", ids.Select(i => i.Split('.')[^1]))} with Default 0 + RememberValue");
        }
    }
}
