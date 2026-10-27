using System;
using System.Collections.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>How a tile's items enter its next tile (derived from §4/§5 classification, never saved).</summary>
public enum Entry : byte
{
    /// <summary>Into the back of a belt or into a curve: lanes preserved, entry pos 0.</summary>
    Rear,
    /// <summary>Sideload from the target's left: both lanes onto target L at pos 128.</summary>
    SideLeft,
    /// <summary>Sideload from the target's right: both lanes onto target R at pos 128.</summary>
    SideRight,
}

/// <summary>
/// Two lanes of one belt tile (or splitter half). Owned by the part's state, so contents move with the part on
/// ship split (§3) and survive topology rebuilds. Lane slots sorted by pos descending (index 0 = head).
/// </summary>
public sealed class LaneTile
{
    public const int Closed = -1;
    public const int Open = -2;

    public readonly int[] Count = new int[2];
    public readonly int[] Pos = new int[2 * LaneSim.K];
    public readonly int[] Type = new int[2 * LaneSim.K];
    /// <summary>Per lane: Closed, Open (transient, within one tick), or the type (>= 0) of a drained item until taken.</summary>
    public readonly int[] Drain = { Closed, Closed };
    /// <summary>Transient per tick: -1 any, else the only type that may drain.</summary>
    public readonly int[] DrainFilter = { -1, -1 };

    public void Clear()
    {
        Count[0] = Count[1] = 0;
        Drain[0] = Drain[1] = Closed;
    }
}

/// <summary>
/// Two-lane belt sim (§3), pure integer, allocation-free per tick, no game dependencies.
/// Each tile is moved exactly once per tick in a precomputed downstream-first order, so an item entering an
/// already-moved tile never moves twice, and a packed line moves at full speed (a previous-tick snapshot would stall
/// every follower in a packed line: new &lt;= ahead.old - 64 = own pos).
/// </summary>
public sealed class LaneSim
{
    public const int TileLength = 256;
    public const int K = 4;
    public const int MinGap = TileLength / K;
    public const int EndPos = TileLength - 1;
    public const int SideEntryPos = TileLength / 2;
    // 2 tiles/s at 30 sim ticks/s (Data/cosmoteer.rules:76): 512/30 pos per tick, fractional part derived from Tick.
    public const int SpeedNum = 2 * TileLength;
    public const int SpeedDen = 30;

    private readonly LaneTile[] _t;
    private int[] _next = Array.Empty<int>();
    private Entry[] _entry = Array.Empty<Entry>();
    private int[] _order = Array.Empty<int>();

    public LaneSim(int tiles) : this(NewTiles(tiles))
    {
    }

    public LaneSim(LaneTile[] tiles)
    {
        _t = tiles;
    }

    private static LaneTile[] NewTiles(int n)
    {
        var t = new LaneTile[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = new LaneTile();
        }
        return t;
    }

    public int Tiles => _t.Length;

    public LaneTile Tile(int tile) => _t[tile];

    public int Count(int tile, int lane) => _t[tile].Count[lane];

    public int Pos(int tile, int lane, int i) => _t[tile].Pos[lane * K + i];

    public int Type(int tile, int lane, int i) => _t[tile].Type[lane * K + i];

    /// <summary>
    /// Pos units moved in tick: floor((tick+1)*512/30) - floor(tick*512/30). Pure function of the sim Tick, so no speed
    /// remainder needs saving and every peer and rejoiner agrees.
    /// </summary>
    public static int Advance(int tick) => (int)((tick + 1L) * SpeedNum / SpeedDen - (long)tick * SpeedNum / SpeedDen);

    /// <summary>
    /// Sets topology: next[t] = tile in front (-1 = belt end), entry[t] = how t enters next[t].
    /// Order: belt ends first (ascending index), then one root per cycle; feeders visited rear, left, right, then index,
    /// which is the §5 merge priority (rear first). Drained-but-untaken items go back onto their lane head if it fits.
    /// </summary>
    public void Build(int[] next, Entry[] entry)
    {
        int n = _t.Length;
        if (next.Length != n || entry.Length != n)
        {
            throw new ArgumentException("topology size mismatch");
        }
        _next = next;
        _entry = entry;
        var feeders = new List<int>[n];
        for (int t = 0; t < n; t++)
        {
            feeders[t] = new List<int>();
        }
        for (int t = 0; t < n; t++)
        {
            if (next[t] >= 0)
            {
                feeders[next[t]].Add(t);
            }
        }
        foreach (List<int> f in feeders)
        {
            f.Sort((a, b) => entry[a] != entry[b] ? entry[a].CompareTo(entry[b]) : a.CompareTo(b));
        }
        var order = new List<int>(n);
        var seen = new bool[n];
        void Visit(int root)
        {
            int start = order.Count;
            seen[root] = true;
            order.Add(root);
            for (int i = start; i < order.Count; i++)
            {
                foreach (int f in feeders[order[i]])
                {
                    if (!seen[f])
                    {
                        seen[f] = true;
                        order.Add(f);
                    }
                }
            }
        }
        for (int t = 0; t < n; t++)
        {
            if (next[t] < 0)
            {
                Visit(t);
            }
        }
        // Every unvisited tile now leads into a cycle: walk to it; the first repeated tile is the cycle root.
        var onPath = new int[n];
        for (int t = 0, mark = 1; t < n; t++, mark++)
        {
            int c = t;
            while (!seen[c] && onPath[c] != mark)
            {
                onPath[c] = mark;
                c = next[c];
            }
            if (!seen[c])
            {
                Visit(c);
            }
        }
        _order = order.ToArray();
        foreach (LaneTile s in _t)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                ReturnDrained(s, lane);
                if (s.Drain[lane] < 0)
                {
                    s.Drain[lane] = LaneTile.Closed;
                }
            }
        }
    }

    /// <summary>Puts a held drained item back onto its lane head if it fits; else it stays held.</summary>
    private static void ReturnDrained(LaneTile s, int lane)
    {
        int held = s.Drain[lane];
        int c = s.Count[lane];
        if (held >= 0 && c < K && (c == 0 || s.Pos[lane * K] <= EndPos - MinGap))
        {
            InsertSorted(s, lane, EndPos, held);
            s.Drain[lane] = LaneTile.Closed;
        }
    }

    /// <summary>Puts an item at pos if the gap rule allows (connector side put, splitter side output).</summary>
    public bool TryInsert(int tile, int lane, int pos, int type)
    {
        if (!Fits(tile, lane, pos))
        {
            return false;
        }
        InsertSorted(_t[tile], lane, pos, type);
        return true;
    }

    public bool Fits(int tile, int lane, int pos)
    {
        LaneTile s = _t[tile];
        int b = lane * K;
        int n = s.Count[lane];
        if (n == K || pos < 0 || pos > EndPos)
        {
            return false;
        }
        for (int j = 0; j < n; j++)
        {
            if (Math.Abs(s.Pos[b + j] - pos) < MinGap)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Connector/splitter put at the belt's back (§7): right behind the tail, at most into the back quarter of the tile,
    /// so a continuously fed line is exactly compressed. Returns the landing pos, or -1 when it does not fit.
    /// </summary>
    public int PutBackPos(int tile, int lane)
    {
        LaneTile s = _t[tile];
        int n = s.Count[lane];
        int pos = n == 0 ? MinGap - 1 : Math.Min(s.Pos[lane * K + n - 1] - MinGap, MinGap - 1);
        return pos >= 0 && Fits(tile, lane, pos) ? pos : -1;
    }

    public bool TryPutBack(int tile, int lane, int type)
    {
        int pos = PutBackPos(tile, lane);
        return pos >= 0 && TryInsert(tile, lane, pos, type);
    }

    /// <summary>
    /// Connector/splitter take at a belt end (§7): during the next Tick the head that crosses the end edge (matching
    /// filter, -1 = any) leaves into the drain, keeping its overshoot so followers stay exactly spaced. Open lasts one
    /// Tick; a drained item stays held (saved) until <see cref="TryTakeDrained"/>.
    /// </summary>
    public void OpenDrain(int tile, int lane, int filter = -1)
    {
        LaneTile s = _t[tile];
        if (s.Drain[lane] < 0)
        {
            s.Drain[lane] = LaneTile.Open;
            s.DrainFilter[lane] = filter;
        }
    }

    public int PeekDrained(int tile, int lane) => Math.Max(_t[tile].Drain[lane], -1);

    public bool TryTakeDrained(int tile, int lane, out int type)
    {
        LaneTile s = _t[tile];
        type = s.Drain[lane];
        if (type < 0)
        {
            return false;
        }
        s.Drain[lane] = LaneTile.Closed;
        return true;
    }

    public void Tick(int tick)
    {
        int adv = Advance(tick);
        // Two lanes of a sideloading tile compete for one target lane: alternate who goes first by tick parity (§5).
        int first = tick & 1;
        foreach (int t in _order)
        {
            MoveLane(t, first, adv);
            MoveLane(t, first ^ 1, adv);
        }
        for (int t = 0; t < _t.Length; t++)
        {
            LaneTile s = _t[t];
            for (int lane = 0; lane < 2; lane++)
            {
                if (s.Drain[lane] == LaneTile.Open)
                {
                    s.Drain[lane] = LaneTile.Closed;
                }
                else if (t < _next.Length && _next[t] >= 0)
                {
                    // No taker on a mid-belt tile (end replaced by a belt): return the held item once the head moves on.
                    ReturnDrained(s, lane);
                }
            }
        }
    }

    private void MoveLane(int t, int lane, int adv)
    {
        LaneTile s = _t[t];
        int n = s.Count[lane];
        if (n == 0)
        {
            return;
        }
        int b = lane * K;
        int nt = _next[t];
        int old = s.Pos[b];
        int p = old + adv;
        if (nt < 0)
        {
            if (p >= TileLength && s.Drain[lane] == LaneTile.Open && (s.DrainFilter[lane] < 0 || s.DrainFilter[lane] == s.Type[b]))
            {
                s.Drain[lane] = s.Type[b];
                RemoveHead(s, lane);
                MoveFollowers(s, b, 0, n - 1, p, adv);
                return;
            }
            p = Math.Min(p, EndPos);
        }
        else
        {
            Entry e = _entry[t];
            LaneTile d = _t[nt];
            int tlane = e == Entry.Rear ? lane : e == Entry.SideLeft ? 0 : 1;
            int entryPos = e == Entry.Rear ? 0 : SideEntryPos;
            int tb = tlane * K;
            int tn = d.Count[tlane];
            // Nearest target item at/after the entry point bounds the landing spot.
            int maxLanding = EndPos;
            for (int j = tn - 1; j >= 0; j--)
            {
                if (d.Pos[tb + j] >= entryPos)
                {
                    maxLanding = d.Pos[tb + j] - MinGap;
                    break;
                }
            }
            p = Math.Max(old, Math.Min(p, TileLength + maxLanding - entryPos));
            if (p >= TileLength)
            {
                int landing = entryPos + p - TileLength;
                int behind = -MinGap; // none: any landing >= 0 fits
                for (int j = 0; j < tn; j++)
                {
                    if (d.Pos[tb + j] < landing)
                    {
                        behind = d.Pos[tb + j];
                        break;
                    }
                }
                if (tn < K && landing - behind >= MinGap)
                {
                    InsertSorted(d, tlane, landing, s.Type[b]);
                    RemoveHead(s, lane);
                    n--;
                    MoveFollowers(s, b, 0, n, p, adv);
                    return;
                }
                p = Math.Max(old, EndPos);
            }
        }
        s.Pos[b] = p;
        MoveFollowers(s, b, 1, n, p, adv);
    }

    private static void MoveFollowers(LaneTile s, int b, int from, int n, int ahead, int adv)
    {
        for (int i = from; i < n; i++)
        {
            int q = s.Pos[b + i];
            ahead = Math.Max(q, Math.Min(q + adv, ahead - MinGap));
            s.Pos[b + i] = ahead;
        }
    }

    private static void InsertSorted(LaneTile s, int lane, int pos, int type)
    {
        int b = lane * K;
        int i = s.Count[lane]++;
        for (; i > 0 && s.Pos[b + i - 1] < pos; i--)
        {
            s.Pos[b + i] = s.Pos[b + i - 1];
            s.Type[b + i] = s.Type[b + i - 1];
        }
        s.Pos[b + i] = pos;
        s.Type[b + i] = type;
    }

    private static void RemoveHead(LaneTile s, int lane)
    {
        int b = lane * K;
        int n = --s.Count[lane];
        for (int i = 0; i < n; i++)
        {
            s.Pos[b + i] = s.Pos[b + i + 1];
            s.Type[b + i] = s.Type[b + i + 1];
        }
    }

    /// <summary>
    /// Order-dependent digest of all state (determinism checks). FNV-1a, not System.HashCode, which is seeded per process
    /// and would differ between MP peers.
    /// </summary>
    public int Digest()
    {
        uint h = 2166136261;
        foreach (LaneTile s in _t)
        {
            NodeState.Mix(ref h, s);
        }
        return (int)h;
    }
}
