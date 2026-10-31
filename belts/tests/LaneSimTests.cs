using System.Diagnostics;
using CrewlessLogistics.Belts;
using Xunit;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>P3: lane sim correctness and cost.</summary>
public class LaneSimTests
{
    [Fact]
    public void PackedStraightLineKeepsInvariantsAtFullSpeed() => Probe.Run(check =>
    {
        // Packed straight line: injector at the start, loader at the end. Expected 512/30/64 items/tick = 8 items/s per lane.
        var sim = Chain(10);
        int[] got = new int[2];
        int tick = 0;
        for (; tick < 300; tick++)
        {
            StepFeedDrain(sim, tick, 0, 9, null);
        }
        bool ok = true;
        for (; tick < 3300; tick++)
        {
            StepFeedDrain(sim, tick, 0, 9, got);
            ok &= Invariants(sim, out _);
        }
        check(ok, "P3 straight 10 tiles: gap/order/capacity invariants every tick");
        check(got[0] is >= 795 and <= 805 && got[1] is >= 795 and <= 805, $"P3 packed line throughput {got[0]}/{got[1]} items per 100 s per lane (expect 800 = 8/s, full speed)");
    });

    [Fact]
    public void HalfFullLoopFlowsAndConservesItems() => Probe.Run(check =>
    {
        var sim = Loop(20);
        Fill(sim, 2);
        int before = Total(sim);
        int d0 = sim.Digest();
        bool ok = true;
        for (int t = 0; t < 10000; t++)
        {
            sim.Tick(t);
            ok &= Invariants(sim, out _);
        }
        check(ok && Total(sim) == before && sim.Digest() != d0, $"P3 loop 20 tiles 50%: {Total(sim)}/{before} items conserved, invariants, moving");
    });

    [Fact]
    public void FullyPackedLoopFreezes()
    {
        // Known limit: a 100% packed closed loop freezes (cycle root sees its successor's old tail). Documented in spec §3.
        var sim = Loop(8);
        Fill(sim, 4);
        int h0 = sim.Pos(0, 0, 0);
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(t);
        }
        Probe.Info($"P3 100% packed loop moves: {sim.Pos(0, 0, 0) != h0} (expected False, accepted limit)");
    }

    [Fact]
    public void SideloadLeftLandsOnLaneL() => Probe.Run(check =>
    {
        // Sideload: feeder (tile 6) into the left side of straight tile 3 of a 6-tile line; all feeder items land on lane L.
        int[] next = { 1, 2, 3, 4, 5, -1, 3 };
        var entry = new Entry[7];
        entry[6] = Entry.SideLeft;
        var sim = new LaneSim(7);
        sim.Build(next, entry);
        int[] got = new int[2];
        int fed = 0;
        bool ok = true;
        for (int t = 0; t < 3000; t++)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                fed += sim.TryPutBack(6, lane, 7) ? 1 : 0;
                sim.OpenDrain(5, lane);
            }
            sim.Tick(t);
            for (int lane = 0; lane < 2; lane++)
            {
                if (sim.TryTakeDrained(5, lane, out int type))
                {
                    got[lane]++;
                    ok &= type == 7;
                }
            }
            ok &= Invariants(sim, out _);
        }
        int onBelt = Total(sim);
        check(ok && got[1] == 0 && got[0] + onBelt == fed && got[0] > 700, $"P3 sideload left -> lane L only: L={got[0]} R={got[1]} fed={fed} onBelt={onBelt}");
    });

    [Fact]
    public void BeltEndPacksAndStops() => Probe.Run(check =>
    {
        // Belt end: 4 items pack at 255,191,127,63 and stop.
        var sim = Chain(1);
        for (int t = 0; t < 200; t++)
        {
            sim.TryInsert(0, 0, 0, 1);
            sim.Tick(t);
        }
        check(sim.Count(0, 0) == 4 && sim.Pos(0, 0, 0) == 255 && sim.Pos(0, 0, 3) == 63, $"P3 belt end stop: count={sim.Count(0, 0)} head={sim.Pos(0, 0, 0)} tail={sim.Pos(0, 0, 3)}");
    });

    [Fact]
    public void HeldDrainRejoinsLaneAfterRebuild() => Probe.Run(check =>
    {
        // Held drain on a packed end tile that becomes mid-belt (connector replaced by a belt) rejoins the lane, not stuck.
        var sim = new LaneSim(2);
        sim.Build(new[] { -1, -1 }, new Entry[2]);
        int t = 0;
        for (; t < 200; t++)
        {
            sim.TryInsert(0, 0, 0, 1);
            sim.Tick(t);
        }
        sim.OpenDrain(0, 0);
        sim.Tick(t++);
        for (int i = 0; i < 200; i++, t++)
        {
            sim.TryInsert(0, 0, 0, 1);
            sim.Tick(t);
        }
        bool heldPacked = sim.PeekDrained(0, 0) == 1 && sim.Count(0, 0) == 4;
        sim.Build(new[] { 1, -1 }, new Entry[2]);
        bool stillHeld = sim.PeekDrained(0, 0) == 1;
        for (int i = 0; i < 200; i++, t++)
        {
            sim.Tick(t);
        }
        check(heldPacked && stillHeld && sim.PeekDrained(0, 0) == -1 && Total(sim) == 5, $"P3 held drain returns after end -> mid rebuild: drain={sim.PeekDrained(0, 0)} onBelt={Total(sim)} (expect -1/5)");
    });

    [Fact]
    public void DigestIsDeterministic() => Probe.Run(check =>
    {
        static int Run()
        {
            var sim = Comb(out int end, out int[] feeders);
            for (int t = 0; t < 2000; t++)
            {
                StepComb(sim, t, end, feeders);
            }
            return sim.Digest();
        }
        check(Run() == Run(), "P3 deterministic digest across two runs");
    });

    [Fact]
    public void BenchLoop() => Bench("loop 500 tiles, 2000 items", () =>
    {
        var sim = Loop(500);
        Fill(sim, 2);
        return (sim, (Action<int>)(t => sim.Tick(t)));
    });

    [Fact]
    public void BenchComb() => Bench("comb 400-tile main + 100 sideload feeders, feed+drain", () =>
    {
        var sim = Comb(out int end, out int[] feeders);
        return (sim, (Action<int>)(t => StepComb(sim, t, end, feeders)));
    });

    static void Bench(string name, Func<(LaneSim Sim, Action<int> Step)> setup) => Probe.Run(check =>
    {
        var (sim, step) = setup();
        for (int t = 0; t < 30000; t++) // warm-up past tiered JIT
        {
            step(t);
        }
        int items = Total(sim);
        var (us, alloc, windows) = Meter.Run(step, 30000, 30000);
        check(us < 200 && alloc == 0, $"P3 bench {name}: {sim.Tiles} tiles {items} items {us:F2} us/tick (budget 200), alloc {alloc} B (windows {windows})");
    });

    static LaneSim Chain(int n)
    {
        var next = new int[n];
        for (int i = 0; i < n; i++)
        {
            next[i] = i + 1 < n ? i + 1 : -1;
        }
        var sim = new LaneSim(n);
        sim.Build(next, new Entry[n]);
        return sim;
    }

    static LaneSim Loop(int n)
    {
        var next = new int[n];
        for (int i = 0; i < n; i++)
        {
            next[i] = (i + 1) % n;
        }
        var sim = new LaneSim(n);
        sim.Build(next, new Entry[n]);
        return sim;
    }

    static LaneSim Comb(out int end, out int[] feeders)
    {
        const int main = 400, side = 100;
        var next = new int[main + side];
        var entry = new Entry[main + side];
        feeders = new int[side];
        for (int i = 0; i < main; i++)
        {
            next[i] = i + 1 < main ? i + 1 : -1;
        }
        for (int s = 0; s < side; s++)
        {
            int f = main + s;
            feeders[s] = f;
            next[f] = s * 4 + 2;
            entry[f] = s % 2 == 0 ? Entry.SideLeft : Entry.SideRight;
        }
        end = main - 1;
        var sim = new LaneSim(main + side);
        sim.Build(next, entry);
        return sim;
    }

    static void StepComb(LaneSim sim, int t, int end, int[] feeders)
    {
        foreach (int f in feeders)
        {
            sim.TryPutBack(f, t & 1, f);
        }
        sim.TryPutBack(0, 0, 1);
        sim.TryPutBack(0, 1, 2);
        sim.OpenDrain(end, 0);
        sim.OpenDrain(end, 1);
        sim.Tick(t);
        sim.TryTakeDrained(end, 0, out _);
        sim.TryTakeDrained(end, 1, out _);
    }

    static void StepFeedDrain(LaneSim sim, int t, int first, int last, int[]? got)
    {
        sim.TryPutBack(first, 0, 1);
        sim.TryPutBack(first, 1, 2);
        sim.OpenDrain(last, 0);
        sim.OpenDrain(last, 1);
        sim.Tick(t);
        for (int lane = 0; lane < 2; lane++)
        {
            if (sim.TryTakeDrained(last, lane, out _) && got != null)
            {
                got[lane]++;
            }
        }
    }

    static void Fill(LaneSim sim, int perLane)
    {
        for (int t = 0; t < sim.Tiles; t++)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                for (int i = 0; i < perLane; i++)
                {
                    if (!sim.TryInsert(t, lane, i * (LaneSim.TileLength / perLane), t * 2 + lane))
                    {
                        throw new InvalidOperationException("fill");
                    }
                }
            }
        }
    }

    static int Total(LaneSim sim)
    {
        int n = 0;
        for (int t = 0; t < sim.Tiles; t++)
        {
            n += sim.Count(t, 0) + sim.Count(t, 1);
        }
        return n;
    }

    static bool Invariants(LaneSim sim, out string error)
    {
        for (int t = 0; t < sim.Tiles; t++)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                int n = sim.Count(t, lane);
                if (n > LaneSim.K)
                {
                    error = $"tile {t} lane {lane} count {n}";
                    return false;
                }
                for (int i = 0; i < n; i++)
                {
                    int p = sim.Pos(t, lane, i);
                    if (p < 0 || p > LaneSim.EndPos || (i > 0 && sim.Pos(t, lane, i - 1) - p < LaneSim.MinGap))
                    {
                        error = $"tile {t} lane {lane} slot {i} pos {p}";
                        return false;
                    }
                }
            }
        }
        error = "";
        return true;
    }
}

/// <summary>
/// Zero-alloc / cost meter for the benches: 3 windows of <c>ticks</c> steps; alloc = min over windows. A per-tick
/// allocation shows in every window; the sporadic 96 B seen in at most one window is the runtime's own tiering
/// transition on this thread (timing-dependent tick, gone with DOTNET_TieredCompilation=0, DOTNET_TieredPGO=0 or
/// DOTNET_TC_OnStackReplacement=0 over 30 runs each; 4/30 runs before), not sim code.
/// </summary>
static class Meter
{
    public static (double Us, long Alloc, string Windows) Run(Action<int> step, int start, int ticks)
    {
        double bestUs = double.MaxValue;
        long minAlloc = long.MaxValue;
        var all = new List<string>();
        for (int w = 0; w < 3; w++)
        {
            int from = start + w * ticks;
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            long ts0 = Stopwatch.GetTimestamp();
            for (int t = from; t < from + ticks; t++)
            {
                step(t);
            }
            long ts1 = Stopwatch.GetTimestamp();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            double us = (ts1 - ts0) * 1e6 / Stopwatch.Frequency / ticks;
            bestUs = Math.Min(bestUs, us);
            minAlloc = Math.Min(minAlloc, alloc);
            all.Add($"{us:F2}us/{alloc}B");
        }
        return (bestUs, minAlloc, string.Join(" ", all));
    }
}
