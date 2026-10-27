using System.Diagnostics;
using System.Reflection;
using CrewlessLogistics.Belts;

// Headless probes P1 (patch targets on the real 0.30.5 assemblies + loader Harmony) and P3 (lane sim correctness + cost).
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok)
    {
        failures++;
    }
}

try
{
    P1();
}
catch (Exception ex)
{
    Check(false, "P1 threw: " + ex);
}
P3();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

void P1()
{
    // The mod DLL is compiled against net10 + publicized game assemblies; reach it by reflection on the game runtime.
    var mod = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "CrewlessLogistics.Belts.dll"));
    var selfTest = mod.GetType("CrewlessLogistics.Belts.Patcher", true)!.GetMethod("SelfTest", BindingFlags.Public | BindingFlags.Static)!;
    foreach (var (ok, what) in (List<(bool, string)>)selfTest.Invoke(null, null)!)
    {
        Check(ok, what);
    }
}

void P3()
{
    // Packed straight line: injector at the start, loader at the end. Expected 512/30/64 items/tick = 8 items/s per lane.
    {
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
        Check(ok, "P3 straight 10 tiles: gap/order/capacity invariants every tick");
        Check(got[0] is >= 795 and <= 805 && got[1] is >= 795 and <= 805, $"P3 packed line throughput {got[0]}/{got[1]} items per 100 s per lane (expect 800 = 8/s, full speed)");
    }
    // Closed loop at 50% density keeps flowing and conserves items.
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
        Check(ok && Total(sim) == before && sim.Digest() != d0, $"P3 loop 20 tiles 50%: {Total(sim)}/{before} items conserved, invariants, moving");
    }
    // Known limit: a 100% packed closed loop freezes (cycle root sees its successor's old tail). Documented in spec §3.
    {
        var sim = Loop(8);
        Fill(sim, 4);
        int h0 = sim.Pos(0, 0, 0);
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(t);
        }
        Console.WriteLine($"INFO P3 100% packed loop moves: {sim.Pos(0, 0, 0) != h0} (expected False, accepted limit)");
    }
    // Sideload: feeder (tile 6) into the left side of straight tile 3 of a 6-tile line; all feeder items land on lane L.
    {
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
        Check(ok && got[1] == 0 && got[0] + onBelt == fed && got[0] > 700, $"P3 sideload left -> lane L only: L={got[0]} R={got[1]} fed={fed} onBelt={onBelt}");
    }
    // Belt end: 4 items pack at 255,191,127,63 and stop.
    {
        var sim = Chain(1);
        for (int t = 0; t < 200; t++)
        {
            sim.TryInsert(0, 0, 0, 1);
            sim.Tick(t);
        }
        Check(sim.Count(0, 0) == 4 && sim.Pos(0, 0, 0) == 255 && sim.Pos(0, 0, 3) == 63, $"P3 belt end stop: count={sim.Count(0, 0)} head={sim.Pos(0, 0, 0)} tail={sim.Pos(0, 0, 3)}");
    }
    // Determinism: identical runs give identical digests.
    {
        int Run()
        {
            var sim = Comb(out int end, out int[] feeders);
            for (int t = 0; t < 2000; t++)
            {
                StepComb(sim, t, end, feeders);
            }
            return sim.Digest();
        }
        Check(Run() == Run(), "P3 deterministic digest across two runs");
    }
    Bench("loop 500 tiles, 2000 items", () =>
    {
        var sim = Loop(500);
        Fill(sim, 2);
        return (sim, (Action<int>)(t => sim.Tick(t)));
    });
    Bench("comb 400-tile main + 100 sideload feeders, feed+drain", () =>
    {
        var sim = Comb(out int end, out int[] feeders);
        return (sim, (Action<int>)(t => StepComb(sim, t, end, feeders)));
    });
}

void Bench(string name, Func<(LaneSim Sim, Action<int> Step)> setup)
{
    var (sim, step) = setup();
    for (int t = 0; t < 30000; t++) // warm-up past tiered JIT
    {
        step(t);
    }
    int items = Total(sim);
    const int ticks = 30000;
    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
    long ts0 = Stopwatch.GetTimestamp();
    for (int t = 30000; t < 30000 + ticks; t++)
    {
        step(t);
    }
    long ts1 = Stopwatch.GetTimestamp();
    long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    double us = (ts1 - ts0) * 1e6 / Stopwatch.Frequency / ticks;
    Check(us < 200 && alloc == 0, $"P3 bench {name}: {sim.Tiles} tiles {items} items {us:F2} us/tick (budget 200), alloc {alloc} B");
}

LaneSim Chain(int n)
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

LaneSim Loop(int n)
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

LaneSim Comb(out int end, out int[] feeders)
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

void StepComb(LaneSim sim, int t, int end, int[] feeders)
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

void StepFeedDrain(LaneSim sim, int t, int first, int last, int[]? got)
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

void Fill(LaneSim sim, int perLane)
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

int Total(LaneSim sim)
{
    int n = 0;
    for (int t = 0; t < sim.Tiles; t++)
    {
        n += sim.Count(t, 0) + sim.Count(t, 1);
    }
    return n;
}

bool Invariants(LaneSim sim, out string error)
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
