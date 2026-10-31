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
using Cosmoteer.Ships.Parts.Logic;
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
    /// <summary>§28 port edge looks of connectors / splitters, static quads on the surface layer (above floors, below items).</summary>
    private const string EdgeSpritesComponent = "BeltEdgeSprites";
    private const int MaxEdges = 6;
    private readonly BeltRenderState _render = new();
    private ManagedShipQuad?[] _surf = Array.Empty<ManagedShipQuad?>();
    private Variant[] _surfV = Array.Empty<Variant>();
    private int[] _surfL = Array.Empty<int>();
    private AtlasSprite[][]? _surfSprites;
    private ManagedShipQuad?[] _lamps = Array.Empty<ManagedShipQuad?>();
    /// <summary>§28 per node <see cref="MaxEdges"/> slots: edge quad, its BeltEdgeSprites index and damage level.</summary>
    private ManagedShipQuad?[] _edges = Array.Empty<ManagedShipQuad?>();
    private int[] _edgeS = Array.Empty<int>(), _edgeL = Array.Empty<int>();
    private AtlasSprite[][]? _edgeSprites;
    private bool _visualsDirty;
    /// <summary>§27 client view state: drawn in the last frame, coarse LOD, tick last rendered, item quads shown / resident.</summary>
    private bool _shown, _coarse;
    private int _renderedTick = int.MinValue, _live, _resident, _shownSurfaces, _shownLamps;

    private readonly List<Node> _nodes = new();
    private readonly List<Part> _nodeParts = new();
    private readonly Dictionary<Part, int> _index = new();
    private readonly Dictionary<Part, PartRoom> _rooms = new();
    private readonly List<ManagedShipQuad> _quads = new();
    private BeltWorld? _world;
    private bool _dirty = true;
    private int _lastTick = int.MinValue;
    private int _dumpAtTick = -1;
    private ID<PartToggleGuiRules> _filterToggle, _inputPriority, _outputPriority, _splitterFilter, _sourcePriority;
    /// <summary>UI toggles of the parts in front of / behind connectors: a mode change (launcher type) re-derives §20 sets.</summary>
    private readonly List<PartUIToggle> _watched = new();
    private bool _flowDirty;

    public BeltManager()
    {
        FixedUpdateBucket = Bucket;
    }

    public int ConfiguredBucket => FixedUpdateBucket;

    /// <summary>
    /// Both callbacks dispatched. A ship without belt parts sleeps after its rebuild and wakes on a belt part added
    /// (vanilla pattern: ConstructionManager / ShipCrewManager toggle FixedUpdatingEnabled), so beltless ships cost nothing.
    /// </summary>
    public bool Awake
    {
        get => FixedUpdatingEnabled && UpdatingEnabled;
        set => FixedUpdatingEnabled = UpdatingEnabled = value;
    }

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

    // Dirty flag only; topology is rebuilt inside FixedUpdate (§3). Belt parts (sites too) wake a sleeping manager.
    private void OnPartAdded(Part part)
    {
        _dirty = true;
        if (BeltState.KindOf(part) != null)
        {
            Awake = true;
        }
    }

    private void OnPartRemoved(Part part)
    {
        _dirty = true;
        // §26: a split moves the Part (and its state) to the new ship (StructureContiguityManager.cs:112-145); every other
        // removal drops the items: destroyed / deconstructed (Health 0) or removed intact (deleted, replaced, reset).
        RemovalDrop drop = NodeState.OnRemoved(Ship?.StructureContiguity.IsSplitting ?? false, part.IsDestroyed);
        if (drop != RemovalDrop.Keep && BeltState.TryGet(part, out NodeState? s))
        {
            try
            {
                DropItems(part, s, drop == RemovalDrop.Destroyed);
            }
            catch (Exception ex)
            {
                Log.Error("drop", ex);
            }
        }
    }

    /// <summary>
    /// Items as nuggets, like vanilla ResourceStorage: destroyed = OnDestroying (ResourceStorage.cs:205-222: salvage
    /// fraction, destroyed unless salvaged), intact = removal drop (ResourceStorage.cs:197: everything). Tiles are cleared,
    /// so a repeated event drops nothing twice.
    /// </summary>
    private void DropItems(Part part, NodeState s, bool destroyed)
    {
        Ship? ship = Ship;
        SimRoot? sim = Sim;
        if (ship == null || sim == null)
        {
            return;
        }
        RotRect worldRect = ship.DetTransformShapeToWorld((Rect)part.Rect);
        Vector2 velocity = ship.Physics.Body?.GetLinearVelocityFromWorldPoint(worldRect.Center) ?? default(System.Numerics.Vector2);
        bool isPlayer = (destroyed && (part.Flags & PartFlags.BuiltByPlayer) != PartFlags.None) || ship.IsHumanPlayerShip;
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
                sim.Nuggets.DropNuggets(PartRoom.Ids[type], counts[type], worldRect, NuggetDropSource.Storage, destroyed ? part.SalvageFraction : 1f,
                    destroyed && (part.Flags & PartFlags.Salvaged) == PartFlags.None, isPlayer, ship, velocity);
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
                Awake = _world != null;
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
                        // §22: reset a choice the neighbours rule out to Auto (Auto/None never rewritten); Auto resolves to
                        // the single allowed type. Sim state on every peer.
                        PartUIToggle? toggle = PartUIToggle.GetOnPart(_nodeParts[i], _filterToggle);
                        uint allowed = _nodes[i].State.Allowed;
                        int mode = toggle?.Mode ?? BeltTypes.ModeAuto, normal = PartFlow.Normalize(allowed, mode);
                        if (toggle != null && normal != mode)
                        {
                            toggle.Mode = normal;
                        }
                        _nodes[i].Filter = PartFlow.Filter(allowed, normal);
                        _nodes[i].State.SourcePriority = Setting(i, _sourcePriority, 4);
                    }
                    else if (_nodes[i].Kind == NodeKind.Splitter)
                    {
                        NodeState st = _nodes[i].State;
                        st.InPriority = Setting(i, _inputPriority, 3);
                        st.OutPriority = Setting(i, _outputPriority, 3);
                        st.SplitFilter = Setting(i, _splitterFilter, BeltTypes.Ids.Length + 1) - 1;
                    }
                }
                _world.Tick(tick);
            }
        }
        catch (Exception ex)
        {
            Log.Error("tick", ex);
        }
        if (tick == _dumpAtTick)
        {
            Probes.DumpTopology(ship, _nodes, _nodeParts);
        }
        Probes.OnTick(sim, tick, duplicate, _nodes.Count, _world?.Sim.Tiles ?? 0, Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>
    /// §27 client visuals per frame (SceneComponent.Update), heavy work only on a new tick or a view / LOD change, so a paused
    /// game still shows a ship that just came into view correctly. Reads sim state only.
    /// </summary>
    public override void Update(SceneRoot root)
    {
        Ship? ship = Ship;
        SimRoot? sim = Sim;
        if (ship == null || sim == null || _world == null || ship.Renderer is not { } renderer)
        {
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        bool worked = false;
        try
        {
            float tiles = ViewTiles(ship, sim);
            if (tiles == float.PositiveInfinity)
            {
                _shown = false; // not drawn (ShipRenderer culls per ship): quads keep their last state
            }
            else
            {
                bool coarse = BeltRenderState.Coarse(_coarse, tiles);
                if (!_shown || coarse != _coarse || sim.Tick != _renderedTick || _visualsDirty)
                {
                    worked = true;
                    Render(ship, sim, renderer, coarse, rebase: !_shown || coarse != _coarse);
                    _shown = true;
                    _coarse = coarse;
                    _renderedTick = sim.Tick;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("render", ex);
        }
        Probes.OnRender(Stopwatch.GetTimestamp() - t0, worked, _shown, _coarse, _live, _shownSurfaces, _shownLamps);
    }

    /// <summary>
    /// §27 ship tiles across the most detailed view showing this ship (main view incl. its near margin, miniview), or
    /// infinity when neither does. Client-only (camera), never feeds the sim.
    /// </summary>
    private static float ViewTiles(Ship ship, SimRoot sim)
    {
        float tiles = float.PositiveInfinity, scale = ship.WorldUniformScale;
        if (ship.IsNearOnScreen)
        {
            tiles = sim.WorldViewRect.Size.Y / scale;
        }
        if (sim.WorldMiniviewRect is { } m && m.IntersectsWith(ship.WorldBoundingCircle))
        {
            tiles = Math.Min(tiles, m.Size.Y / scale);
        }
        return tiles;
    }

    /// <summary>§23 setting of node i from its UIToggle (synced sim state on every peer).</summary>
    private int Setting(int i, ID<PartToggleGuiRules> toggle, int choices) => NodeState.Setting(PartUIToggle.GetOnPart(_nodeParts[i], toggle)?.Mode, choices);

    private void Rebuild(Ship ship)
    {
        _filterToggle = new ID<PartToggleGuiRules>(BeltState.FilterToggleId);
        _inputPriority = new ID<PartToggleGuiRules>(BeltState.InputPriorityToggleId);
        _outputPriority = new ID<PartToggleGuiRules>(BeltState.OutputPriorityToggleId);
        _splitterFilter = new ID<PartToggleGuiRules>(BeltState.SplitterFilterToggleId);
        _sourcePriority = new ID<PartToggleGuiRules>(BeltState.SourcePriorityToggleId);
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
        AtlasSprite[][]? surf = _surfSprites = Sprites(ship, SurfaceSpritesComponent, (int)Variant.CableHeatCrossing + 1);
        AtlasSprite[][]? lamps = Sprites(ship, LampSpritesComponent, 3);
        AtlasSprite[][]? edges = _edgeSprites = Sprites(ship, EdgeSpritesComponent, BeltRenderState.EdgeSprites);
        var surfLayer = new ID<ShipRenderLayerRules>(SurfaceLayer);
        var lampLayer = new ID<ShipRenderLayerRules>(LampLayer);
        for (int i = 0; i < _nodes.Count; i++)
        {
            Node n = _nodes[i];
            Part p = _nodeParts[i];
            var center = (Vector2)p.Location + (Vector2)p.Size / 2f;
            if (n.Kind == NodeKind.Belt && surf != null)
            {
                var (v, flip) = BeltRenderState.Select(n, n.NoCurve ? CrossingVariant(ship, p) : Variant.Straight);
                _surfV[i] = v;
                // Created still (speed 0 = frozen on the global-phase frame); Render starts it once its lanes move (§22).
                _surfL[i] = BeltRenderState.DamageLevel(p.DamageFraction, surf[(int)v].Length);
                _surf[i] = new ManagedShipQuad(renderer.GetLayerQuads(surfLayer), surf[(int)v][_surfL[i]], 0, flip ^ p.FlipX, center, Vector2.Zero, p.Direction,
                    ship.WorldUniformScale, Color.White, ship.Rules.RenderLayers[surfLayer].Inflate, 0f, sim.Clock);
            }
            for (int e = 0; edges != null && e < BeltRenderState.EdgeCount(n); e++)
            {
                // §28: the look follows the topology of this epoch (neighbours change only through a rebuild).
                var (x, y, ox, oy) = BeltRenderState.Edge(n, e);
                var cell = new IntVector2(x + ox, y + oy);
                int j = NodeAt(ship, cell.X, cell.Y), s = BeltRenderState.EdgeSprite(n.Kind, e,
                    BeltRenderState.Look(n, e, j >= 0 ? _nodes[j] : null, ship.Parts[cell] != null), p.FlipX);
                if (s >= 0)
                {
                    int k = MaxEdges * i + e;
                    _edgeS[k] = s;
                    _edgeL[k] = BeltRenderState.DamageLevel(p.DamageFraction, edges[s].Length);
                    _edges[k] = new ManagedShipQuad(renderer.GetLayerQuads(surfLayer), edges[s][_edgeL[k]], 0, false, center, Vector2.Zero, p.Direction,
                        ship.WorldUniformScale, Color.White, ship.Rules.RenderLayers[surfLayer].Inflate, 0f, sim.Clock);
                }
            }
            if (n.Kind != NodeKind.Belt && lamps != null)
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
        _surfV = new Variant[_nodes.Count];
        _surfL = new int[_nodes.Count];
        _lamps = new ManagedShipQuad?[2 * _nodes.Count];
        for (int i = 0; i < _edges.Length; i++)
        {
            if (_edges[i] is { } q)
            {
                q.Sprite = null;
            }
        }
        _edges = new ManagedShipQuad?[MaxEdges * _nodes.Count];
        _edgeS = new int[_edges.Length];
        _edgeL = new int[_edges.Length];
    }

    /// <summary>
    /// §25 crossing art: what each Up/Down edge links to through any chain of crossings (a crossing links both networks
    /// to a neighbouring crossing, so only the part past the chain tells). Render-only read of port connections.
    /// </summary>
    private static Variant CrossingVariant(Ship ship, Part p)
    {
        IntVector2 c = Cell(p, 0, 0);
        return BeltRenderState.CrossingVariant(Link(ship, p, Cell(p, 0, -1) - c), Link(ship, p, Cell(p, 0, 1) - c));
    }

    private static IntVector2 Cell(Part p, int x, int y) => p.Rules.GetShipRelativeCell(new IntVector2(x, y), p.Location, p.Rotation, p.FlipX);

    private static CrossLink Link(Ship ship, Part p, IntVector2 d)
    {
        // A straight chain of crossings ends within the ship; the bound only guards a malformed grid.
        for (int hop = 0; hop < 1024; hop++)
        {
            IntVector2 c = Cell(p, 0, 0);
            string edge = Cell(p, 0, -1) - c == d ? "Up" : Cell(p, 0, 1) - c == d ? "Down" : "";
            bool up = edge == "Up";
            bool heat = edge.Length > 0 && Linked(p, up ? PortUp : PortDown), cable = edge.Length > 0 && Linked(p, up ? CablePortUp : CablePortDown);
            if (!heat && !cable)
            {
                return CrossLink.None;
            }
            if (ship.Parts[c + d] is not { } next || !BeltState.IsCrossing(next))
            {
                return heat ? CrossLink.Heat : CrossLink.Cable;
            }
            p = next;
        }
        return CrossLink.None;
    }

    /// <summary>§27 port component IDs built once (string concat + ID per crossing per tick allocated before).</summary>
    private static readonly ID<PartComponentRules> PortUp = new("Port_Up"), PortDown = new("Port_Down"),
        CablePortUp = new("ElectricPort_Up"), CablePortDown = new("ElectricPort_Down");

    private static bool Linked(Part p, ID<PartComponentRules> port) => p.TryGetComponent(port, out IComponentToggleProvider? t) && t.IsToggleOn;

    /// <summary>
    /// Client visuals only (reads sim state, never writes it): surfaces, lamps, one pooled quad per item (§11, §21). §27
    /// coarse LOD: items freed, surfaces frozen, lamps off, nothing per tick; rebase = first frame after a gap in updates.
    /// </summary>
    private void Render(Ship ship, SimRoot sim, ShipRenderer renderer, bool coarse, bool rebase)
    {
        if (_visualsDirty)
        {
            _visualsDirty = false;
            BuildVisuals(ship, renderer, sim); // surfaces created still, lamps unlit (item quads are not touched)
        }
        // §28 edge quads stay at every LOD (they shape the joint); only their damage level follows the part, as surfaces.
        for (int k = 0; _edgeSprites != null && k < _edges.Length; k++)
        {
            if (_edges[k] is { } eq)
            {
                AtlasSprite[] levels = _edgeSprites[_edgeS[k]];
                int level = BeltRenderState.DamageLevel(_nodeParts[k / MaxEdges].DamageFraction, levels.Length);
                if (level != _edgeL[k])
                {
                    _edgeL[k] = level;
                    eq.Sprite = levels[level];
                }
            }
        }
        if (coarse && rebase)
        {
            HideQuads(0);
            foreach (ManagedShipQuad? q in _surf)
            {
                q?.UpdateAnimationSpeedFactor(0f, sim.Clock);
            }
            foreach (ManagedShipQuad? q in _lamps)
            {
                if (q != null)
                {
                    q.Color = Color.TransparentWhite;
                }
            }
        }
        if (coarse)
        {
            _shownSurfaces = _shownLamps = 0;
            return;
        }
        if (rebase)
        {
            _render.Rebase();
        }
        _render.Update(sim.Tick);
        _shownSurfaces = _shownLamps = 0;
        for (int i = 0; i < _surf.Length; i++)
        {
            // §25: a crossing follows its links (ports connect after the topology rebuild, neighbours change); §26 every
            // surface follows its part's damage level like vanilla sprites (PartSpriteRules.GetAtlasSpriteForDamage).
            if (_surf[i] is { } c && _surfSprites != null)
            {
                _shownSurfaces++;
                Variant v = _nodes[i].NoCurve ? CrossingVariant(ship, _nodeParts[i]) : _surfV[i];
                int level = BeltRenderState.DamageLevel(_nodeParts[i].DamageFraction, _surfSprites[(int)v].Length);
                if (v != _surfV[i] || level != _surfL[i])
                {
                    _surfV[i] = v;
                    _surfL[i] = level;
                    // Same interval and frame count: keep the quad's phase (SetSprite re-phases a paused quad, Codex).
                    c.Sprite = _surfSprites[(int)v][level];
                }
            }
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
                _shownLamps++;
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
                            ManagedShipQuad q = _quads[used];
                            q.Color = Color.White; // a resident surplus quad was transparent (no-op when already white)
                            q.SetSprite(sprite, sim.Clock);
                            q.Location = loc;
                        }
                        used++;
                    }
                }
            }
        }
        _resident = Math.Max(_resident, used);
        HideQuads(used);
    }

    /// <summary>§27 transparent surplus item quads kept resident per ship, so a small count change neither frees nor creates atlas quads.</summary>
    private const int PoolSlack = 64;

    /// <summary>
    /// Hides item quads from <paramref name="from"/>: up to <see cref="PoolSlack"/> stay resident transparent, the rest are
    /// freed (from 0: all freed, LOD / rebuild / detach).
    /// </summary>
    private void HideQuads(int from)
    {
        int keep = from == 0 ? 0 : from + PoolSlack;
        for (int i = from; i < _resident; i++)
        {
            if (i < keep)
            {
                _quads[i].Color = Color.TransparentWhite;
            }
            else
            {
                _quads[i].Sprite = null;
            }
        }
        _resident = Math.Min(_resident, keep);
        _live = from;
    }
}
