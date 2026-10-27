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
using Cosmoteer.Ships.Parts.Graphics;
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
    /// <summary>Layer of the BeltItemSprites atlas entries (mod/belt.rules), under crew like cargo contents (§11).</summary>
    private const string ItemLayer = "doodads_low";
    /// <summary>Single-nugget sprites are one tile with the nugget ~0.55 tile wide; 0.4 tile quad -> ~0.22 tile item, fits a lane.</summary>
    private const float ItemShrink = -0.3f;
    private const string ItemSpritesComponent = "BeltItemSprites";
    /// <summary>§21 layers: belt surface between part floors (-700) and items (-500); lamps additive above (Factorio order).</summary>
    private const string SurfaceLayer = "turrets";
    private const string SurfaceSpritesComponent = "BeltSurfaceSprites";
    private const string LampLayer = "lights_add";
    private const string LampSpritesComponent = "BeltLampSprites";
    private readonly BeltRenderState _render = new();
    private ManagedShipQuad?[] _surf = Array.Empty<ManagedShipQuad?>();
    private ManagedShipQuad?[] _lamps = Array.Empty<ManagedShipQuad?>();
    private bool _visualsDirty;

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
    /// <summary>UI toggles of the parts in front of / behind connectors: a mode change (launcher type) re-derives §20 sets.</summary>
    private readonly List<PartUIToggle> _watched = new();
    private bool _flowDirty;

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
        Unwatch();
        HideQuads(0);
        DisposeVisuals();
    }

    private void OnNeighbourMode(object? sender, EventArgs e) => _flowDirty = true;

    private void Unwatch()
    {
        foreach (PartUIToggle t in _watched)
        {
            t.ModeChanged -= OnNeighbourMode;
        }
        _watched.Clear();
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
                if (_flowDirty)
                {
                    _flowDirty = false;
                    UpdateAllowed(ship);
                }
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (_nodes[i].Kind == NodeKind.Connector)
                    {
                        // §20 D3: auto-set a single type / reset a choice the neighbours rule out. Sim state on every peer.
                        PartUIToggle? toggle = PartUIToggle.GetOnPart(_nodeParts[i], _filterToggle);
                        int mode = toggle?.Mode ?? 0, normal = PartFlow.Normalize(_nodes[i].State.Allowed, mode);
                        if (toggle != null && normal != mode)
                        {
                            toggle.Mode = normal;
                        }
                        _nodes[i].Filter = BeltTypes.FilterFromMode(normal);
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
            // Construction completion replaces the Part (PartConstructionTracker.cs:258-259), so a site must not hold items.
            if (part.IsUnderConstruction || BeltState.KindOf(part) is not { } kind)
            {
                continue;
            }
            PartRules r = part.Rules;
            IntVector2 c = r.GetShipRelativeCell(new IntVector2(0, 0), part.Location, part.Rotation, part.FlipX);
            bool crossing = BeltState.IsCrossing(part);
            IntVector2 f = r.GetShipRelativeCell(crossing ? new IntVector2(1, 0) : new IntVector2(0, -1), part.Location, part.Rotation, part.FlipX);
            IntVector2 h = kind == NodeKind.Splitter ? r.GetShipRelativeCell(new IntVector2(1, 0), part.Location, part.Rotation, part.FlipX) : c;
            _index[part] = _nodes.Count;
            _nodes.Add(new Node(BeltState.GetOrCreate(part, kind), c.X, c.Y, f.X - c.X, f.Y - c.Y, h.X - c.X, h.Y - c.Y, crossing));
            _nodeParts.Add(part);
        }
        _world = _nodes.Count == 0 ? null : new BeltWorld(_nodes, (x, y) => NodeAt(ship, x, y), (x, y) => RoomAt(ship, x, y));
        if (_world == null)
        {
            HideQuads(0);
        }
        DisposeVisuals();
        _visualsDirty = _world != null;
        Unwatch();
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Kind != NodeKind.Connector)
            {
                continue;
            }
            Node n = _nodes[i];
            // Back, front and both sides (§21 D8 side rooms feed the allowed set too).
            foreach (IntVector2 cell in new[] { new IntVector2(n.X - n.Dx, n.Y - n.Dy), new IntVector2(n.X + n.Dx, n.Y + n.Dy), new IntVector2(n.X + n.Dy, n.Y - n.Dx), new IntVector2(n.X - n.Dy, n.Y + n.Dx) })
            {
                if (ship.Parts[cell] is not { } p || BeltState.KindOf(p) != null)
                {
                    continue;
                }
                foreach (ID<PartToggleGuiRules> id in p.Rules.UIToggleComponentsByID.Keys)
                {
                    if (PartUIToggle.GetOnPart(p, id) is { } t && !_watched.Contains(t))
                    {
                        t.ModeChanged += OnNeighbourMode;
                        _watched.Add(t);
                    }
                }
            }
        }
        _flowDirty = true;
    }

    /// <summary>§20 D2 allowed set per connector from the live neighbours (rules + current toggle modes, no endpoint scan).</summary>
    private void UpdateAllowed(Ship ship)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Kind == NodeKind.Connector)
            {
                _nodes[i].State.Allowed = PartFlow.ForConnector(_nodeParts[i], cell => ship.Parts[cell], p => ((Part)p).IsUnderConstruction,
                    p => PartFlow.LiveModes((Part)p));
            }
        }
    }

    private int NodeAt(Ship ship, int x, int y) =>
        ship.Parts[new IntVector2(x, y)] is { } p && _index.TryGetValue(p, out int i) ? i : -1;

    private IRoom? RoomAt(Ship ship, int x, int y)
    {
        if (ship.Parts[new IntVector2(x, y)] is not { } p || p.IsUnderConstruction || BeltState.KindOf(p) != null)
        {
            return null;
        }
        if (!_rooms.TryGetValue(p, out PartRoom? room))
        {
            _rooms[p] = room = new PartRoom(p, ship);
        }
        return room;
    }

    private static AtlasSprite[][]? Sprites(Ship ship, string component, int count)
    {
        return ship.Rules.PartsByID.TryGetValue(new ID<PartRules>(BeltState.BeltId), out PartRules? belt)
            && belt.ComponentsByID.TryGetValue(new ID<PartComponentRules>(component), out PartComponentRules? c)
            && c is PartSpriteRules { RandomDamageLevelSprites: { } s } && s.Length >= count ? s : null;
    }

    /// <summary>
    /// §21 surface + lamp quads, created once per topology epoch (never per tick). Belt tiles: animated surface on
    /// <see cref="SurfaceLayer"/> over the static rules floor (fallback without DLL). Connector/splitter: lamp overlays.
    /// </summary>
    private void BuildVisuals(Ship ship, ShipRenderer renderer, SimRoot sim)
    {
        DisposeVisuals();
        _render.Reset(_nodes);
        AtlasSprite[][]? surf = Sprites(ship, SurfaceSpritesComponent, (int)Variant.CableCrossing + 1);
        AtlasSprite[][]? lamps = Sprites(ship, LampSpritesComponent, 3);
        var surfLayer = new ID<ShipRenderLayerRules>(SurfaceLayer);
        var lampLayer = new ID<ShipRenderLayerRules>(LampLayer);
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            Part p = _nodeParts[i];
            var center = (Vector2)p.Location + (Vector2)p.Size / 2f;
            if (n.Kind == NodeKind.Belt && surf != null)
            {
                var (v, flip) = BeltRenderState.Select(n, p.Rules.ID.ToString() switch
                {
                    BeltState.HeatCrossingId => Variant.HeatCrossing,
                    BeltState.CableCrossingId => Variant.CableCrossing,
                    _ => Variant.Straight,
                });
                // Created still (speed 0 = frozen on the global-phase frame); Render starts it once its lanes move (§22).
                _surf[i] = new ManagedShipQuad(renderer.GetLayerQuads(surfLayer), surf[(int)v][0], 0, flip ^ p.FlipX, center, Vector2.Zero, p.Direction,
                    ship.WorldUniformScale, Color.White, ship.Rules.RenderLayers[surfLayer].Inflate, 0f, sim.Clock);
            }
            else if (n.Kind != NodeKind.Belt && lamps != null)
            {
                for (int k = 0; k < (n.Kind == NodeKind.Splitter ? 2 : 1); k++)
                {
                    _lamps[2 * i + k] = new ManagedShipQuad(renderer.GetLayerQuads(lampLayer), lamps[n.Kind == NodeKind.Splitter ? 1 + k : 0][0], 0, p.FlipX, center,
                        Vector2.Zero, p.Direction, ship.WorldUniformScale, Color.TransparentWhite, ship.Rules.RenderLayers[lampLayer].Inflate, 1f, sim.Clock);
                }
            }
        }
    }

    private void DisposeVisuals()
    {
        for (int i = 0; i < _surf.Length; i++)
        {
            if (_surf[i] is { } q)
            {
                q.Sprite = null;
            }
        }
        for (int i = 0; i < _lamps.Length; i++)
        {
            if (_lamps[i] is { } q)
            {
                q.Sprite = null;
            }
        }
        _surf = new ManagedShipQuad?[_nodes.Count];
        _lamps = new ManagedShipQuad?[2 * _nodes.Count];
    }

    /// <summary>Client visuals only (reads sim state, never writes it): surfaces, lamps, one pooled quad per item (§11, §21).</summary>
    private void Render(Ship ship, SimRoot sim)
    {
        ShipRenderer? renderer = ship.Renderer;
        if (renderer == null)
        {
            return;
        }
        if (_visualsDirty)
        {
            _visualsDirty = false;
            BuildVisuals(ship, renderer, sim);
        }
        _render.Update(sim.Tick);
        for (int i = 0; i < _surf.Length; i++)
        {
            if (_surf[i] is { } q && q.AnimationSpeedFactor != 0f != _render.Moving[i])
            {
                // Pause freezes the current frame (ManagedShipQuad.cs:234); resume re-syncs to the global phase so moving
                // neighbours stay seamless.
                q.UpdateAnimationSpeedFactor(_render.Moving[i] ? 1f : 0f, sim.Clock);
                if (_render.Moving[i] && q.Sprite is { } s)
                {
                    q.AnimationStartTime = s.GetAnimationStartTime(sim.Clock);
                }
            }
        }
        for (int j = 0; j < _lamps.Length; j++)
        {
            if (_lamps[j] is { } q)
            {
                q.Color = _render.Lamp[j] ? Color.White : Color.TransparentWhite;
            }
        }
        if (Sprites(ship, ItemSpritesComponent, BeltTypes.Ids.Length) is not { } sprites)
        {
            return;
        }
        var layer = new ID<ShipRenderLayerRules>(ItemLayer);
        int used = 0;
        foreach (Node n in _nodes)
        {
            // Splitter contents are inside its closed housing (Factorio: structure above items); connectors hold none.
            for (int half = 0; BeltRenderState.DrawsItems(n) && half < n.State.Tiles.Length; half++)
            {
                LaneTile t = n.State.Tiles[half];
                for (int lane = 0; lane < 2; lane++)
                {
                    for (int i = 0; i < t.Count[lane]; i++)
                    {
                        int type = t.Type[lane * LaneSim.K + i];
                        AtlasSprite? sprite = sprites[type][0];
                        var (x, y) = BeltWorld.ItemPoint(n, half, lane, t.Pos[lane * LaneSim.K + i]);
                        var loc = new Vector2(x, y);
                        if (used == _quads.Count)
                        {
                            _quads.Add(new ManagedShipQuad(renderer.GetLayerQuads(layer), sprite, 0, flipU: false, loc, Vector2.Zero, Direction.Zero,
                                ship.WorldUniformScale, Color.White, ship.Rules.RenderLayers[layer].Inflate + ItemShrink, 1f, sim.Clock));
                        }
                        else
                        {
                            _quads[used].SetSprite(sprite, sim.Clock);
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
