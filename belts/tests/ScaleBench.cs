using System.Diagnostics;
using CrewlessLogistics.Belts;
using Xunit;

namespace CrewlessLogistics.Belts.Tests;

public class ScaleTests
{
    [Fact]
    public void FleetBench() => Probe.Run(WorldProbes.FleetBench);

    [Fact]
    public void FleetGolden() => Probe.Run(WorldProbes.FleetGolden);

    [Fact]
    public void RenderLod() => Probe.Run(WorldProbes.RenderLod);
}

/// <summary>
/// §27 scale bench: the owner target of ~20 big ships full of belts, headless (pure sim + client render state; the game's
/// quad writes are measured in game by the P3 perf line). One ship = 20 modules of a 100-tile clockwise loop (4 curves),
/// fed by a source connector from an endless room, with a splitter diverting into a 3-tile branch, a connector and an
/// endless sink room: straights, curves, sideloads, splitters, connectors and rooms, items always flowing.
/// </summary>
static partial class WorldProbes
{
    /// <summary>Endless source / sink of one type (a big cargo hold that never empties nor fills).</summary>
    sealed class Endless : IRoom
    {
        readonly int _type;

        public Endless(int type) => _type = type;

        public int FindSupply(int filter, IRoom? acceptor)
        {
            int t = filter < 0 ? _type : filter;
            return acceptor == null || acceptor.CanAccept(t) ? t : -1;
        }

        public bool Take(int type) => true;

        public bool CanAccept(int type) => true;

        public bool Put(int type) => true;
    }

    const int LoopW = 30, LoopH = 22, Modules = 20, ModulesPerRow = 5;

    /// <summary>One big ship: <see cref="Modules"/> loop modules, ~2000 lane tiles.</summary>
    static Grid BigShip(int seed)
    {
        var g = new Grid();
        for (int m = 0; m < Modules; m++)
        {
            int ox = (m % ModulesPerRow) * (LoopW + 10), oy = (m / ModulesPerRow) * (LoopH + 10);
            int type = (seed * 7 + m) % BeltTypes.Ids.Length;
            int mid = ox + LoopW / 2;
            // Perimeter clockwise (screen y down): top row right, right column down, bottom row left, left column up.
            var cells = new List<(int X, int Y)>();
            for (int x = 0; x < LoopW; x++) cells.Add((ox + x, oy));
            for (int y = 1; y < LoopH; y++) cells.Add((ox + LoopW - 1, oy + y));
            for (int x = LoopW - 2; x >= 0; x--) cells.Add((ox + x, oy + LoopH - 1));
            for (int y = LoopH - 2; y >= 1; y--) cells.Add((ox, oy + y));
            for (int i = 0; i < cells.Count; i++)
            {
                var (x, y) = cells[i];
                var (nx, ny) = cells[(i + 1) % cells.Count];
                int dir = Array.IndexOf(Dir, (nx - x, ny - y));
                if (y == oy + LoopH - 1 && x == mid)
                {
                    // Half 1 below the loop, its output starts the branch; §23 settings mixed across modules.
                    NodeState st = g.Add(NodeKind.Splitter, x, y, dir, 0, 1).State;
                    st.OutPriority = m % 3;
                    st.InPriority = m % 2;
                    st.SplitFilter = m % 5 == 4 ? type : -1;
                }
                else
                {
                    g.Belt(x, y, dir);
                }
            }
            for (int k = 1; k <= 3; k++)
            {
                g.Belt(mid - k, oy + LoopH, Left);
            }
            g.Connector(mid - 4, oy + LoopH, Left, BeltTypes.FilterAny);
            g.Room(mid - 5, oy + LoopH, new Endless(type));
            // Source: a connector above the top row feeds it from the side (far lane), endless room behind it.
            g.Connector(ox + 3, oy - 1, Down, BeltTypes.FilterAny).State.SourcePriority = m % 4 == 2 ? 1 : 0;
            g.Room(ox + 3, oy - 2, new Endless(type));
        }
        return g;
    }

    internal sealed class Fleet
    {
        public readonly List<BeltWorld> Worlds = new();
        public readonly List<BeltRenderState> Render = new();
        public int Tiles, Nodes;

        public Fleet(int ships)
        {
            for (int s = 0; s < ships; s++)
            {
                Grid g = BigShip(s);
                BeltWorld w = g.Build();
                var r = new BeltRenderState();
                r.Reset(g.Nodes);
                Worlds.Add(w);
                Render.Add(r);
                Tiles += w.Sim.Tiles;
                Nodes += g.Nodes.Count;
            }
        }

        public void Tick(int t)
        {
            foreach (BeltWorld w in Worlds)
            {
                w.Tick(t);
            }
        }

        /// <summary>Client per-tick render work that is pure: lamp / moving state and every drawn item's point.</summary>
        public void RenderTick(int t)
        {
            for (int s = 0; s < Worlds.Count; s++)
            {
                Render[s].Update(t);
                IReadOnlyList<Node> nodes = Worlds[s].Nodes;
                for (int j = 0; j < nodes.Count; j++)
                {
                    Node n = nodes[j];
                    for (int half = 0; BeltRenderState.DrawsItems(n) && half < n.State.Tiles.Length; half++)
                    {
                        LaneTile lt = n.State.Tiles[half];
                        for (int lane = 0; lane < 2; lane++)
                        {
                            for (int i = 0; i < lt.Count[lane]; i++)
                            {
                                var (x, y) = BeltWorld.ItemPoint(n, half, lane, lt.Pos[lane * LaneSim.K + i]);
                                _sink += x + y;
                            }
                        }
                    }
                }
            }
        }

        float _sink;

        /// <summary>Quads one ship draws now: items (belts only), surfaces (belt tiles), lamps (1 per connector, 2 per splitter).</summary>
        public (int Items, int Surfaces, int Lamps) Quads(int s)
        {
            int items = 0, surf = 0, lamps = 0;
            foreach (Node n in Worlds[s].Nodes)
            {
                if (n.Kind == NodeKind.Belt)
                {
                    surf++;
                    items += n.State.Tiles[0].Count[0] + n.State.Tiles[0].Count[1];
                }
                else
                {
                    lamps += n.Kind == NodeKind.Splitter ? 2 : 1;
                }
            }
            return (items, surf, lamps);
        }

        public int Digest()
        {
            uint h = 2166136261;
            foreach (BeltWorld w in Worlds)
            {
                NodeState.Add(ref h, w.Sim.Digest());
                for (int i = 0; i < w.Nodes.Count; i++)
                {
                    NodeState.Add(ref h, w.Nodes[i].State.Digest());
                }
            }
            return (int)h;
        }
    }

    /// <summary>§27 golden digest: the perf refactors keep the fleet sim bit-identical (value from the pre-§27 code).</summary>
    internal static void FleetGolden(Action<bool, string> check)
    {
        var fleet = new Fleet(20);
        for (int t = 0; t < 9000; t++)
        {
            fleet.Tick(t);
        }
        const int golden = 768269553;
        int d = fleet.Digest();
        check(d == golden, $"fleet golden digest after 9000 ticks: {d} (golden {golden})");
    }

    /// <summary>
    /// §27 client LOD: coarse threshold with hysteresis (out of every view = infinity = coarse), and Rebase after a gap in
    /// render updates shows no stale activity (no lamp flash, surfaces still) until the next transfer / move.
    /// </summary>
    internal static void RenderLod(Action<bool, string> check)
    {
        float c = BeltRenderState.CoarseTiles;
        check(!BeltRenderState.Coarse(false, c * 1.1f - 1) && BeltRenderState.Coarse(false, c * 1.1f + 1)
            && BeltRenderState.Coarse(true, c * 0.9f + 1) && !BeltRenderState.Coarse(true, c * 0.9f - 1) && BeltRenderState.Coarse(false, float.PositiveInfinity),
            $"coarse LOD at {c} tiles: fine->coarse above {c * 1.1f}, coarse->fine below {c * 0.9f}, no view = coarse");
        var fleet = new Fleet(1);
        BeltRenderState r = fleet.Render[0];
        int t = 0;
        bool litBefore = false, movingBefore = false;
        for (; t < 600; t++)
        {
            fleet.Tick(t);
            r.Update(t);
            litBefore |= t >= 590 && r.Lamp.Any(x => x);
            movingBefore |= t >= 590 && r.Moving.Any(x => x);
        }
        for (; t < 700; t++) // out of view: sim runs, render state does not
        {
            fleet.Tick(t);
        }
        r.Rebase();
        r.Update(t);
        bool litAfter = r.Lamp.Any(x => x), movingAfter = r.Moving.Any(x => x);
        bool litLater = false, movingLater = false;
        for (t++; t < 720; t++)
        {
            fleet.Tick(t);
            r.Update(t);
            litLater |= r.Lamp.Any(x => x);
            movingLater |= r.Moving.Any(x => x);
        }
        check(litBefore && movingBefore && !litAfter && !movingAfter && litLater && movingLater,
            $"rebase after 100 unrendered ticks: lamps/moving before {litBefore}/{movingBefore}, right after {litAfter}/{movingAfter} (want false), later {litLater}/{movingLater}");
    }

    /// <summary>
    /// §27: 20 ships x ~2000 tiles. Budget (Release, this PC): sim &lt;= 1000 us/tick for the fleet at 30 Hz, 0 B/tick;
    /// render state (every ship in view, worst case) &lt;= 2000 us/tick, 0 B/tick. Time budgets are checked in Release only:
    /// unoptimised Debug JIT runs render state ~4x slower than Release, so any Debug budget is just a guess of that ratio and
    /// failed under machine load (the quality gate runs tests in Debug); Debug still checks 0 B and prints the numbers.
    /// </summary>
    internal static void FleetBench(Action<bool, string> check)
    {
        bool release = !Debugger.IsAttached && typeof(WorldProbes).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        bool Within(double v, double budget) => !release || v < budget;
        string Budget(double b, string unit) => release ? $"budget {b:F0} {unit}" : "no time budget in Debug";
        var fleet = new Fleet(20);
        for (int t = 0; t < 9000; t++) // warm-up past tiered JIT, belts reach steady flow
        {
            fleet.Tick(t);
            fleet.RenderTick(t);
        }
        var (simUs, simAlloc, simW) = Meter.Run(fleet.Tick, 9000, 3000);
        var (renUs, renAlloc, renW) = Meter.Run(fleet.RenderTick, 18000, 3000);
        var (items, surf, lamps) = fleet.Quads(0);
        int allItems = Enumerable.Range(0, 20).Sum(s => fleet.Quads(s).Items);
        check(Within(simUs, 1000) && simAlloc == 0,
            $"fleet sim ({(release ? "Release" : "Debug")}): 20 ships, {fleet.Tiles} tiles, {fleet.Nodes} nodes, {allItems} items: {simUs:F0} us/tick ({Budget(1000, "us")}), alloc {simAlloc} B (windows {simW})");
        check(Within(renUs, 2000) && renAlloc == 0,
            $"fleet render state: {renUs:F0} us/tick ({Budget(2000, "us")}, all 20 ships in view), alloc {renAlloc} B (windows {renW}); quads ship 0: {items} items + {surf} surfaces + {lamps} lamps");
        // Topology rebuild of one big ship (a part built / destroyed rebuilds that ship's world once, in the next tick).
        Grid g = BigShip(0);
        g.Build();
        long ts0 = Stopwatch.GetTimestamp();
        const int reps = 20;
        for (int i = 0; i < reps; i++)
        {
            g.Build();
        }
        double buildMs = Stopwatch.GetElapsedTime(ts0).TotalMilliseconds / reps;
        check(Within(buildMs, 20), $"rebuild one ship ({g.Nodes.Count} nodes): {buildMs:F2} ms ({Budget(20, "ms")})");
    }
}
