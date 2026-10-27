using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Simulation;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Live-game probe logging (§14). Reads sim state only, except P5 which is opt-in via p5_parts.txt in the mod folder.
/// Wall-clock values are logged, never fed back into the sim.
/// </summary>
internal static class Probes
{
    private static SimRoot? _sim;
    private static int _tick = int.MinValue;
    private static int _callsThisTick;
    private static int _duplicates;
    private static int _trackedThisTick;
    private static long _costWindow;
    private static int _ticksWindow;
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static readonly HashSet<string> Dumped = new();
    private static readonly HashSet<string>? P5Parts = LoadP5Parts();

    public static void OnTick(SimRoot sim, int tick, bool duplicate, int tracked, long cost)
    {
        if (!ReferenceEquals(sim, _sim))
        {
            _sim = sim;
            _tick = tick;
            _callsThisTick = 0;
            _windowStart = Stopwatch.GetTimestamp();
            _ticksWindow = 0;
            _costWindow = 0;
            Log.Info($"P1 sim attached tick={tick}");
        }
        if (tick != _tick)
        {
            int ships = sim.Ships.Count;
            _ticksWindow += tick - _tick;
            if (_ticksWindow >= 300)
            {
                double secs = Stopwatch.GetElapsedTime(_windowStart).TotalSeconds;
                Log.Info($"P1 tick={_tick} calls/tick={_callsThisTick} simShips={ships} duplicates={_duplicates} bucket={BeltManager.Bucket} "
                    + $"| P3 rate={_ticksWindow / secs:F1} ticks/s cost={_costWindow * 1e6 / Stopwatch.Frequency / _ticksWindow:F1} us/tick "
                    + $"trackedParts={_trackedThisTick}");
                _windowStart = Stopwatch.GetTimestamp();
                _ticksWindow = 0;
                _costWindow = 0;
            }
            _tick = tick;
            _callsThisTick = 0;
            _trackedThisTick = 0;
        }
        _callsThisTick++;
        _trackedThisTick += tracked;
        _costWindow += cost;
        if (duplicate)
        {
            _duplicates++;
            Log.Info($"P1 FAIL duplicate FixedUpdate in tick {tick}");
        }
    }

    private static bool IsLocal(Ship ship) => ship.Game != null && ship.Metadata.PlayerIndex == ship.Game.LocalPlayerIndex;

    /// <summary>P2: one line per distinct (part ID, endpoint signature) on the local player's ships.</summary>
    public static void DumpEndpoints(Ship ship)
    {
        if (!IsLocal(ship))
        {
            return;
        }
        try
        {
            var byPart = new Dictionary<Part, (List<string> Src, List<string> Snk)>();
            foreach (IResourceSource s in ship.Resources.Sources.ToList())
            {
                if (s.Part is { } p)
                {
                    Entry(byPart, p).Src.Add($"{s.GetType().Name}:{s.ResourceType} supplies={s.SuppliesResources} prio={s.SortPriority}");
                }
            }
            foreach (IResourceSink k in ship.Resources.Sinks.ToList())
            {
                if (k.Part is { } p)
                {
                    var type = k.GetResourceType(MPValueType.Confirmed);
                    Entry(byPart, p).Snk.Add($"{k.GetType().Name}:{type} canReceive={k.CanReceive} prio={k.GetSortPriority(false, MPValueType.Confirmed)} multi={k.AllowMultipleReceivedTypes}");
                }
            }
            foreach (var (part, e) in byPart.OrderBy(kv => kv.Key.Rules.ID.ToString()))
            {
                string sig = $"src[{Group(e.Src)}] sink[{Group(e.Snk)}]";
                if (Dumped.Add(part.Rules.ID + sig))
                {
                    Log.Info($"P2 part={part.Rules.ID} {sig}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("p2", ex);
        }
    }

    /// <summary>
    /// One line per changed belt network of any ship in this session (Creative ships are not the local player's):
    /// node kinds, curves, connector modes. Unchanged rebuilds are skipped, at most 10 lines per second.
    /// </summary>
    public static void DumpTopology(Ship ship, List<Node> nodes, List<Part> parts)
    {
        if (nodes.Count == 0)
        {
            return;
        }
        var sb = new System.Text.StringBuilder($"topology nodes={nodes.Count}:");
        for (int i = 0; i < nodes.Count && i < 64; i++)
        {
            Node n = nodes[i];
            sb.Append($" {n.Kind}@{n.X},{n.Y}>{n.Dx},{n.Dy}");
            if (n.Kind == NodeKind.Belt && n.Shape != Shape.Straight)
            {
                sb.Append('/').Append(n.Shape);
            }
            if (n.Kind == NodeKind.Connector)
            {
                sb.Append('/').Append(n.Mode).Append(" f=").Append(n.Filter);
            }
        }
        string line = sb.ToString();
        if (LastTopology.TryGetValue(ship, out string? last) && last == line)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (now - _topologyWindow >= Stopwatch.Frequency)
        {
            _topologyWindow = now;
            _topologyLines = 0;
        }
        if (_topologyLines++ < 10)
        {
            LastTopology.AddOrUpdate(ship, line); // only logged lines count, so a throttled one logs on the next rebuild
            Log.Info(line);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ship, string> LastTopology = new();
    private static long _topologyWindow;
    private static int _topologyLines;

    private static (List<string> Src, List<string> Snk) Entry(Dictionary<Part, (List<string>, List<string>)> d, Part p)
    {
        if (!d.TryGetValue(p, out var e))
        {
            d[p] = e = (new List<string>(), new List<string>());
        }
        return e;
    }

    // Grid tiles produce one identical endpoint per tile: show "signature xN".
    private static string Group(List<string> items) =>
        string.Join(", ", items.GroupBy(x => x).OrderBy(g => g.Key).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));

    private static HashSet<string>? LoadP5Parts()
    {
        string path = Path.Combine(Log.ModFolder, "p5_parts.txt");
        if (!File.Exists(path))
        {
            return null;
        }
        var set = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        Log.Info("P5 enabled for: " + string.Join(", ", set));
        return set;
    }

    /// <summary>
    /// P5 (single-player test only): every 60 ticks push 1 unit via ReceiveResources(isCrewDelivery:false) into the first
    /// sink of each listed part, logging the vanilla job-owned anticipated delivery before/after.
    /// </summary>
    public static void PushP5(Ship ship, int tick)
    {
        if (P5Parts == null || tick % 60 != 0 || !IsLocal(ship))
        {
            return;
        }
        try
        {
            var seen = new HashSet<Part>();
            foreach (IResourceSink k in ship.Resources.Sinks.ToList())
            {
                if (k.Part is not { } p || !P5Parts.Contains(p.Rules.ID.ToString()) || !seen.Add(p))
                {
                    continue;
                }
                var type = k.GetResourceType(MPValueType.Confirmed);
                var info = ResourceManager.GetSinkInfo(k);
                var before = info.AnticipatedDelivery;
                int cap = k.GetRemainingCapacity(type, false);
                int got = k.CanReceive && cap > 0 ? k.ReceiveResources(type, 1, isCrewDelivery: false, sticky: false) : 0;
                var after = info.AnticipatedDelivery;
                Log.Info($"P5 tick={tick} part={p.Rules.ID} sink={k.GetType().Name} type={type} cap={cap} got={got} "
                    + $"anticipated={before.Confirmed}->{after.Confirmed} jobs={info.AutoJobs.Count()}"
                    + (after.Confirmed < 0 ? " FAIL negative" : ""));
            }
        }
        catch (Exception ex)
        {
            Log.Error("p5", ex);
        }
    }
}
