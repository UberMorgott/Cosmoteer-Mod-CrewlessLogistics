using CrewlessLogistics.Belts;

namespace CrewlessLogistics.Belts.Tests;

/// <summary>§28 connection-aware port edges: look per edge from the real topology, and the sprite slot it draws.</summary>
static partial class WorldProbes
{
    /// <summary>Neighbour kinds placed at an edge; Rel: 0 points into the box, 1 points away, 2 crosses past it, 3 short side faces it (splitter).</summary>
    static readonly (string Name, NodeKind? Kind, bool Cross, int Rel)[] EdgeNeighbours =
    {
        ("empty", null, false, -1), ("room", null, false, -2),
        ("belt in", NodeKind.Belt, false, 0), ("belt away", NodeKind.Belt, false, 1), ("belt past", NodeKind.Belt, false, 2),
        ("crossing in", NodeKind.Belt, true, 0), ("crossing away", NodeKind.Belt, true, 1),
        ("connector in", NodeKind.Connector, false, 0), ("connector away", NodeKind.Connector, false, 1), ("connector past", NodeKind.Connector, false, 2),
        ("splitter in", NodeKind.Splitter, false, 0), ("splitter away", NodeKind.Splitter, false, 1), ("splitter side", NodeKind.Splitter, false, 3),
    };

    /// <summary>Places a neighbour at cell (x, y) relative to the edge's outward direction o.</summary>
    static void PlaceNeighbour(Grid g, (string Name, NodeKind? Kind, bool Cross, int Rel) k, int x, int y, int o)
    {
        int into = (o + 2) % 4, perp = (o + 1) % 4;
        var (px, py) = Dir[perp];
        var (ox, oy) = Dir[o];
        switch (k.Rel)
        {
            case -1:
                return;
            case -2:
                g.Room(x, y, new Cargo(1));
                return;
        }
        int dir = k.Rel switch { 0 => into, 1 => o, _ => perp };
        if (k.Kind == NodeKind.Splitter)
        {
            // Second half away from the box: beside the first along the edge, or behind it (short side facing the box).
            var (sx, sy) = k.Rel == 3 ? (ox, oy) : (px, py);
            g.Add(NodeKind.Splitter, x, y, dir, sx, sy);
        }
        else if (k.Kind == NodeKind.Connector)
        {
            g.Connector(x, y, dir, BeltTypes.FilterAny);
        }
        else
        {
            g.Add(NodeKind.Belt, x, y, dir, noCurve: k.Cross);
        }
    }

    /// <summary>Expected look: an output shows what it hands to, an input what hands to it; a room seals, else closed.</summary>
    static EdgeLook ExpectedLook(bool output, bool side, (string Name, NodeKind? Kind, bool Cross, int Rel) k)
    {
        bool linked = k.Kind != null && (output ? k.Rel != 0 : k.Rel == 0);
        if (linked)
        {
            return k.Kind == NodeKind.Belt ? EdgeLook.Belt : EdgeLook.Coupling;
        }
        return side ? EdgeLook.Wall : k.Rel == -2 ? EdgeLook.Seal : EdgeLook.Cap;
    }

    /// <summary>
    /// §28 edge looks: connector (4 edges) and splitter (6 edges, both half orders) in 4 rotations x 13 neighbour kinds
    /// (empty, room, belt / crossing / connector / splitter pointing in, away, past, splitter short side) against the
    /// expected table; a connector cut out of a closed loop shows closed ports; sprite slots cover 0..35 once, flip-aware.
    /// </summary>
    internal static void EdgeSelect(Action<bool, string> check)
    {
        int bad = 0, total = 0;
        string first = "";
        for (int dir = 0; dir < 4; dir++)
        {
            var (lx, ly) = LeftOf(dir);
            foreach (int flip in new[] { 0, 1 })
            {
                foreach (NodeKind kind in new[] { NodeKind.Connector, NodeKind.Splitter })
                {
                    if (kind == NodeKind.Connector && flip == 1)
                    {
                        continue;
                    }
                    int sx = flip == 0 ? -lx : lx, sy = flip == 0 ? -ly : ly; // half 1 right of travel, left when flipped
                    int edges = kind == NodeKind.Connector ? 4 : 6;
                    for (int e = 0; e < edges; e++)
                    {
                        foreach (var k in EdgeNeighbours)
                        {
                            total++;
                            var g = new Grid();
                            Node box = kind == NodeKind.Connector ? g.Connector(5, 5, dir, BeltTypes.FilterAny) : g.Add(NodeKind.Splitter, 5, 5, dir, sx, sy);
                            var (x, y, ox, oy) = BeltRenderState.Edge(box, e);
                            int o = DirOf(ox, oy);
                            PlaceNeighbour(g, k, x + ox, y + oy, o);
                            g.Build();
                            int j = g.Nodes.FindIndex(n => n != box && (n.X == x + ox && n.Y == y + oy || n.Kind == NodeKind.Splitter && n.X + n.Sx == x + ox && n.Y + n.Sy == y + oy));
                            bool output = kind == NodeKind.Connector ? e == 0 : e < 2;
                            EdgeLook got = BeltRenderState.Look(box, e, j >= 0 ? g.Nodes[j] : null, !g.Free(x + ox, y + oy));
                            EdgeLook want = ExpectedLook(output, e >= 4, k);
                            if (got != want)
                            {
                                bad++;
                                first = first.Length > 0 ? first : $" [{kind} dir {dir} flip {flip} edge {e} neighbour '{k.Name}': {got}, want {want}]";
                            }
                        }
                    }
                }
            }
        }
        // Closed connector loop: the cut connector (first in node order) outputs nothing, so both ends of that joint close.
        var loop = new Grid();
        Node c0 = loop.Connector(0, 0, Right, BeltTypes.FilterAny);
        loop.Connector(1, 0, Down, BeltTypes.FilterAny);
        loop.Connector(1, 1, Left, BeltTypes.FilterAny);
        Node c3 = loop.Connector(0, 1, Up, BeltTypes.FilterAny);
        loop.Build();
        Node c1 = loop.Nodes[1];
        bool cut = BeltRenderState.Look(c0, 0, c1, true) == EdgeLook.Cap && BeltRenderState.Look(c1, 1, c0, true) == EdgeLook.Cap // c0 is right of c1's travel (down)
            && BeltRenderState.Look(c3, 0, c0, true) == EdgeLook.Coupling;
        // Sprite slots: every (kind, edge, look) that draws maps to one of 0..35, each slot used once per flip, connector flip-free.
        bool slotsOk = true;
        foreach (int flip in new[] { 0, 1 })
        {
            var used = new HashSet<int>();
            for (int e = 0; e < 6; e++)
            {
                foreach (EdgeLook l in Enum.GetValues<EdgeLook>())
                {
                    foreach (NodeKind kind in new[] { NodeKind.Connector, NodeKind.Splitter })
                    {
                        if (kind == NodeKind.Connector && e >= 4)
                        {
                            continue;
                        }
                        int s = BeltRenderState.EdgeSprite(kind, e, l, flip == 1);
                        bool draws = l != EdgeLook.Wall && (e < 4 || l is EdgeLook.Belt or EdgeLook.Coupling);
                        slotsOk &= draws ? s is >= 0 and < BeltRenderState.EdgeSprites && used.Add(s) : s == -1;
                        slotsOk &= kind != NodeKind.Connector || s == BeltRenderState.EdgeSprite(kind, e, l, flip == 0);
                        slotsOk &= kind != NodeKind.Splitter || flip == 0 || s == BeltRenderState.EdgeSprite(kind, e ^ 1, l, false);
                    }
                }
            }
            slotsOk &= used.Count == BeltRenderState.EdgeSprites;
        }
        check(bad == 0 && cut && slotsOk,
            $"edge select: {total} cases (connector 4 edges, splitter 6 edges x 2 half orders, 4 rotations, 13 neighbour kinds), bad={bad}{first}; loop cut closes both ends={cut}; sprite slots 0..{BeltRenderState.EdgeSprites - 1} once each, flip-aware={slotsOk}");
    }
}
