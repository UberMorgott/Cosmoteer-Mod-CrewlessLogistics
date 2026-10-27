using System;
using System.Collections.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>Belt surface sprite per tile (docs/BELTS_DESIGN.md §21), index into mod/belt.rules BeltSurfaceSprites.</summary>
public enum Variant : byte
{
    Straight,
    JoinLeft,
    JoinRight,
    JoinBoth,
    /// <summary>Curve-left art; curve right = the same sprite mirrored (flipU).</summary>
    Curve,
    HeatCrossing,
    CableCrossing,
    /// <summary>§25 belt crossing with nothing linked: the straight belt in the crossing frame (belt flows right).</summary>
    PlainCrossing,
    /// <summary>§25 heat pipe linked at the part Up edge, cable at Down (and the reverse).</summary>
    HeatCableCrossing,
    CableHeatCrossing,
}

/// <summary>§25 what a belt crossing's network ports link to at one edge, through any chain of crossings.</summary>
public enum CrossLink : byte
{
    None,
    Heat,
    Cable,
}

/// <summary>
/// Client-only render state (§21, §22): which surface sprite each belt draws, whether it animates (only while its lanes
/// move) and which lamps are lit. Reads the sim (node shape, side inputs, lane contents, transfer counters), never writes
/// it; nothing here is saved or hashed. Arrays are sized on rebuild only; <see cref="Update"/>
/// does not allocate.
/// </summary>
public sealed class BeltRenderState
{
    /// <summary>A lamp shows activity for this many ticks after a transfer, lit on 2-tick on/off phases (blinks at full rate).</summary>
    public const int BlinkTicks = 3;

    /// <summary>A belt surface keeps animating this many ticks after its lanes last moved, so stop-and-go lines do not flicker.</summary>
    public const int HoldTicks = 6;

    private IReadOnlyList<Node> _nodes = Array.Empty<Node>();
    /// <summary>Per node 2 lamps: connector [0], splitter output i [i].</summary>
    private int[] _lastCount = Array.Empty<int>();
    private int[] _lastEvent = Array.Empty<int>();
    /// <summary>Per node: lane contents signature of the last tick and the tick it last changed.</summary>
    private int[] _sig = Array.Empty<int>();
    private int[] _lastMove = Array.Empty<int>();

    public bool[] Lamp = Array.Empty<bool>();
    /// <summary>Per node: its belt surface animates (lanes moved within <see cref="HoldTicks"/>); empty or blocked -> still.</summary>
    public bool[] Moving = Array.Empty<bool>();

    public void Reset(IReadOnlyList<Node> nodes)
    {
        _nodes = nodes;
        int n = nodes.Count;
        _lastCount = new int[2 * n];
        _lastEvent = new int[2 * n];
        Lamp = new bool[2 * n];
        _sig = new int[n];
        _lastMove = new int[n];
        Moving = new bool[n];
        Array.Fill(_lastEvent, int.MinValue / 2);
        Array.Fill(_lastMove, int.MinValue / 2);
        for (int i = 0; i < n; i++)
        {
            _lastCount[2 * i] = nodes[i].Events[0];
            _lastCount[2 * i + 1] = nodes[i].Events[1];
            _sig[i] = Signature(nodes[i]);
        }
    }

    /// <summary>Lane counts + positions of a node (any move changes a position: items advance >= 17 units per moving tick).</summary>
    private static int Signature(Node n)
    {
        uint h = 2166136261;
        foreach (LaneTile t in n.State.Tiles)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                NodeState.Add(ref h, t.Count[lane]);
                for (int i = 0; i < t.Count[lane]; i++)
                {
                    NodeState.Add(ref h, t.Pos[lane * LaneSim.K + i]);
                }
            }
        }
        return (int)h;
    }

    public void Update(int tick)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            if (n.Kind == NodeKind.Belt)
            {
                int s = Signature(n);
                if (s != _sig[i])
                {
                    _sig[i] = s;
                    _lastMove[i] = tick;
                }
                Moving[i] = tick - _lastMove[i] < HoldTicks;
            }
            for (int k = 0; k < 2; k++)
            {
                int j = 2 * i + k;
                if (n.Events[k] != _lastCount[j])
                {
                    _lastCount[j] = n.Events[k];
                    _lastEvent[j] = tick;
                }
                Lamp[j] = tick - _lastEvent[j] < BlinkTicks && (tick & 2) == 0;
            }
        }
    }

    /// <summary>Items drawn per node: belts only; splitter contents sit inside its closed housing, connectors hold none.</summary>
    public static bool DrawsItems(Node n) => n.Kind == NodeKind.Belt;

    /// <summary>§26 damage level of a part sprite with that many levels, as vanilla PartSpriteRules.GetAtlasSpriteForDamage.</summary>
    public static int DamageLevel(float damageFraction, int levels) => Math.Clamp((int)(damageFraction * levels), 0, levels - 1);

    /// <summary>§25 crossing art from what its Up / Down edges link to: one network on both or one edge, both, or none.</summary>
    public static Variant CrossingVariant(CrossLink up, CrossLink down) => (up, down) switch
    {
        (CrossLink.Heat, CrossLink.Cable) => Variant.HeatCableCrossing,
        (CrossLink.Cable, CrossLink.Heat) => Variant.CableHeatCrossing,
        _ when up == CrossLink.Heat || down == CrossLink.Heat => Variant.HeatCrossing,
        _ when up == CrossLink.Cable || down == CrossLink.Cable => Variant.CableCrossing,
        _ => Variant.PlainCrossing,
    };

    /// <summary>Surface sprite of a belt node and whether it is mirrored; crossings never curve nor show joins.</summary>
    public static (Variant V, bool FlipU) Select(Node n, Variant crossing = Variant.Straight)
    {
        if (n.NoCurve)
        {
            return (crossing, false);
        }
        return n.Shape switch
        {
            Shape.CurveLeft => (Variant.Curve, false),
            Shape.CurveRight => (Variant.Curve, true),
            _ => ((Variant)(n.SideIn & 3), false),
        };
    }
}
