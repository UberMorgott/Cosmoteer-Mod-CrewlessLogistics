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

/// <summary>One belt part of a ship for one topology epoch (rebuilt on part changes, never saved).</summary>
public sealed class Node
{
    public readonly NodeState State;
    /// <summary>Ship cell of the part (splitter: half 0), front direction, splitter offset half 0 -> half 1.</summary>
    public readonly int X, Y, Dx, Dy, Sx, Sy;
    /// <summary>Connector filter, refreshed from the UIToggle every tick (<see cref="BeltTypes.FilterFromMode"/>).</summary>
    public int Filter = BeltTypes.FilterNone;
    public Shape Shape;
    internal int Tile0 = -1;
    internal readonly int[] OutTile = { -1, -1 };
    internal readonly Entry[] OutEntry = new Entry[2];
    internal int BackTile = -1, FrontTile = -1, FarLane = -1;
    internal IRoom? BackRoom, FrontRoom;

    public Node(NodeState state, int x, int y, int dx, int dy, int sx = 0, int sy = 0)
    {
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
        : (BackTile >= 0 ? "belt" : BackRoom != null ? "room" : "none") + ">" + (FrontTile >= 0 ? "belt" : FrontRoom != null ? "room" : "none");
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
            n.OutTile[0] = n.OutTile[1] = n.BackTile = n.FrontTile = n.FarLane = -1;
            n.BackRoom = n.FrontRoom = null;
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
        var feeders = new List<(int Tile, Entry Rel)>[nodes.Count];

        // Feeder classification: every lane tile looks at its front cell.
        for (int t = 0; t < count; t++)
        {
            Node n = nodes[_tileNode[t]];
            var (cx, cy) = TileCell(t);
            int j = nodeAt(cx + n.Dx, cy + n.Dy);
            if (j < 0)
            {
                continue;
            }
            Node m = nodes[j];
            if (m.Kind == NodeKind.Belt && !(m.Dx == -n.Dx && m.Dy == -n.Dy))
            {
                Entry rel = m.Dx == n.Dx && m.Dy == n.Dy ? Entry.Rear : n.Dx == -m.Dy && n.Dy == m.Dx ? Entry.SideLeft : Entry.SideRight;
                (feeders[j] ??= new()).Add((t, rel));
            }
            else if (m.Kind == NodeKind.Splitter && m.Dx == n.Dx && m.Dy == n.Dy)
            {
                SetOut(t, m.Tile0 + HalfAt(m, cx + n.Dx, cy + n.Dy), Entry.Rear);
            }
            else if (m.Kind == NodeKind.Connector && n.Kind == NodeKind.Belt && m.Dx == n.Dx && m.Dy == n.Dy)
            {
                // Belt end pointing into the connector's back. shortcut: splitter output straight into a connector is
                // idle (splitter owns its drains); put a belt tile between them.
                m.BackTile = t;
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
                if (x.Rel == Entry.Rear)
                {
                    rear++;
                }
                else
                {
                    sides++;
                }
            }
            bool curve = rear == 0 && sides == 1;
            nodes[j].Shape = curve ? (f[0].Rel == Entry.SideLeft ? Shape.CurveLeft : Shape.CurveRight) : Shape.Straight;
            foreach (var x in f)
            {
                SetOut(x.Tile, nodes[j].Tile0, curve ? Entry.Rear : x.Rel);
            }
        }
        void SetOut(int t, int target, Entry e)
        {
            Node n = nodes[_tileNode[t]];
            if (n.Kind == NodeKind.Belt)
            {
                next[t] = target;
                entry[t] = e;
            }
            else
            {
                n.OutTile[t - n.Tile0] = target;
                n.OutEntry[t - n.Tile0] = e;
            }
        }

        // Connectors: front occupant decides put mode; rooms only where no belt part is.
        foreach (Node c in nodes)
        {
            if (c.Kind != NodeKind.Connector)
            {
                continue;
            }
            int bx = c.X - c.Dx, by = c.Y - c.Dy, fx = c.X + c.Dx, fy = c.Y + c.Dy;
            if (c.BackTile < 0 && nodeAt(bx, by) < 0)
            {
                c.BackRoom = roomAt(bx, by);
            }
            int j = nodeAt(fx, fy);
            if (j < 0)
            {
                c.FrontRoom = roomAt(fx, fy);
                if (c.FrontRoom != null && ReferenceEquals(c.FrontRoom, c.BackRoom))
                {
                    c.FrontRoom = null;
                }
                continue;
            }
            Node m = nodes[j];
            if (m.Kind == NodeKind.Belt && !(m.Dx == -c.Dx && m.Dy == -c.Dy))
            {
                c.FrontTile = m.Tile0;
                // From the belt's left side the near lane is L, so the far lane is R (owner inserter rule, §7).
                c.FarLane = m.Dx == c.Dx && m.Dy == c.Dy ? -1 : c.Dx == -m.Dy && c.Dy == m.Dx ? 1 : 0;
            }
            else if (m.Kind == NodeKind.Splitter && m.Dx == c.Dx && m.Dy == c.Dy)
            {
                c.FrontTile = m.Tile0 + HalfAt(m, fx, fy);
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
            if (n.Kind == NodeKind.Splitter)
            {
                for (int i = 0; i < 4; i++)
                {
                    _sim.OpenDrain(n.Tile0 + (i >> 1), i & 1);
                }
            }
            else if (n.Kind == NodeKind.Connector && n.BackTile >= 0 && n.Filter != BeltTypes.FilterNone && (n.FrontTile >= 0 || n.FrontRoom != null))
            {
                _sim.OpenDrain(n.BackTile, 0, n.Filter);
                _sim.OpenDrain(n.BackTile, 1, n.Filter);
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
                Transfer(n);
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
                int used = Put(n.OutTile[o], n.OutEntry[o], lane, type) ? o : Put(n.OutTile[o ^ 1], n.OutEntry[o ^ 1], lane, type) ? o ^ 1 : -1;
                if (used >= 0)
                {
                    _sim.TryTakeDrained(n.Tile0 + h, lane, out _);
                    n.State.RoundRobin[lane] = used ^ 1;
                }
            }
        }
    }

    private bool Put(int tile, Entry e, int lane, int type) =>
        tile >= 0 && (e == Entry.Rear ? _sim.TryPutBack(tile, lane, type) : _sim.TryInsert(tile, e == Entry.SideLeft ? 0 : 1, LaneSim.SideEntryPos, type));

    /// <summary>Front belt landing for a connector put: side -> far lane at the entry point; back -> preferred lane, then the other.</summary>
    private bool FrontSlot(Node c, int preferLane, out int lane, out int pos)
    {
        for (int k = 0; k < 2; k++)
        {
            lane = c.FarLane >= 0 ? c.FarLane : preferLane ^ k;
            pos = c.FarLane >= 0 ? (_sim.Fits(c.FrontTile, lane, LaneSim.SideEntryPos) ? LaneSim.SideEntryPos : -1) : _sim.PutBackPos(c.FrontTile, lane);
            if (pos >= 0)
            {
                return true;
            }
        }
        lane = pos = -1;
        return false;
    }

    private void Transfer(Node c)
    {
        if (c.Filter == BeltTypes.FilterNone)
        {
            return;
        }
        NodeState s = c.State;
        if (s.Cooldown > 0)
        {
            s.Cooldown--;
            return;
        }
        bool done = false;
        if (c.BackTile >= 0)
        {
            // Loader / pass-through: drained head items only, alternating lanes.
            for (int k = 0; k < 2 && !done; k++)
            {
                int lane = s.Alternate ^ k;
                int type = _sim.PeekDrained(c.BackTile, lane);
                if (type < 0)
                {
                    continue;
                }
                if (c.FrontTile >= 0 ? FrontSlot(c, lane, out int fl, out int fp) && _sim.TryInsert(c.FrontTile, fl, fp, type) : c.FrontRoom?.Put(type) == true)
                {
                    _sim.TryTakeDrained(c.BackTile, lane, out _);
                    s.Alternate = lane ^ 1;
                    done = true;
                }
            }
        }
        else if (c.BackRoom is { } back)
        {
            if (c.FrontTile >= 0)
            {
                // Extractor: reserve the belt slot first, then take exactly one unit.
                if (FrontSlot(c, s.Alternate, out int lane, out int pos))
                {
                    int type = back.FindSupply(c.Filter, null);
                    if (type >= 0 && back.Take(type))
                    {
                        _sim.TryInsert(c.FrontTile, lane, pos, type);
                        s.Alternate = lane ^ 1;
                        done = true;
                    }
                }
            }
            else if (c.FrontRoom is { } front)
            {
                int type = back.FindSupply(c.Filter, front);
                if (type >= 0 && back.Take(type))
                {
                    if (!front.Put(type))
                    {
                        back.Put(type); // CanAccept said yes; never lose the unit
                    }
                    done = true;
                }
            }
        }
        if (done)
        {
            s.Cooldown = ConnectorPeriod - 1;
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
