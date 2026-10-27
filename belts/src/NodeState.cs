using System;
using System.Collections.Generic;

namespace CrewlessLogistics.Belts;

public enum NodeKind : byte
{
    Belt,
    Splitter,
    Connector,
}

/// <summary>
/// Carriable resource types (§7): the vanilla stackable resources, i.e. the ones with StackSprites (drawable on the
/// belt) that cargo holds store. Index = sim/payload type id and connector filter choice (<see cref="FilterFromMode"/>).
/// Append-only: reordering changes saved payloads and the filter toggle (rules choice order is checked by the probes).
/// </summary>
public static class BeltTypes
{
    public static readonly string[] Ids =
    {
        "sulfur", "iron", "copper", "carbon", "gold", "uranium", "diamond", "hyperium",
        "steel", "coil", "coil2", "tristeel", "tritanium", "processor", "enriched_uranium",
        "bullet", "missile_part_he", "missile_part_emp", "missile_part_nuke", "missile_part_thermal", "mine_part",
    };

    /// <summary>Bit per type in <see cref="Ids"/>: every carriable type (connector allowed set, §20).</summary>
    public const uint AllMask = (1u << 21) - 1;

    public const int FilterNone = -2;
    public const int FilterAny = -1;

    /// <summary>UIToggle modes (§22): 0 Auto (default; resolved by PartFlow.Filter), 1 any, 2+i type i (i &lt; 21), 23 explicit None.</summary>
    public const int ModeAuto = 0;
    public const int ModeAny = 1;
    public const int ModeNone = 23;

    /// <summary>UIToggle Mode -> filter without neighbour context: Auto, None and out of range pass nothing. Types
    /// appended after index 20 take modes 24+ (23 is None).</summary>
    public static int FilterFromMode(int mode) => mode == ModeAny ? FilterAny
        : mode >= 2 && mode < ModeNone ? mode - 2
        : mode > ModeNone && mode - 3 < Ids.Length ? mode - 3
        : FilterNone;
}

/// <summary>
/// Per-part sim state (§3, §10): lane contents, splitter round-robin, connector cooldown/alternation. Everything that
/// affects the future is here and in the payload; topology and filter are re-derived.
/// </summary>
public sealed class NodeState
{
    public const int PayloadVersion = 2;

    public readonly NodeKind Kind;
    public readonly LaneTile[] Tiles;
    /// <summary>Splitter: next output per lane. Connector: [0] = next source room (back, left, right).</summary>
    public readonly int[] RoundRobin = new int[2];
    public int Cooldown;
    public int Alternate;
    /// <summary>Connector: types it may pass (§20), derived from the neighbour parts on rebuild/mode change; not saved.</summary>
    public uint Allowed = BeltTypes.AllMask;

    public NodeState(NodeKind kind)
    {
        Kind = kind;
        Tiles = new LaneTile[kind == NodeKind.Belt ? 1 : kind == NodeKind.Splitter ? 2 : 0];
        for (int i = 0; i < Tiles.Length; i++)
        {
            Tiles[i] = new LaneTile();
        }
    }

    /// <summary>[kind, tiles, per tile per lane: count, held, (pos, type) x count], rr0, rr1, cooldown, alternate.</summary>
    public int[] Encode()
    {
        var d = new List<int> { (int)Kind, Tiles.Length };
        foreach (LaneTile s in Tiles)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                d.Add(s.Count[lane]);
                d.Add(Math.Max(s.Drain[lane], -1));
                for (int i = 0; i < s.Count[lane]; i++)
                {
                    d.Add(s.Pos[lane * LaneSim.K + i]);
                    d.Add(s.Type[lane * LaneSim.K + i]);
                }
            }
        }
        d.Add(RoundRobin[0]);
        d.Add(RoundRobin[1]);
        d.Add(Cooldown);
        d.Add(Alternate);
        return d.ToArray();
    }

    /// <summary>Validates a saved payload (trust boundary: save files, MP joins). Null when malformed.</summary>
    public static NodeState? Decode(NodeKind kind, int[]? d)
    {
        var s = new NodeState(kind);
        int k = 0;
        bool Next(out int v)
        {
            v = d != null && k < d.Length ? d[k] : 0;
            return d != null && k++ < d.Length;
        }
        if (!Next(out int dk) || dk != (int)kind || !Next(out int nt) || nt != s.Tiles.Length)
        {
            return null;
        }
        foreach (LaneTile t in s.Tiles)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                if (!Next(out int c) || c < 0 || c > LaneSim.K || !Next(out int held) || held < -1 || held >= BeltTypes.Ids.Length)
                {
                    return null;
                }
                t.Count[lane] = c;
                t.Drain[lane] = held >= 0 ? held : LaneTile.Closed;
                for (int i = 0; i < c; i++)
                {
                    if (!Next(out int pos) || pos < 0 || pos > LaneSim.EndPos || !Next(out int type) || type < 0 || type >= BeltTypes.Ids.Length
                        || (i > 0 && t.Pos[lane * LaneSim.K + i - 1] - pos < LaneSim.MinGap))
                    {
                        return null;
                    }
                    t.Pos[lane * LaneSim.K + i] = pos;
                    t.Type[lane * LaneSim.K + i] = type;
                }
            }
        }
        if (!Next(out int rr0) || !Next(out int rr1) || !Next(out int cd) || !Next(out int alt) || k != d!.Length)
        {
            return null;
        }
        // Splitter: output per lane (0/1). Connector: [0] = next room to pull from (0..2, §21 D8).
        s.RoundRobin[0] = kind == NodeKind.Connector ? (rr0 is >= 0 and < 3 ? rr0 : 0) : rr0 & 1;
        s.RoundRobin[1] = rr1 & 1;
        s.Cooldown = Math.Max(0, cd);
        s.Alternate = alt & 1;
        return s;
    }

    public int Digest()
    {
        uint h = 2166136261;
        Add(ref h, (int)Kind);
        foreach (LaneTile t in Tiles)
        {
            Mix(ref h, t);
        }
        Add(ref h, RoundRobin[0]);
        Add(ref h, RoundRobin[1]);
        Add(ref h, Cooldown);
        Add(ref h, Alternate);
        return (int)h;
    }

    internal static void Add(ref uint h, int v) => h = (h ^ (uint)v) * 16777619;

    internal static void Mix(ref uint h, LaneTile s)
    {
        for (int lane = 0; lane < 2; lane++)
        {
            Add(ref h, s.Count[lane]);
            Add(ref h, Math.Max(s.Drain[lane], -1));
            for (int i = 0; i < s.Count[lane]; i++)
            {
                Add(ref h, s.Pos[lane * LaneSim.K + i]);
                Add(ref h, s.Type[lane * LaneSim.K + i]);
            }
        }
    }
}
