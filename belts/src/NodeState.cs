using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace CrewlessLogistics.Belts;

public enum NodeKind : byte
{
    Belt,
    Splitter,
    Connector,
}

/// <summary>
/// Carriable resource types (§7): every registered stackable resource (MaxStackSize &gt; 0, what cargo holds store).
/// Index = sim type id (internal only: payloads carry names, ship metadata carries the table). The 21 vanilla types
/// keep indices 0..20 (and their filter modes) forever; other types follow by lower-case ID, ordinal, rebuilt per rules
/// load (<see cref="Set"/>). Identical mods on every peer give an identical table (<see cref="Fingerprint"/> is hashed).
/// </summary>
public static class BeltTypes
{
    /// <summary>The v0.5 table: v2 payloads and pre-table metadata use exactly these indices.</summary>
    public static readonly string[] Vanilla =
    {
        "sulfur", "iron", "copper", "carbon", "gold", "uranium", "diamond", "hyperium",
        "steel", "coil", "coil2", "tristeel", "tritanium", "processor", "enriched_uranium",
        "bullet", "missile_part_he", "missile_part_emp", "missile_part_nuke", "missile_part_thermal", "mine_part",
    };

    public static string[] Ids { get; private set; } = Vanilla;

    /// <summary>Bit per type in <see cref="Ids"/>: every carriable type (connector allowed set, §20).</summary>
    public static BigInteger AllMask { get; private set; } = (BigInteger.One << Vanilla.Length) - 1;

    /// <summary>FNV-1a of the table, added to the ship integrity hash.</summary>
    public static int Fingerprint { get; private set; } = Hash(Vanilla);

    /// <summary>The table for these registered stackable IDs: vanilla first, then the rest by lower-case ID (engine IDs are case-insensitive).</summary>
    public static string[] Order(IEnumerable<string> stackable) => Vanilla.Concat(stackable.Select(s => s.ToLowerInvariant())
        .Where(s => Array.IndexOf(Vanilla, s) < 0).Distinct().OrderBy(s => s, StringComparer.Ordinal)).ToArray();

    public static void Set(string[] ids)
    {
        Ids = ids;
        AllMask = (BigInteger.One << ids.Length) - 1;
        Fingerprint = Hash(ids);
    }

    private static int Hash(string[] ids)
    {
        uint h = 2166136261;
        foreach (string s in ids)
        {
            foreach (char c in s)
            {
                NodeState.Add(ref h, c);
            }
            NodeState.Add(ref h, 0);
        }
        return (int)h;
    }

    public const int FilterNone = -2;
    public const int FilterAny = -1;

    /// <summary>UIToggle modes (§22): 0 Auto (default; resolved by <see cref="Filter"/>), 1 any, 2+i vanilla type i, 23 explicit None, 3+i type i &gt;= 21.</summary>
    public const int ModeAuto = 0;
    public const int ModeAny = 1;
    public const int ModeNone = 23;

    /// <summary>UIToggle Mode -> filter without neighbour context: Auto, None and out of range pass nothing.</summary>
    public static int FilterFromMode(int mode) => FilterFromMode(mode, Ids.Length);

    private static int FilterFromMode(int mode, int count) => mode == ModeAny ? FilterAny
        : mode >= 2 && mode < ModeNone ? mode - 2
        : mode > ModeNone && mode - 3 < count ? mode - 3
        : FilterNone;

    /// <summary>Connector toggle mode of type i (inverse of <see cref="FilterFromMode(int)"/>).</summary>
    public static int ModeOf(int type) => type < ModeNone - 2 ? type + 2 : type + 3;

    /// <summary>
    /// A saved filter toggle mode written under table <paramref name="saved"/> as a mode of the current table: vanilla
    /// modes never move; a type the current rules lack becomes explicit None (connector, never another type or Auto) or
    /// no filter (splitter: mode 0, 1+i type i).
    /// </summary>
    public static int Remap(int mode, bool splitter, IReadOnlyList<string?> saved)
    {
        if (mode <= (splitter ? Vanilla.Length : ModeNone))
        {
            return mode; // auto, any, none, vanilla types (and negative modes, which the sim reads as before)
        }
        int t = splitter ? mode - 1 : FilterFromMode(mode, saved.Count), now = t >= 0 && t < saved.Count ? Array.IndexOf(Ids, saved[t]?.ToLowerInvariant()) : -1;
        return now < 0 ? (splitter ? 0 : ModeNone) : splitter ? now + 1 : ModeOf(now);
    }

    /// <summary>Types a connector may pass (§20 D2): Out(back part) and In(front part); 0 (no flows) does not narrow; no common type gives 0.</summary>
    public static BigInteger Allowed(BigInteger backOut, BigInteger frontIn) => (backOut.IsZero ? AllMask : backOut) & (frontIn.IsZero ? AllMask : frontIn);

    /// <summary>Filter choices offered (§20 D5, §22): Auto and None always; all -> every choice; one type -> it; else any + the set.</summary>
    public static bool ChoiceAllowed(BigInteger allowed, int mode)
    {
        if (allowed == AllMask || mode == ModeAuto || mode == ModeNone)
        {
            return true;
        }
        int f = FilterFromMode(mode);
        return f >= 0 ? !(allowed >> f).IsEven : mode == ModeAny && !allowed.IsZero && !allowed.IsPowerOfTwo;
    }

    /// <summary>Normalised toggle mode (§22): a choice the neighbours rule out -> Auto; else unchanged (Auto and None are never rewritten).</summary>
    public static int Normalize(BigInteger allowed, int mode) => ChoiceAllowed(allowed, mode) ? mode : ModeAuto;

    /// <summary>Sim filter of a normalised mode (§22): Auto passes the single allowed type, else nothing; other modes as chosen.</summary>
    public static int Filter(BigInteger allowed, int mode) => mode != ModeAuto ? FilterFromMode(mode)
        : !allowed.IsZero && allowed.IsPowerOfTwo ? (int)BigInteger.TrailingZeroCount(allowed) : FilterNone;
}

/// <summary>§26 item handling of a removed belt part (<see cref="NodeState.OnRemoved"/>).</summary>
public enum RemovalDrop : byte
{
    Keep,
    Destroyed,
    Intact,
}

/// <summary>
/// Per-part sim state (§3, §10): lane contents, splitter round-robin, connector cooldown/alternation. Everything that
/// affects the future is here and in the payload; topology and filter are re-derived.
/// </summary>
public sealed class NodeState
{
    /// <summary>v3: types as indices into the payload's own name table (<see cref="Encode"/>); v2: indices into <see cref="BeltTypes.Vanilla"/>.</summary>
    public const int PayloadVersion = 3;

    public readonly NodeKind Kind;
    public readonly LaneTile[] Tiles;
    /// <summary>Splitter: next output per lane. Connector: [0] = next source room (back, left, right).</summary>
    public readonly int[] RoundRobin = new int[2];
    public int Cooldown;
    public int Alternate;
    /// <summary>Connector: types it may pass (§20), derived from the neighbour parts on rebuild/mode change; not saved.</summary>
    public BigInteger Allowed = BeltTypes.AllMask;
    /// <summary>
    /// §23 settings: copied from the part's UIToggles every tick (vanilla saves, copies and MP-syncs the toggles), so not in
    /// the payload; hashed. Splitter: InPriority / OutPriority 0 none, 1 left, 2 right of travel; SplitFilter -1 none, else
    /// a type. Connector: SourcePriority 0 round-robin, 1 back, 2 left, 3 right. 0 / -1 = behaviour before §23.
    /// </summary>
    public int InPriority, OutPriority, SplitFilter = -1, SourcePriority;

    public NodeState(NodeKind kind)
    {
        Kind = kind;
        Tiles = new LaneTile[kind == NodeKind.Belt ? 1 : kind == NodeKind.Splitter ? 2 : 0];
        for (int i = 0; i < Tiles.Length; i++)
        {
            Tiles[i] = new LaneTile();
        }
    }

    /// <summary>
    /// [kind, tiles, per tile per lane: count, held, (pos, type) x count], rr0, rr1, cooldown, alternate. With
    /// <paramref name="names"/> each type (held included) is an index into it, appended on first use (payload v3).
    /// </summary>
    public int[] Encode(List<string>? names = null)
    {
        var d = new List<int> { (int)Kind, Tiles.Length };
        int Type(int t)
        {
            if (names == null || t < 0)
            {
                return t;
            }
            int k = names.IndexOf(BeltTypes.Ids[t]);
            if (k < 0)
            {
                names.Add(BeltTypes.Ids[t]);
                k = names.Count - 1;
            }
            return k;
        }
        foreach (LaneTile s in Tiles)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                d.Add(s.Count[lane]);
                d.Add(Type(Math.Max(s.Drain[lane], -1)));
                for (int i = 0; i < s.Count[lane]; i++)
                {
                    d.Add(s.Pos[lane * LaneSim.K + i]);
                    d.Add(Type(s.Type[lane * LaneSim.K + i]));
                }
            }
        }
        d.Add(RoundRobin[0]);
        d.Add(RoundRobin[1]);
        d.Add(Cooldown);
        d.Add(Alternate);
        return d.ToArray();
    }

    /// <summary>
    /// Validates a saved payload (trust boundary: save files, MP joins). Null when malformed. Without
    /// <paramref name="names"/> types index <see cref="BeltTypes.Vanilla"/> (v2); with it (v3) a name the current rules
    /// lack drops only that item or held item.
    /// </summary>
    public static NodeState? Decode(NodeKind kind, int[]? d, string?[]? names = null)
    {
        if (names != null && Array.IndexOf(names, null) >= 0)
        {
            return null; // ObjectBits round-trips null entries
        }
        int[] map = names == null ? Enumerable.Range(0, BeltTypes.Vanilla.Length).ToArray()
            : Array.ConvertAll(names, n => Array.IndexOf(BeltTypes.Ids, n!.ToLowerInvariant()));
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
                if (!Next(out int c) || c < 0 || c > LaneSim.K || !Next(out int held) || held < -1 || held >= map.Length)
                {
                    return null;
                }
                t.Drain[lane] = held >= 0 && map[held] >= 0 ? map[held] : LaneTile.Closed;
                int n = 0, prev = 0;
                for (int i = 0; i < c; i++)
                {
                    if (!Next(out int pos) || pos < 0 || pos > LaneSim.EndPos || !Next(out int type) || type < 0 || type >= map.Length
                        || (i > 0 && prev - pos < LaneSim.MinGap))
                    {
                        return null;
                    }
                    prev = pos;
                    if (map[type] >= 0) // dropping one item only widens the gaps of the rest
                    {
                        t.Pos[lane * LaneSim.K + n] = pos;
                        t.Type[lane * LaneSim.K + n++] = map[type];
                    }
                }
                t.Count[lane] = n;
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
        Add(ref h, InPriority);
        Add(ref h, OutPriority);
        Add(ref h, SplitFilter);
        Add(ref h, SourcePriority);
        return (int)h;
    }

    /// <summary>A UIToggle mode as a §23 setting: out of range (rules changed, bad input) -> 0, the pre-§23 behaviour.</summary>
    public static int Setting(int? mode, int choices) => mode is int m && m >= 0 && m < choices ? m : 0;

    /// <summary>
    /// §26 what a removed belt part does with its items: a ship split moves the Part (state follows it), Health 0
    /// (weapons or crew deconstruction, PartsManager.OnPartDestroyed) drops like a destroyed vanilla storage, any other
    /// removal (deleted, replaced, ship reset) drops everything.
    /// </summary>
    public static RemovalDrop OnRemoved(bool splitting, bool destroyed) => splitting ? RemovalDrop.Keep : destroyed ? RemovalDrop.Destroyed : RemovalDrop.Intact;

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
