using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Modes;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.UI;
using Cosmoteer.Ships.Rendering;
using Cosmoteer.Simulation;
using Halfling.Geometry;
using Halfling.Graphics;
using Halfling.Scene2D;
using Halfling.Timing;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Per-ship deterministic belt sim (§1): FixedUpdate in bucket -11 Commands, which SimRoot runs sequentially
/// (no SetCustomFixedUpdateBucketHandler for -11, SimRoot.cs:605-618). Not serialized; rebuilt from parts.
/// </summary>
internal sealed class BeltManager : ShipComponent
{
    public const int Bucket = FixedUpdateBuckets.Commands;
    /// <summary>Same layer as cargo contents (storage_2x2 FlexResourceGrid), so every StackSprite is in its atlas (§11).</summary>
    private const string ItemLayer = "doodads_low";
    /// <summary>StackSprites are one tile; shrink to 0.4 tile per item.</summary>
    private const float ItemShrink = -0.3f;

    private readonly List<Node> _nodes = new();
    private readonly List<Part> _nodeParts = new();
    private readonly Dictionary<Part, int> _index = new();
    private readonly Dictionary<Part, PartRoom> _rooms = new();
    private readonly List<ManagedShipQuad> _quads = new();
    private BeltWorld? _world;
    private bool _dirty = true;
    private int _lastTick = int.MinValue;
    private int _dumpAtTick = -1;
    private ID<PartToggleGuiRules> _filterToggle;

    public BeltManager()
    {
        FixedUpdateBucket = Bucket;
    }

    public int ConfiguredBucket => FixedUpdateBucket;

    // public: lib\ is publicized; runtime original is protected and CLR allows widening on override.
    public override void OnShipComponentAttaching(Ship ship)
    {
        ship.Parts.PartAdded += OnPartAdded;
        ship.Parts.PartRemoved += OnPartRemoved;
    }

    public override void OnShipComponentDetached(Ship ship)
    {
        ship.Parts.PartAdded -= OnPartAdded;
        ship.Parts.PartRemoved -= OnPartRemoved;
        HideQuads(0);
    }

    // Dirty flag only; topology is rebuilt inside FixedUpdate (§3).
    private void OnPartAdded(Part _) => _dirty = true;

    private void OnPartRemoved(Part part)
    {
        _dirty = true;
        // Split keeps the Part (and its state) alive on the new ship; a destroyed belt drops its items (§3).
        // shortcut: deconstructed/removed belts lose their items; drop them too once removal vs split is told apart.
        if (part.IsDestroyed && BeltState.TryGet(part, out NodeState? s))
        {
            try
            {
                DropItems(part, s);
            }
            catch (Exception ex)
            {
                Log.Error("drop", ex);
            }
        }
    }

    private void DropItems(Part part, NodeState s)
    {
        Ship? ship = Ship;
        SimRoot? sim = Sim;
        if (ship == null || sim == null)
        {
            return;
        }
        RotRect worldRect = ship.DetTransformShapeToWorld((Rect)part.Rect);
        Vector2 velocity = ship.Physics.Body?.GetLinearVelocityFromWorldPoint(worldRect.Center) ?? default(System.Numerics.Vector2);
        Span<int> counts = stackalloc int[BeltTypes.Ids.Length];
        foreach (LaneTile t in s.Tiles)
        {
            for (int lane = 0; lane < 2; lane++)
            {
                for (int i = 0; i < t.Count[lane]; i++)
                {
                    counts[t.Type[lane * LaneSim.K + i]]++;
                }
                if (t.Drain[lane] >= 0)
                {
                    counts[t.Drain[lane]]++;
                }
            }
            t.Clear();
        }
        for (int type = 0; type < counts.Length; type++)
        {
            if (counts[type] > 0)
            {
                sim.Nuggets.DropNuggets(PartRoom.Ids[type], counts[type], worldRect, NuggetDropSource.Storage, 1f, destroyed: true, ship.IsHumanPlayerShip, ship, velocity);
            }
        }
    }

    public override void FixedUpdate(FixedUpdater fixedUpdater, SceneRoot root)
    {
        Ship? ship = Ship;
        SimRoot? sim = Sim;
        if (ship == null || sim == null)
        {
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        int tick = sim.Tick;
        bool duplicate = tick == _lastTick;
        _lastTick = tick;
        try
        {
            if (_dirty)
            {
                _dirty = false;
                Rebuild(ship);
                _dumpAtTick = tick + 90;
            }
            if (_world != null)
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_nodes[i].Kind == NodeKind.Connector)
                    {
                        _nodes[i].Filter = BeltTypes.FilterFromMode(PartUIToggle.GetOnPart(_nodeParts[i], _filterToggle)?.Mode ?? 0);
                    }
                }
                _world.Tick(tick);
                Render(ship, sim);
            }
        }
        catch (Exception ex)
        {
            Log.Error("tick", ex);
        }
        if (tick == _dumpAtTick)
        {
            Probes.DumpEndpoints(ship);
            Probes.DumpTopology(ship, _nodes, _nodeParts);
        }
        Probes.PushP5(ship, tick);
        Probes.OnTick(sim, tick, duplicate, _nodes.Count, Stopwatch.GetTimestamp() - t0);
    }

    private void Rebuild(Ship ship)
    {
        _filterToggle = new ID<PartToggleGuiRules>(BeltState.FilterToggleId);
        _nodes.Clear();
        _nodeParts.Clear();
        _index.Clear();
        _rooms.Clear();
        foreach (Part part in ship.Parts) // ordered list, never hash-set order
        {
            if (BeltState.KindOf(part) is not { } kind)
            {
                continue;
            }
            PartRules r = part.Rules;
            IntVector2 c = r.GetShipRelativeCell(new IntVector2(0, 0), part.Location, part.Rotation, part.FlipX);
            IntVector2 f = r.GetShipRelativeCell(new IntVector2(0, -1), part.Location, part.Rotation, part.FlipX);
            IntVector2 h = kind == NodeKind.Splitter ? r.GetShipRelativeCell(new IntVector2(1, 0), part.Location, part.Rotation, part.FlipX) : c;
            _index[part] = _nodes.Count;
            _nodes.Add(new Node(BeltState.GetOrCreate(part, kind), c.X, c.Y, f.X - c.X, f.Y - c.Y, h.X - c.X, h.Y - c.Y));
            _nodeParts.Add(part);
        }
        _world = _nodes.Count == 0 ? null : new BeltWorld(_nodes, (x, y) => NodeAt(ship, x, y), (x, y) => RoomAt(ship, x, y));
        if (_world == null)
        {
            HideQuads(0);
        }
    }

    private int NodeAt(Ship ship, int x, int y) =>
        ship.Parts[new IntVector2(x, y)] is { } p && _index.TryGetValue(p, out int i) ? i : -1;

    private IRoom? RoomAt(Ship ship, int x, int y)
    {
        if (ship.Parts[new IntVector2(x, y)] is not { } p || BeltState.KindOf(p) != null)
        {
            return null;
        }
        if (!_rooms.TryGetValue(p, out PartRoom? room))
        {
            _rooms[p] = room = new PartRoom(p, ship);
        }
        return room;
    }

    /// <summary>Client visuals only (reads sim state, never writes it): one pooled ship-layer quad per item (§11).</summary>
    private void Render(Ship ship, SimRoot sim)
    {
        ShipRenderer? renderer = ship.Renderer;
        if (renderer == null)
        {
            return;
        }
        var layer = new ID<ShipRenderLayerRules>(ItemLayer);
        int used = 0;
        foreach (Node n in _nodes)
        {
            for (int half = 0; half < n.State.Tiles.Length; half++)
            {
                LaneTile t = n.State.Tiles[half];
                for (int lane = 0; lane < 2; lane++)
                {
                    for (int i = 0; i < t.Count[lane]; i++)
                    {
                        int type = t.Type[lane * LaneSim.K + i];
                        AtlasSprite[]? sprites = PartRoom.Ids[type].GetResourceRules()?.StackSprites;
                        if (sprites == null || sprites.Length == 0)
                        {
                            continue;
                        }
                        var (x, y) = BeltWorld.ItemPoint(n, half, lane, t.Pos[lane * LaneSim.K + i]);
                        var loc = new Vector2(x, y);
                        if (used == _quads.Count)
                        {
                            _quads.Add(new ManagedShipQuad(renderer.GetLayerQuads(layer), sprites[0], 0, flipU: false, loc, Vector2.Zero, Direction.Zero,
                                ship.WorldUniformScale, Color.White, ship.Rules.RenderLayers[layer].Inflate + ItemShrink, 1f, sim.Clock));
                        }
                        else
                        {
                            _quads[used].SetSprite(sprites[0], sim.Clock);
                            _quads[used].Location = loc;
                        }
                        used++;
                    }
                }
            }
        }
        HideQuads(used);
    }

    private void HideQuads(int from)
    {
        for (int i = from; i < _quads.Count; i++)
        {
            _quads[i].Sprite = null;
        }
    }
}
