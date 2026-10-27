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
}

/// <summary>
/// Client-only render state (§21): which surface sprite each belt draws and which lamps are lit. Surfaces always
/// animate in the global phase (Factorio parity, agreed with Codex). Reads the sim (node shape, side inputs, transfer
/// counters), never writes it; nothing here is saved or hashed. Arrays are sized on rebuild only; <see cref="Update"/>
/// does not allocate.
/// </summary>
public sealed class BeltRenderState
{
    /// <summary>A lamp shows activity for this many ticks after a transfer, lit on 2-tick on/off phases (blinks at full rate).</summary>
    public const int BlinkTicks = 3;

    private IReadOnlyList<Node> _nodes = Array.Empty<Node>();
    /// <summary>Per node 2 lamps: connector [0], splitter output i [i].</summary>
    private int[] _lastCount = Array.Empty<int>();
    private int[] _lastEvent = Array.Empty<int>();

    public bool[] Lamp = Array.Empty<bool>();

    public void Reset(IReadOnlyList<Node> nodes)
    {
        _nodes = nodes;
        int n = nodes.Count;
        _lastCount = new int[2 * n];
        _lastEvent = new int[2 * n];
        Lamp = new bool[2 * n];
        Array.Fill(_lastEvent, int.MinValue / 2);
        for (int i = 0; i < n; i++)
        {
            _lastCount[2 * i] = nodes[i].Events[0];
            _lastCount[2 * i + 1] = nodes[i].Events[1];
        }
    }

    public void Update(int tick)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
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
