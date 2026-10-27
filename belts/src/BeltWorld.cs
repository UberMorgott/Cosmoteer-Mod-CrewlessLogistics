using System;
using System.Collections.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>A non-belt part as a connector endpoint (§7). Type = <see cref="BeltTypes"/> index.</summary>
public interface IRoom
{
    /// <summary>First type this room can give under filter (-1 any) that acceptor (if any) can take; -1 if none.</summary>
    int FindSupply(int filter, IRoom? acceptor);

    bool Take(int type);

    bool CanAccept(int type);

    bool Put(int type);
}

public enum Shape : byte
{
    Straight,
    /// <summary>Single side feeder from the belt's left, no rear feeder (§4).</summary>
    CurveLeft,
    CurveRight,
}

/// <summary>
/// Downstream input of a node output (§7a): a lane tile of a belt/splitter (landing by <see cref="Entry"/>), a connector
/// (gate, forwards to its own <see cref="Node.Front"/>), or a room (connector fronts only). Derived per topology epoch.
/// </summary>
internal sealed class Sink
{
    /// <summary>Target belt, splitter or connector; null = room.</summary>
    public Node? Node;
    public int Tile = -1;
    public Entry Entry;
    /// <summary>Connector put (§7): at the back preferred lane then the other; at a side the far lane.</summary>
    public bool Inserter;
    public IRoom? Room;

    public Node? Conn => Node is { Kind: NodeKind.Connector } c ? c : null;
}

/// <summary>One belt part of a ship for one topology epoch (rebuilt on part changes, never saved).</summary>
public sealed class Node
{
    public readonly NodeState State;
    /// <summary>Ship cell of the part (splitter: half 0), front direction, splitter offset half 0 -> half 1.</summary>
    public readonly int X, Y, Dx, Dy, Sx, Sy;
    /// <summary>Connector filter, refreshed from the UIToggle every tick (<see cref="BeltTypes.FilterFromMode"/>).</summary>
    public int Filter = BeltTypes.FilterNone;
    public Shape Shape;
    /// <summary>Belt crossing (heat pipe / cable under it): straight only, a lone side feeder side-loads instead of curving.</summary>
    public readonly bool NoCurve;
    internal int Tile0 = -1;
    /// <summary>Belt: non-tile output (connector) of its end. Splitter: output of half i. Connector: [0] = itself as a sink (chain start).</summary>
    internal readonly Sink?[] Out = new Sink?[2];
    /// <summary>Connector: front output, back room (only where no belt part is), first input kind (diagnostics).</summary>
    internal Sink? Front;
    internal IRoom? BackRoom;
    internal string? Input;
    /// <summary>Connector: may pass one item this tick (cooldown was 0 at tick start).</summary>
    internal bool Ready;

    public Node(NodeState state, int x, int y, int dx, int dy, int sx = 0, int sy = 0, bool noCurve = false)
    {
        NoCurve = noCurve;
        State = state;
        X = x;
        Y = y;
        Dx = dx;
        Dy = dy;
        Sx = sx;
        Sy = sy;
    }

    public NodeKind Kind => State.Kind;

    public string Mode => Kind != NodeKind.Connector ? "-"
        : (Input ?? (BackRoom != null ? "room" : "none")) + ">" + (Front == null ? "none" : Front.Node?.Kind.ToString().ToLowerInvariant() ?? "room");
}

/// <summary>
/// Belt network of one ship: topology (§4 auto-curve, §5 sideload, §6 splitter, §7 connector) plus the per-tick step.
/// Pure: the game supplies cell lookups and <see cref="IRoom"/> endpoints. Deterministic: node order = ship part order.
/// </summary>
public sealed class BeltWorld
{
    /// <summary>Connector rate (§7, Q3): one item per this many ticks (15/s, a full two-lane belt is 16/s).</summary>
    public const int ConnectorPeriod = 2;

    private readonly List<Node> _nodes;
    private readonly LaneSim _sim;
    private readonly int[] _tileNode;

    public BeltWorld(List<Node> nodes, Func<int, int, int> nodeAt, Func<int, int, IRoom?> roomAt)
    {
        _nodes = nodes;
        var tiles = new List<LaneTile>();
        var tileNode = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            Node n = nodes[i];
            n.Tile0 = n.State.Tiles.Length > 0 ? tiles.Count : -1;
            n.Out[0] = n.Out[1] = n.Front = null;
            n.BackRoom = null;
            n.Input = null;
            n.Shape = Shape.Straight;
            foreach (LaneTile t in n.State.Tiles)
            {
                tiles.Add(t);
                tileNode.Add(i);
            }
        }
        _tileNode = tileNode.ToArray();
        _sim = new LaneSim(tiles.ToArray());
        int count = tiles.Count;
        var next = new int[count];
        var entry = new Entry[count];
        Array.Fill(next, -1);
        var feeders = new List<(int Tile, Sink S)>[nodes.Count];

        // One rule for every output (§7a): what is in front of the output cell, and from which side it is entered.
        Sink? SinkAt(int cx, int cy, int dx, int dy, bool inserter)
        {
            int fx = cx + dx, fy = cy + dy, j = nodeAt(fx, fy);
            if (j < 0)
            {
                return inserter && roomAt(fx, fy) is { } r ? new Sink { Room = r } : null;
            }
            Node m = nodes[j];
            if (m.Dx == -dx && m.Dy == -dy)
            {
                return null; // m's output faces this output: defined no-op
            }
            Entry rel = m.Dx == dx && m.Dy == dy ? Entry.Rear : dx == -m.Dy && dy == m.Dx ? Entry.SideLeft : Entry.SideRight;
            return new Sink
            {
                Node = m,
                Tile = m.Kind == NodeKind.Connector ? -1 : m.Tile0 + (m.Kind == NodeKind.Splitter ? HalfAt(m, fx, fy) : 0),
                Entry = rel,
                Inserter = inserter,
            };
        }

        // Lane tile outputs. Belt and splitter targets of belts become LaneSim moves; everything else is a handoff.
        for (int t = 0; t < count; t++)
        {
            Node n = nodes[_tileNode[t]];
            var (cx, cy) = TileCell(t);
            if (SinkAt(cx, cy, n.Dx, n.Dy, false) is not { Node: { } m } s)
            {
                continue;
            }
            if (m.Kind == NodeKind.Belt)
            {
                (feeders[_tileNode[m.Tile0]] ??= new()).Add((t, s));
            }
            else
            {
                SetOut(t, s);
            }
        }
        for (int j = 0; j < nodes.Count; j++)
        {
            if (feeders[j] is not { } f)
            {
                continue;
            }
            int rear = 0, sides = 0;
            foreach (var x in f)
            {
                if (x.S.Entry == Entry.Rear)
                {
                    rear++;
                }
                else
                {
                    sides++;
                }
            }
            bool curve = !nodes[j].NoCurve && rear == 0 && sides == 1;
            nodes[j].Shape = curve ? (f[0].S.Entry == Entry.SideLeft ? Shape.CurveLeft : Shape.CurveRight) : Shape.Straight;
            foreach (var x in f)
            {
                if (curve)
                {
                    x.S.Entry = Entry.Rear;
                }
                SetOut(x.Tile, x.S);
            }
        }
        void SetOut(int t, Sink s)
        {
            Node n = nodes[_tileNode[t]];
            if (s.Conn is { } c)
            {
                c.Input ??= n.Kind == NodeKind.Belt ? "belt" : "splitter";
            }
            if (n.Kind == NodeKind.Belt && s.Tile >= 0)
            {
                next[t] = s.Tile;
                entry[t] = s.Entry;
            }
            else
            {
                n.Out[t - n.Tile0] = s;
            }
        }

        foreach (Node c in nodes)
        {
            if (c.Kind != NodeKind.Connector)
            {
                continue;
            }
            if (nodeAt(c.X - c.Dx, c.Y - c.Dy) < 0)
            {
                c.BackRoom = roomAt(c.X - c.Dx, c.Y - c.Dy);
            }
            c.Out[0] = new Sink { Node = c };
            c.Front = SinkAt(c.X, c.Y, c.Dx, c.Dy, true);
            if (c.Front?.Conn is { } m)
            {
                m.Input ??= "connector";
            }
        }
        // A closed connector loop has no end: cut it at its first connector (node order), so every chain terminates.
        foreach (Node c in nodes)
        {
            int hops = 0;
            for (Node? x = c.Front?.Conn; x != null && hops <= nodes.Count; x = x.Front?.Conn, hops++)
            {
                if (ReferenceEquals(x, c))
                {
                    c.Front = null;
                    break;
                }
            }
        }
        _sim.Build(next, entry);
    }

    public LaneSim Sim => _sim;

    public IReadOnlyList<Node> Nodes => _nodes;

    private static int HalfAt(Node m, int x, int y) => x == m.X && y == m.Y ? 0 : 1;

    private (int X, int Y) TileCell(int t)
    {
        Node n = _nodes[_tileNode[t]];
        int h = t - n.Tile0;
        return (n.X + h * n.Sx, n.Y + h * n.Sy);
    }

    public void Tick(int tick)
    {
        foreach (Node n in _nodes)
        {
            if (n.Kind == NodeKind.Connector)
            {
                // Before any handoff, so the rate does not depend on node order.
                n.Ready = n.State.Cooldown == 0;
                n.State.Cooldown = Math.Max(0, n.State.Cooldown - 1);
            }
            else if (n.Kind == NodeKind.Splitter)
            {
                for (int i = 0; i < 4; i++)
                {
                    _sim.OpenDrain(n.Tile0 + (i >> 1), i & 1);
                }
            }
            else if (n.Kind == NodeKind.Belt && n.Out[0] is { } s && Resolve(s, false, out _, out int filter))
            {
                _sim.OpenDrain(n.Tile0, 0, filter);
                _sim.OpenDrain(n.Tile0, 1, filter);
            }
        }
        _sim.Tick(tick);
        foreach (Node n in _nodes)
        {
            if (n.Kind == NodeKind.Splitter)
            {
                Route(n);
            }
            else if (n.Kind == NodeKind.Connector)
            {
                Extract(n);
            }
            else if (n.Out[0] is { } s)
            {
                // Belt end into a connector: drained head items only, alternating lanes.
                for (int k = 0; k < 2; k++)
                {
                    int lane = n.State.Alternate ^ k;
                    int type = _sim.PeekDrained(n.Tile0, lane);
                    if (type >= 0 && Offer(s, lane, type))
                    {
                        _sim.TryTakeDrained(n.Tile0, lane, out _);
                        n.State.Alternate = lane ^ 1;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>§6: per lane, items of both inputs alternate between both outputs; a blocked output yields to the other.</summary>
    private void Route(Node n)
    {
        for (int lane = 0; lane < 2; lane++)
        {
            for (int h = 0; h < 2; h++)
            {
                int type = _sim.PeekDrained(n.Tile0 + h, lane);
                if (type < 0)
                {
                    continue;
                }
                int o = n.State.RoundRobin[lane];
                int used = Offer(n.Out[o], lane, type) ? o : Offer(n.Out[o ^ 1], lane, type) ? o ^ 1 : -1;
                if (used >= 0)
                {
                    _sim.TryTakeDrained(n.Tile0 + h, lane, out _);
                    n.State.RoundRobin[lane] = used ^ 1;
                }
            }
        }
    }

    /// <summary>
    /// Walks a sink through any connector chain (§7a) to its end (lane tile or room). Filter = intersection of the
    /// chain's filters (-1 any). False when there is no end, a connector is off (filter none), the filters exclude each
    /// other, or (ready) a connector already passed an item within its period.
    /// </summary>
    private static bool Resolve(Sink? s, bool ready, out Sink end, out int filter)
    {
        filter = BeltTypes.FilterAny;
        for (; s?.Conn is { } c; s = c.Front)
        {
            if (c.Filter == BeltTypes.FilterNone || (ready && !c.Ready))
            {
                end = null!;
                return false;
            }
            filter = filter == BeltTypes.FilterAny ? c.Filter : c.Filter == BeltTypes.FilterAny || c.Filter == filter ? filter : BeltTypes.FilterNone;
            if (filter == BeltTypes.FilterNone)
            {
                end = null!;
                return false;
            }
        }
        end = s!;
        return s != null;
    }

    /// <summary>The single handoff (§7a): one item from any output into its sink; true = accepted (never lost).</summary>
    private bool Offer(Sink? s, int lane, int type)
    {
        if (!Resolve(s, true, out Sink end, out int filter) || (filter >= 0 && filter != type))
        {
            return false;
        }
        bool ok = end.Room != null ? end.Room.Put(type)
            : end.Inserter ? Slot(end, lane, out int l, out int p) && _sim.TryInsert(end.Tile, l, p, type)
            : end.Entry == Entry.Rear ? _sim.TryPutBack(end.Tile, lane, type)
            : _sim.TryInsert(end.Tile, end.Entry == Entry.SideLeft ? 0 : 1, LaneSim.SideEntryPos, type);
        if (ok)
        {
            Commit(s);
        }
        return ok;
    }

    /// <summary>Every connector the item passed is busy for <see cref="ConnectorPeriod"/> ticks.</summary>
    private static void Commit(Sink? s)
    {
        for (; s?.Conn is { } c; s = c.Front)
        {
            c.Ready = false;
            c.State.Cooldown = ConnectorPeriod - 1;
        }
    }

    /// <summary>Connector landing on a lane tile: side -> far lane at the entry point; back -> preferred lane, then the other.</summary>
    private bool Slot(Sink end, int preferLane, out int lane, out int pos)
    {
        // From the belt's left side the near lane is L, so the far lane is R (owner inserter rule, §7).
        int far = end.Entry == Entry.Rear ? -1 : end.Entry == Entry.SideLeft ? 1 : 0;
        for (int k = 0; k < 2; k++)
        {
            lane = far >= 0 ? far : preferLane ^ k;
            pos = far >= 0 ? (_sim.Fits(end.Tile, lane, LaneSim.SideEntryPos) ? LaneSim.SideEntryPos : -1) : _sim.PutBackPos(end.Tile, lane);
            if (pos >= 0)
            {
                return true;
            }
        }
        lane = pos = -1;
        return false;
    }

    /// <summary>Connector with a room behind it: pulls one unit per period into its front chain.</summary>
    private void Extract(Node c)
    {
        if (c.BackRoom is not { } back)
        {
            return;
        }
        Sink self = c.Out[0]!;
        if (!Resolve(self, true, out Sink end, out int filter))
        {
            return;
        }
        if (end.Room is { } front)
        {
            if (ReferenceEquals(front, back))
            {
                return; // chain loops back into its own room: no-op
            }
            int type = back.FindSupply(filter, front);
            if (type >= 0 && back.Take(type))
            {
                if (front.Put(type))
                {
                    Commit(self);
                }
                else
                {
                    back.Put(type); // CanAccept said yes; never lose the unit
                }
            }
        }
        else if (Slot(end, c.State.Alternate, out int lane, out int pos))
        {
            // Reserve the belt slot first, then take exactly one unit.
            int type = back.FindSupply(filter, null);
            if (type >= 0 && back.Take(type))
            {
                _sim.TryInsert(end.Tile, lane, pos, type);
                c.State.Alternate = lane ^ 1;
                Commit(self);
            }
        }
    }

    /// <summary>
    /// Rendering only (floats never feed the sim): ship-cell point of an item. Lane L is left of travel; curves follow a
    /// quarter arc around the inner corner (§4: inner lane stays inner).
    /// </summary>
    /// <summary>
    /// Belt surface width in tiles: mod/assets/belt.png (Codex art) dark plate between the rails spans pixels 15..48 of
    /// 64 (34 px), centered. Connector (13..50, 38 px) and splitter (17..46, 30 px) art differ; belt.png is the reference.
    /// </summary>
    public const float SurfaceWidth = 34f / 64f;

    /// <summary>Lane center distance from the belt centerline: middle of each surface half.</summary>
    public const float LaneOffset = SurfaceWidth / 4;

    public static (float X, float Y) ItemPoint(Node n, int half, int lane, int pos)
    {
        float cx = n.X + half * n.Sx + 0.5f, cy = n.Y + half * n.Sy + 0.5f;
        float dx = n.Dx, dy = n.Dy, lx = n.Dy, ly = -n.Dx; // left of travel, screen y down
        float u = pos / (float)LaneSim.TileLength;
        const float laneOffset = LaneOffset;
        if (n.Shape == Shape.Straight)
        {
            float o = lane == 0 ? laneOffset : -laneOffset;
            return (cx + dx * (u - 0.5f) + lx * o, cy + dy * (u - 0.5f) + ly * o);
        }
        float side = n.Shape == Shape.CurveLeft ? 1 : -1; // feeder enters from +left (CurveLeft) or -left
        float px = cx + lx * 0.5f * side + dx * 0.5f, py = cy + ly * 0.5f * side + dy * 0.5f;
        bool inner = (lane == 0) == (n.Shape == Shape.CurveLeft);
        float r = 0.5f + (inner ? -laneOffset : laneOffset);
        float a = u * MathF.PI / 2;
        float vx = -dx * MathF.Cos(a) - lx * side * MathF.Sin(a), vy = -dy * MathF.Cos(a) - ly * side * MathF.Sin(a);
        return (px + vx * r, py + vy * r);
    }
}
