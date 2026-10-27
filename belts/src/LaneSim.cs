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
/// Per-tile two-lane belt sim (§3), pure integer, allocation-free per tick, no game dependencies.
/// Lane slots sorted by pos descending (index 0 = head). Each tile is moved exactly once per tick in a precomputed
/// downstream-first order, so an item entering an already-moved tile never moves twice, and a packed line moves at full
/// speed (a previous-tick snapshot would stall every follower in a packed line: new &lt;= ahead.old - 64 = own pos).
/// </summary>
public sealed class LaneSim
{
    public const int TileLength = 256;
    public const int K = 4;
    public const int MinGap = TileLength / K;
    public const int EndPos = TileLength - 1;
    public const int SideEntryPos = TileLength / 2;
    // 2 tiles/s at 30 sim ticks/s (Data/cosmoteer.rules:76): 512/30 pos per tick, remainder accumulated.
    public const int SpeedNum = 2 * TileLength;
    public const int SpeedDen = 30;

    private readonly int _tiles;
    private int[] _next = Array.Empty<int>();
    private Entry[] _entry = Array.Empty<Entry>();
    private int[] _order = Array.Empty<int>();
    private readonly int[] _count;
    private readonly int[] _pos;
    private readonly int[] _type;
    private const int DrainClosed = -1;
    private const int DrainOpen = -2;
    // Per lane: DrainClosed, DrainOpen, or the type of the item that left (>= 0) until taken.
    private readonly int[] _drain;

    /// <summary>Speed remainder; part of the saved payload (§10).</summary>
    public int Accumulator;

    public LaneSim(int tiles)
    {
        _tiles = tiles;
        _count = new int[tiles * 2];
        _pos = new int[tiles * 2 * K];
        _type = new int[tiles * 2 * K];
        _drain = new int[tiles * 2];
        Array.Fill(_drain, DrainClosed);
    }

    public int Tiles => _tiles;

    public int Count(int tile, int lane) => _count[tile * 2 + lane];

    public int Pos(int tile, int lane, int i) => _pos[(tile * 2 + lane) * K + i];

    public int Type(int tile, int lane, int i) => _type[(tile * 2 + lane) * K + i];

    /// <summary>
    /// Sets topology: next[t] = tile in front (-1 = belt end), entry[t] = how t enters next[t].
    /// Order: belt ends first (ascending index), then one root per cycle; feeders visited rear, left, right, then index,
    /// which is the §5 merge priority (rear first).
    /// </summary>
    public void Build(int[] next, Entry[] entry)
    {
        if (next.Length != _tiles || entry.Length != _tiles)
        {
            throw new ArgumentException("topology size mismatch");
        }
        _next = next;
        _entry = entry;
        var feeders = new List<int>[_tiles];
        for (int t = 0; t < _tiles; t++)
        {
            feeders[t] = new List<int>();
        }
        for (int t = 0; t < _tiles; t++)
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
        var order = new List<int>(_tiles);
        var seen = new bool[_tiles];
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
        for (int t = 0; t < _tiles; t++)
        {
            if (next[t] < 0)
            {
                Visit(t);
            }
        }
        // Every unvisited tile now leads into a cycle: walk to it; the first repeated tile is the cycle root.
        var onPath = new int[_tiles];
        for (int t = 0, mark = 1; t < _tiles; t++, mark++)
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
    }

    /// <summary>Puts an item at pos if the gap rule allows (connector put, §7). Returns false when it does not fit.</summary>
    public bool TryInsert(int tile, int lane, int pos, int type) => TryInsertAt(tile * 2 + lane, pos, type);

    /// <summary>
    /// Connector put at the belt's back (§7): lands right behind the tail, at most into the back quarter of the tile,
    /// so a continuously fed line is exactly compressed (a fixed pos 0 would round spacing up to whole tick steps).
    /// </summary>
    public bool TryPutBack(int tile, int lane, int type)
    {
        int li = tile * 2 + lane;
        int n = _count[li];
        int pos = n == 0 ? MinGap - 1 : Math.Min(_pos[li * K + n - 1] - MinGap, MinGap - 1);
        return pos >= 0 && TryInsertAt(li, pos, type);
    }

    /// <summary>
    /// Connector take at a belt end (§7): the next head that crosses the end edge leaves into the drain during Tick,
    /// keeping its overshoot so followers stay exactly spaced (taking a head parked at 255 caps a lane at
    /// ceil(64/17.07) = 4 ticks per item = 7.5/s instead of 8/s). Stays open until an item leaves.
    /// </summary>
    public void OpenDrain(int tile, int lane)
    {
        int li = tile * 2 + lane;
        if (_drain[li] == DrainClosed)
        {
            _drain[li] = DrainOpen;
        }
    }

    public bool TryTakeDrained(int tile, int lane, out int type)
    {
        int li = tile * 2 + lane;
        type = _drain[li];
        if (type < 0)
        {
            return false;
        }
        _drain[li] = DrainClosed;
        return true;
    }
    public void Tick(int tick)
    {
        Accumulator += SpeedNum;
        int adv = Accumulator / SpeedDen;
        Accumulator %= SpeedDen;
        // Two lanes of a sideloading tile compete for one target lane: alternate who goes first by tick parity (§5).
        int first = tick & 1;
        foreach (int t in _order)
        {
            MoveLane(t, first, adv);
            MoveLane(t, first ^ 1, adv);
        }
    }

    private void MoveLane(int t, int lane, int adv)
    {
        int li = t * 2 + lane;
        int n = _count[li];
        if (n == 0)
        {
            return;
        }
        int b = li * K;
        int nt = _next[t];
        int old = _pos[b];
        int p = old + adv;
        if (nt < 0)
        {
            if (p >= TileLength && _drain[li] == DrainOpen)
            {
                _drain[li] = _type[b];
                RemoveHead(li);
                MoveFollowers(b, 0, n - 1, p, adv);
                return;
            }
            p = Math.Min(p, EndPos);
        }
        else
        {
            Entry e = _entry[t];
            int tl = nt * 2 + (e == Entry.Rear ? lane : e == Entry.SideLeft ? 0 : 1);
            int entryPos = e == Entry.Rear ? 0 : SideEntryPos;
            int tb = tl * K;
            int tn = _count[tl];
            // Nearest target item at/after the entry point bounds the landing spot.
            int maxLanding = EndPos;
            for (int j = tn - 1; j >= 0; j--)
            {
                if (_pos[tb + j] >= entryPos)
                {
                    maxLanding = _pos[tb + j] - MinGap;
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
                    if (_pos[tb + j] < landing)
                    {
                        behind = _pos[tb + j];
                        break;
                    }
                }
                if (tn < K && landing - behind >= MinGap)
                {
                    InsertSorted(tl, landing, _type[b]);
                    RemoveHead(li);
                    n--;
                    MoveFollowers(b, 0, n, p, adv);
                    return;
                }
                p = Math.Max(old, EndPos);
            }
        }
        _pos[b] = p;
        MoveFollowers(b, 1, n, p, adv);
    }

    private void MoveFollowers(int b, int from, int n, int ahead, int adv)
    {
        for (int i = from; i < n; i++)
        {
            int q = _pos[b + i];
            ahead = Math.Max(q, Math.Min(q + adv, ahead - MinGap));
            _pos[b + i] = ahead;
        }
    }

    private bool TryInsertAt(int li, int pos, int type)
    {
        int b = li * K;
        int n = _count[li];
        if (n == K || pos < 0 || pos > EndPos)
        {
            return false;
        }
        for (int j = 0; j < n; j++)
        {
            if (Math.Abs(_pos[b + j] - pos) < MinGap)
            {
                return false;
            }
        }
        InsertSorted(li, pos, type);
        return true;
    }

    private void InsertSorted(int li, int pos, int type)
    {
        int b = li * K;
        int i = _count[li]++;
        for (; i > 0 && _pos[b + i - 1] < pos; i--)
        {
            _pos[b + i] = _pos[b + i - 1];
            _type[b + i] = _type[b + i - 1];
        }
        _pos[b + i] = pos;
        _type[b + i] = type;
    }

    private void RemoveHead(int li)
    {
        int b = li * K;
        int n = --_count[li];
        for (int i = 0; i < n; i++)
        {
            _pos[b + i] = _pos[b + i + 1];
            _type[b + i] = _type[b + i + 1];
        }
    }

    /// <summary>
    /// Order-dependent digest of all state (determinism checks, integrity hash §10). FNV-1a, not System.HashCode,
    /// which is seeded per process and would differ between MP peers.
    /// </summary>
    public int Digest()
    {
        uint h = 2166136261;
        void Add(int v)
        {
            h = (h ^ (uint)v) * 16777619;
        }
        Add(Accumulator);
        for (int li = 0; li < _count.Length; li++)
        {
            Add(_count[li]);
            Add(_drain[li]);
            for (int i = 0; i < _count[li]; i++)
            {
                Add(_pos[li * K + i]);
                Add(_type[li * K + i]);
            }
        }
        return (int)h;
    }
}
