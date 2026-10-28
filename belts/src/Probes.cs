using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Simulation;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Live-game probe logging (§14). Reads sim state only, the same on every peer; wall-clock values are logged, never fed
/// back into the sim.
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

    /// <summary>§27 render accumulators of the current log window (client only, BeltManager.Update).</summary>
    private static long _renderCost;
    private static int _renderCalls, _renderWork, _shownShips, _coarseShips, _beltShipsThisTick, _tilesThisTick;
    private static long _items, _surfaces, _lamps;

    /// <summary>§27 one BeltManager.Update: cost, whether it rendered (new tick / view change), what the ship shows now.</summary>
    public static void OnRender(long cost, bool worked, bool shown, bool coarse, int items, int surfaces, int lamps)
    {
        _renderCost += cost;
        _renderCalls++;
        if (worked)
        {
            _renderWork++;
            _shownShips += shown ? 1 : 0;
            _coarseShips += coarse ? 1 : 0;
            _items += items;
            _surfaces += surfaces;
            _lamps += lamps;
        }
    }

    public static void OnTick(SimRoot sim, int tick, bool duplicate, int tracked, int tiles, long cost)
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
                // §27 perf (rate-limited with this line): sim = FixedUpdate incl. rebuilds, render = per-frame Update; quads = per rendered ship tick.
                double perTick = 1e6 / Stopwatch.Frequency / _ticksWindow, frames = _renderCalls / (double)Math.Max(1, _beltShipsThisTick);
                Log.Info($"perf beltShips={_beltShipsThisTick} tiles={_tilesThisTick} sim={_costWindow * perTick:F0} us/tick render={_renderCost * perTick:F0} us/tick "
                    + $"({_renderCost * 1e6 / Stopwatch.Frequency / Math.Max(1, frames):F0} us/frame, {frames / secs:F0} fps) shownShips/tick={_shownShips / (double)_ticksWindow:F1} "
                    + $"coarse={_coarseShips / (double)_ticksWindow:F1} quads/tick items={_items / _ticksWindow} surfaces={_surfaces / _ticksWindow} lamps={_lamps / _ticksWindow}");
                _renderCost = _items = _surfaces = _lamps = 0;
                _renderCalls = _renderWork = _shownShips = _coarseShips = 0;
                _windowStart = Stopwatch.GetTimestamp();
                _ticksWindow = 0;
                _costWindow = 0;
            }
            _tick = tick;
            _callsThisTick = 0;
            _trackedThisTick = 0;
            _beltShipsThisTick = _tilesThisTick = 0;
        }
        _callsThisTick++;
        _trackedThisTick += tracked;
        _beltShipsThisTick += tracked > 0 ? 1 : 0;
        _tilesThisTick += tiles;
        _costWindow += cost;
        if (duplicate)
        {
            _duplicates++;
            Log.Info($"P1 FAIL duplicate FixedUpdate in tick {tick}");
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
                sb.Append('/').Append(n.Mode).Append(" f=").Append(n.Filter).Append(" sp=").Append(n.State.SourcePriority);
            }
            else if (n.Kind == NodeKind.Splitter)
            {
                sb.Append($"/in={n.State.InPriority} out={n.State.OutPriority} f={n.State.SplitFilter} left={BeltWorld.LeftHalf(n)}");
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
}
