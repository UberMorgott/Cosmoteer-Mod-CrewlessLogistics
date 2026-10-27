# Belts design (Factorio-style logistics) — spec, v1 implemented (§18)

Status: v1 implemented on branch `feat/ammo-belts`, in-game verification pending. Game 0.30.5. Engine refs are to `E:\DEV\cosmoteer\Decompiled`
(`D:` = vanilla `D:\Steam\steamapps\common\Cosmoteer\Data\ships\terran`). Codex review folded in at the end.

## 0. Scope (owner decisions, latest wins)

- Whole mod becomes loader-dependent: Yet Another Mod Loader (Workshop 3577650065, Harmony, entry
  `public static void GameLoadInitializer()`), `Dependencies = ["3577650065"]` in `mod/mod.rules`.
- Belts carry **any** resource (sulfur, iron, ammo, missile parts, ...) and reach **any** part, incl. cargo holds.
- Parts: one auto-curving belt, two lanes, sideloading, 1x2 splitter (split + merge), one directional **connector**.
- Dropped: underground belts, inserters/manipulators, separate extractor/loader, ammo-only lane types.
- Energy network stays as is (rules); C# candidates only listed (section 13).

## 1. Architecture: (A) C# belt sim vs (B) rules lanes + C# extensions

Rules-only facts that bound (B):

- A `ResourceStorage` holds one `ResourceType` (`ResourceStorage.cs:69`, single type field via BaseResourceStorage);
  a lane slot per type is one storage + converter + gates. Current prototype = ~8 components per type per tile
  (`mod/ammo_belt_common.rules:319-338`). 26 resource dirs in vanilla `Data/resources`; ~20 carriable.
  2 lanes x 20 types x ~8 = ~320 components/tile (estimate, not an engine limit; rejected as impractical).
  (B) only works for a short type list and still needs C# for mixed FIFO and motion.
- A rules proxy binds to the part covering one cell, first matching criteria wins
  (`ProxyHandler.cs:157-163,196-212`), criteria see only ID/category/Rot/FlipX/rect (`RelativePartCriteria.cs:102-157`),
  never a neighbour's state. Lane/curve decisions need extra ToggleProxy chains per neighbour.
- Cargo holds are `FlexResourceGrid : BaseResourceConsumer` (`FlexResourceGrid.cs:32`), not an `IResourceStorage`, so
  `ResourceStorageProxy` (`ResourceStorageProxy.cs:15`, `ProxyHandler<IResourceStorage>`) cannot reach them.
- No FIFO across types, no sub-tile position, no smooth motion.

(A) feasibility, grounded:

- Vanilla already runs per-ship deterministic logic as a C# `ShipComponent` with `FixedUpdate`: the
  `ResourceConverterManager` (`ResourceConverter.cs:26-97`), created lazily per ship and added via
  `Sim.EnqueueDeterministic` (`ResourceConverter.cs:477-490`). A mod `BeltManager : ShipComponent` copies the attach
  pattern, but NOT its bucket: `-24 ConvertResources` runs ships in parallel (`SimRoot.cs:605-618`). BeltManager uses
  `FixedUpdateBucket = -11` (`Commands`, `FixedUpdateBuckets.cs`), sequential, after converters (-24), resources (-17),
  jobs/crew (-15..-12). `ShipComponent` is `internal abstract` (`ShipComponent.cs:14`): build against publicized
  assemblies + `IgnoresAccessChecksTo` like MiningDrone (`MiningDrone.csproj:45`, `src/AssemblyInfo.cs:6`).
- Sim is lockstep: inputs at `InputTicksPerSecond` (`BaseMPManager.cs:589,599`), every client runs the same `FixedUpdate`
  (`SimRoot.cs:1394-1396`, `Tick++`), desync detection hashes sim state (`SimRoot.cs:741-751` -> `Ship.cs:1294`).
  Deterministic C# inside FixedUpdate is MP-safe if all peers run the same DLL (SharedShip precedent:
  `SharedShip/README.md:27-28`, fail-closed rollback `SharedShip/src/Patcher.cs:66-76`).
- Vanilla crew transfer API is reachable from C#: `IResourceSource.SubtractResources(int)` (`IResourceSource.cs:234`),
  `IResourceSink.GetRemainingCapacity(type, ...)` / `ReceiveResources(type, amount, isCrewDelivery, sticky)`
  (`IResourceSink.cs:175,192`). Grid tiles of factories/weapons/cargo implement both
  (`TypedResourceGrid.cs:38`, `FlexResourceGrid.cs:37`). Cargo tile direct API: `ResourceTile.SetResources` (`FlexResourceGrid.cs:341`).

**Recommendation: (A) full C# belt sim**, rules only declare parts (ID, sprites, cost, health, UI toggles).
Evidence: (B) cannot carry "any resource" (component explosion) nor reach cargo; (A) uses a vanilla-proven
per-ship deterministic pattern and the crew source/sink API. Cost: hard loader dependency, Harmony patches to maintain
per game version, MP needs identical DLL.

Design rule that keeps (A) safe without the loader: belt part rules use **only vanilla component types**
(Graphics, BlueprintSprite, UIToggle, base hull). No custom rules `Type =`: HalflingCore
`BaseSerializer.GetAllPolymorphicSubclasses` would find a mod subclass (Codex, decompiled HalflingCore.dll), but
rules naming it fail to parse without the DLL. Without the DLL the rules load, belts are inert floor, saves readable.

## 2. Parts list

| ID (new, belts never shipped in a release) | Size | Role |
| --- | --- | --- |
| `morgott.crewless_logistics.belt` | 1x1 | belt, auto straight/curve, 2 lanes |
| `morgott.crewless_logistics.belt_splitter` | 1x2 | 2 in / 2 out, split + merge, lane-preserving |
| `morgott.crewless_logistics.belt_connector` | 1x1 | directional room<->belt/room transfer, resource filter |
| `morgott.crewless_logistics.belt_heat_crossing` | 1x1 | straight belt over a vanilla heat pipe (§19) |
| `morgott.crewless_logistics.belt_cable_crossing` | 1x1 | straight belt over our electric cable (§19) |

Retired prototype IDs (`ammo_belt`, `ammo_belt_corner_left/right`, `ammo_belt_merger`, `ammo_belt_splitter`,
`mod/ammo_belt_common.rules:10-17`) were never in a Workshop release (0.3.5 description says logistics "planned"), so
removal only affects branch test saves: deleted (done; `mod/ammo_belt*.rules` line refs in this spec = files at c029386), plus the 6 `belt_*` filter toggles (`mod/ammo_belt_toggles.rules`,
`mod/mod.rules` AddMany). Base stats reuse current `BasePart` (heat-pipe stats, crew blocked, rotatable,
`mod/ammo_belt_common.rules:430-441`). Belt and connector `IsFlippable = false` (output is always front; mirroring
is handled by rotation, no flip state needed). Splitter `IsFlippable = false` (symmetric).

## 3. Lane model (C#)

- Per belt tile: lanes L, R (L = left of travel direction). Each lane = ordered slots, cap `K = 4` items/lane/tile.
- Item = `(ResourceType id, int pos)`; `pos` integer 0..255 along the tile (fixed point, no float -> determinism).
- v1 = per-tile state (Codex: segments/sleep premature). Min gap 64 (= 256/K). ~~Staged transfer per tick: compute all
  moves from the previous-tick snapshot, then apply~~ **Changed by P3 (2026-10-27):** a previous-tick snapshot stalls
  every follower in a packed line (`new <= ahead.old - 64 = own pos`), halving compressed throughput. Instead each tile
  is moved exactly once per tick in a **downstream-first order** precomputed at topology rebuild (belt ends first by
  ascending index, then one root per cycle; feeders rear, left, right = §5 priority). An item entering an
  already-moved tile never moves twice; overshoot carries across tile boundaries. Accepted limit: a 100% packed closed
  loop freezes (cycle root sees its successor's old tail). Connector put = right behind the tail (`TryPutBack`, back
  quarter of the tile); connector take = drain-on-cross at a belt end (`OpenDrain`/`TryTakeDrained`) keeping
  overshoot — taking a head parked at 255 caps a lane at 7.5/s instead of 8/s (measured). `belts/src/LaneSim.cs`.
  Segments (Factorio transport lines) not needed: P3 cost is ~6-8 us/tick at 500 tiles.
- Speed: integer pos-units per sim tick + remainder accumulator (sim = 30 ticks/s, `Data/cosmoteer.rules:76`,
  `SimRoot.cs:1396`; 2 tiles/s = 512/30 is fractional). Tick-count driven, never wall time.
- Mixed types, strict FIFO per lane (the rules-model limitation disappears).
- Rebuild topology on `ship.Parts.PartAdded/PartRemoved` (`CommonBasePartsManager.cs:286,296`, used by
  `ShipPhysics.cs:194-195`): dirty flag only, rebuild inside FixedUpdate; iterate the ordered part list
  (`CommonBasePartsManager.cs:397`), never hash-set order.
- Lane contents live on the Part (per-part state object), not on the manager, so they move with the part.
- `PartRemoved` != destroyed: on ship split the same Part moves to the new ship (`StructureContiguityManager.cs:134-142`,
  `IShipSplitHandler.OnPartSplitting`), and `part.Ship` may already be null in the event
  (`CommonBasePartsManager.cs:2097`). Split -> keep contents; real destroy/remove -> drop nuggets like
  `DropResourcesWhenDestroyed/Removed` (`ResourceStorage.cs:193-222`), capturing ship/world rect before detach.

## 4. Auto-curve logic

Belt output = its front cell, always. Input classification from neighbours that point **into** this belt
(neighbour is a belt/splitter-output whose front cell is this belt's cell):

- `rear` = feeder at back, `left` / `right` = feeders at sides.
- Curve iff exactly one side feeder and no rear feeder (Factorio rule). Curve lanes: L->L, R->R (inner stays inner).
- Otherwise straight; side feeders become sideloads (section 5).
- State is derived each topology rebuild, never saved. Sprite: C# selects curve-left / curve-right / straight art
  per tile (ship-layer quads, section 11); rules fallback without DLL = straight arrow (`mod/ammo_belt_common.rules:261-276`).
- Rotation in Cosmoteer = remove + add part, so it fires the same rebuild.

## 5. Sideloading

- A feeder pointing into the side of a **straight** belt (has rear feeder, or two side feeders) puts all items onto
  the **near** lane at the entry position (pos 128 of that tile); feeder from the left -> target L, right -> R.
- A feeder into a curve keeps its lanes (section 4).
- Feeder into the back or outer side of a splitter half / any side but the front of a connector: see 6/7/7a.
- Merge priority when two inputs compete for one lane gap: rear first, then alternate sides by `Tick` parity
  (deterministic, no Rand needed).

## 6. Splitter 1x2

- Two input cells (back of each half), two output cells (front of each half). Lane-preserving: L stays L, R stays R.
- Per lane, items from both inputs alternate between both outputs (round-robin counter per lane, saved).
  Blocked output -> all goes to the free one. One input only -> even split. One output connected -> merge.
- Priority / filter: §23 (Factorio splitter settings).
- Replaces old merger + 3-way splitter.

## 7. Connector (replaces extractor, loader, inserter, underground)

Directional 1x1, arrow back -> front. Behaviour from what occupies the back cell and the front cell
(`Ship.Parts[cell]` returns the part covering that cell, any cell of a multi-cell part: same lookup as
`ProxyHandler.cs:157-158`):

| back | front | behaviour |
| --- | --- | --- |
| part (room) | belt | extractor: pull filtered type from part sources, put on belt |
| belt | part (room) | loader: take filtered items from belt end, insert into part sinks |
| part | part | direct room-to-room transfer |
| belt | belt | pass-through belt tile with filter (non-matching items block) |
| belt / splitter / connector (back or side) | belt / splitter / connector / room | handoff chain (§7a) |
| empty / other | any | idle |

- Belt end condition: a belt pointing into the connector's back or side feeds it; front part must not point back
  into it (§7a).
- Put onto belt: connector at the belt's back -> alternate L/R; connector at the belt's side -> **far** lane
  (owner rule from inserter). Take from belt: both lanes, head items only.
- Sources/sinks = the part's vanilla `IResourceSource` / `IResourceSink` objects (crew transfer API, section 1). This
  automatically respects inputs vs outputs (factory output grids are sources, weapon ammo grids are sinks, cargo is
  both), so no per-part whitelist or output-cell criteria (old `mod/ammo_belt_common.rules:39-55` disappears).
  Enumeration: live `ship.Resources.Sources/Sinks` (`ResourceManager.cs:500-506`) filtered by `Part`, copied into a
  reusable buffer before any mutation. **No cache**: cargo (un)registers tile endpoints as contents change
  (`FlexResourceGrid.cs:368`), launcher swaps components on mode change (`PartToggledComponents.cs:109-136`).
- Eligibility gates: `IResourceSource.SuppliesResources` (`IResourceSource.cs:76`), `IResourceSink.CanReceive`
  (`IResourceSink.cs:137`), `GetResourceType(MPValueType.Confirmed)` == item type, capacity minus anticipated
  deliveries/pickups like vanilla (`ResourceManager.cs:1754,1815`). `TypedResourceGrid.ReceiveResources` does not
  check type (`TypedResourceGrid.cs:587`) and cargo `SetResources` can overwrite another type
  (`FlexResourceGrid.cs:348`) -> check type + capacity right before the call and use the **returned** quantity; an
  item not accepted stays on the belt. Direct calls outside crew jobs are a vanilla pattern (`ResourceManager.cs:717`).
- Sink choice: highest `GetSortPriority(false, Confirmed)`, then remaining capacity; `ReceiveResources(type, n,
  isCrewDelivery:false, sticky:false)`. Crew keep working in parallel; belts do not reserve.
- Rate: fixed, tick-based, configurable constant (1 item / `T` ticks, Q3); `BaseResourceStorage` subtract path
  bypasses crew `PickUpRate` (`BaseResourceStorage.cs:680`), so the connector rate is the only limit.
- Filter: rules `UIToggle`, `Style = Popout` (`PartToggleGuiStyle.cs` Popout), one choice per resource type + "any".
  No choice-count cap (`PartPopoutToggleButton.cs:79`), but layout is window-bounded without scrolling: check ~20
  choices on a small screen. `RememberValue = true` stores it in `Ship.Metadata.PartUIToggleStates` keyed by
  (PartSaveData, ToggleID) (`PartUIToggle.cs:62-71,234-236`); export/import with ship designs confirmed
  (`Ship.cs:1540,3198`). C# reads the toggle value; no custom state, no blueprint payload.
- **P2 static finding (2026-10-27): "all sinks of the part" is too wide.** Besides storages, a part can own
  `PartConstructionSink` / `PartRepairSink` (`IsConstructionSink = true`, `QueuedPartConstruction.cs:85`,
  `QueuedPartRepair.cs:82`), `StatusResourceSink` (`StatusResourceSink.cs:27`) and `Airlock` (`Airlock.cs:20`, type
  `default`). Proposed gate: only `BaseResourceStorage` subclasses and the `ResourceTile`s of `TypedResourceGrid` /
  `FlexResourceGrid` (`BaseResourceStorage.cs:30`, `TypedResourceGrid.cs:38`, `FlexResourceGrid.cs:37`), never
  `IsConstructionSink`. Crewless Logistics network storages are `BaseResourceStorage` too: exclude
  `BasePartNetworkResourceStorage` subclasses so belts never touch batteries in the energy network.
- **P5 static finding:** `ReceiveResources(isCrewDelivery:false)` does not touch anticipated delivery (job-owned:
  `ResourceTransferJob.AnticipatedDelivery` setter `:362-376`, reversed by `UpdateDeltas(-1)` `:1569-1573`); a crew
  arriving at a sink the belt already filled keeps the excess in hand (`DeliverResources` `:1458-1463`, uses returned
  qty). Difference vs crew: `CrewResourcesReceived` trigger is not raised (`BaseResourceStorage.cs:819`) — rules that
  react to crew deliveries do not fire for belt inserts.
- Missile launcher modes: no special case; launcher's mode-specific storages appear/disappear via ToggledComponents
  (`D:\missile_launcher\missile_launcher.rules:129-199`), sinks of the wrong type report 0 capacity for the filter type.

## 7a. Connectivity: one handoff for every adjacency (2026-10-27, owner: any combination must work)

- Every output (belt end, splitter half front, connector front) looks at its front cell with one rule (`SinkAt`):
  belt / splitter half / connector there = its input, entered from its back, left or right; empty cell = room
  (connector fronts only). Entry from the target's **front** (two outputs facing each other) = defined no-op.
- Belt -> belt / splitter half: a LaneSim move (downstream-first order, §3): back = lanes kept, side of a straight
  tile = near lane at pos 128 (§5), lone side feeder = curve (§4). Splitter halves accept side entry too (only the outer
  side exists; the inner side is the other half).
- Everything else is one handoff `Offer(sink, lane, item)`: lane tile = splitter-style put (back: same lane at the
  tail, side: near lane at 128); connector = gate (filter, `Ready` = cooldown 0 at tick start) forwarding to its own
  front; room = `Put`. Connector chains are walked to their end: filters intersect (any ∩ x = x, x ∩ y≠x = blocked),
  every connector passed gets the 1-per-`ConnectorPeriod` cooldown, the end lands with connector rules (back:
  preferred lane then the other, side: far lane). A belt end into a connector opens its drains with the chain filter.
- Drivers (node order, after the LaneSim tick): belt end into a connector (alternating lanes), splitter (§6
  round-robin per lane over both half sinks; a refused output yields to the other), connector with a room behind it
  (reserve the end slot / check the end room, then take one unit). Plain connectors are passive gates.
- Defined no-ops (no crash, no loss): outputs facing each other (belt/splitter/connector into the front of a part
  whose front faces back), closed connector loops (cut at the loop's first connector in part order), chains that end
  in nothing or in the room they started from, filter none anywhere on the chain.
- Tested exhaustively (`WorldProbes.AdjacencyMatrix`): source {belt, curve L/R, connector any/iron/none, splitter half
  A/B} x target {belt, connector any/iron/none, splitter half A/B} x 4 rotations x entry {back, left, right, front}.

## 8. Endpoints (examples, all via sources/sinks — no whitelist)

| part | component (vanilla) | as source | as sink |
| --- | --- | --- | --- |
| storage_2x2..4x4 (cargo) | `Storage` FlexResourceGrid (`D:\storage_2x2\storage_2x2.rules:37-39`) | yes | yes |
| factory_ammo | `AmmoStorage` TypedResourceGrid bullet (`factory_ammo.rules:~117-119`) | bullets | sulfur input |
| factory_he/emp/nuke/thermal | `MissileStorage` TypedResourceGrid (`factory_he.rules:152`, `factory_emp.rules:163`, `factory_nuke.rules:159`, `factory_thermal.rules:149`) | missile parts | inputs |
| factory_mine | `MineStorage` TypedResourceGrid mine_part (`factory_mine.rules:149`) | mine parts | inputs |
| cannon_med | `AmmoStorage` ResourceStorage 15 (`cannon_med.rules:150`) | no | bullets |
| cannon_large, cannon_deck, flak_cannon_large, railgun_loader | `AmmoStorage` TypedResourceGrid | no | bullets |
| chaingun | `CombinedAmmoStorage` (`chaingun.rules:345`) | no | bullets |
| missile_launcher | per mode: HE/thermal `CombinedMissileStorage`, EMP/nuke `MissileStorage`, mine `MineStorage` | no | mode type |

Exact source/sink classes per row are verified in probe P2 (table lists storage components, not yet the sink objects).
Example chains: cargo -> connector(sulfur) -> belt -> connector -> factory_ammo; factory_ammo -> connector(bullet)
-> belt -> splitter -> connectors -> cannons.

## 9. Perf budget

- Rules side: ~4 vanilla components per belt tile (graphics, blueprint, base hull set); filter toggle on connector.
  vs current prototype ~20 components per tile per 2 types. Big net win.
- C# side per ship per tick: O(belt tiles + items + connectors), staged. No sleep in v1 (no consumption events
  from weapons/factories to wake on; connectors poll at their fixed rate). Add sleep/segments only if P3 needs it.
- Target: 500 belt tiles, 2000 items, < 0.2 ms/tick on one ship (measure in P3). No allocations in FixedUpdate.
- §27: 20 ships x 2080 tiles measured at ~0.4 ms/tick for the fleet; sleep still not needed; render is view / LOD gated.

## 10. Save / blueprint / MP

- Belt contents: Harmony postfix on explicit `IGenericSerializable.WriteTo` of Part (`Part.cs:1371-1397`) writes a
  versioned `Components/morgott.belt` subtree; postfix on the load constructor `Part(GenericSerialReader)`
  (`Part.cs:827-851`) reads it into the Part's state object, held until the part is attached (array deserialization
  precedes `AddPart`, `PartsManager.cs:223`). Vanilla ignores unknown paths -> loads without the DLL; **resaving
  without the DLL drops belt contents** (parts intact).
- Payload = everything future-affecting: lane items (type, pos), speed remainder, splitter round-robin counters,
  connector cooldown and L/R alternation. Manager itself is not serialized (`Ship.cs:2194`); it rebuilds from parts
  before its first tick.
- Later alternative (not v1): inject a `HasSaveState` component at runtime with programmatic rules (vanilla does this:
  `PartRepairTracker.cs:36`; save keyed by `SerialID`, `Part.cs:845,1389`) — no rules `Type` needed.
- MP: resync/rejoin use normal `SaveTo` + `GameRoot.Load` (`GameResyncFlow.cs:61,206`), MP save load
  `LoadGameInit.GameData` (`LoadGameInit.cs:48`) -> same Part paths; verify load, resync, rejoin separately (P4).
- Integrity: postfix on `Ship.GetIntegrityHash` (`Ship.cs:1294`) adds every payload field + a protocol version.
  Detects divergence only; it is not a pre-join compatibility check (mod version match does that).
- Determinism rules: integer positions, Tick-based timing, no Dictionary/HashSet iteration order, no wall clock, no
  `System.Random` (use ship `Rand` if randomness is ever needed), no per-client state in sim (overlay is client-only).
- Blueprints: only the connector filter (UIToggle metadata, section 7); belt contents are not blueprinted.
- Version guard: like SharedShip, verify all patches applied, else disable the belt sim and log loudly.

## 11. Visuals

- Rules static: belt floor + arrow, darker than corridor; works without DLL.
- C# client-side drawing of animated belt surface per tile (straight/curve L/curve R from section 4) and items at
  interpolated positions (`pos` + per-tick speed, lerp by frame fraction).
- Hook: native ship-layer quads, `ManagedShipQuad` on `ship.Renderer.GetLayerQuads(layer)` — how cargo draws its
  contents (`FlexResourceGrid.cs:452`). Renders under crew, in miniviews, with ship-level culling
  (`ShipRenderer.cs:225,997`). Rejected: `sim.Overlays.DrawingCustomOverlays` (MiningDrone `src/MinerManager.cs:250`)
  is gated on GUI visibility and skips miniviews (`SimOverlayRenderer.cs:1520-1524,1559`); fine for debug only.
- Sprites must be in the layer's atlas (custom render layer like `mod/cable_material.rules` ThermalLayer, or doodad
  layers). Shader rule from MiningDrone (`src/Icons.cs:161-165`): vertex input must match primitive.
- Rendering only reads part state, never mutates sim. Cost: one quad per tile + per item, pooled; per-item culling
  is ours (ship culling is coarse). `ResourceSprites` rejected: it picks art by quantity, not position.

Sprites needed (Codex image gen later; 64 px/tile like vanilla conveyor):

- belt straight, 8 animation frames, direction arrows, darker than corridor floor (vanilla conveyor anim is 8 frames,
  `mod/ammo_belt_common.rules:281-297`).
- belt curve left, 8 frames (curve right = UV mirror at draw time).
- splitter 1x2, 8 frames.
- connector: base + arrow, 1 static frame + 1 "active" frame.
- blueprint sprites: belt, splitter, connector (1 each). Editor icons: 3.
- Item icons: reuse vanilla `Data/resources/*/icon.png` / `*1.png`.

## 12. Migration (0.3.x subscribers)

- 0.4.0 adds `Dependencies = ["3577650065"]`. Users must install the loader manually (`Installer.bat`); Workshop
  description EN/RU top line: "Requires Yet Another Mod Loader (3577650065) installed via its Installer.bat".
  Replace "Rules-only, no DLL or loader" in `mod/mod.rules` Description.
- Saves: energy parts/IDs unchanged -> 0.3.x saves load. Belt IDs are new.
- Without loader installed: rules still load (no custom Types), energy works, belts inert. DLL logs a version warning.
- MP: every peer needs same mod + loader + DLL version.

## 13. Energy network: C# candidates (not redesigned here)

Candidates where C# removes rule crutches: network reachability instead of per-part proxy chains, reactor-first /
capacitor-last priority as code, one manager per ship instead of per-device converters, hyperdrive gate. Decide
after belts ship.

## 14. Probes before implementation (rules + DLL test ships)

- P1: Harmony targets apply on 0.30.5 (Part load ctor, explicit WriteTo, Ship.GetIntegrityHash); BeltManager runs in
  bucket -11 once per tick per ship.
- P2: list `IResourceSource`/`IResourceSink` per endpoint part in section 8 (part debugger / log).
- P3: sim tick rate + BeltManager cost at 500 tiles.
- P4: MP 2 peers: build, run, save, rejoin; integrity hash stable.
- P5: `ReceiveResources` into weapons/factories outside crew path does not break anticipated-delivery bookkeeping
  (`IResourceSink.UpdateAnticipatedDelivery`, `IResourceSink.cs:197`).

### 14.1 Probe results (2026-10-27, code in `belts/`)

Headless: `dotnet test` (xUnit project `belts\tests\CrewlessLogistics.Belts.Tests.csproj`, in `CrewlessLogistics.sln`;
shared .NET 10 runtime + real 0.30.5 `Cosmoteer.dll` from the game folder + loader `0Harmony.dll`; game-backed tests skip
without the game). The mod DLL targets net10.0 like the game; P1 also passed on the game's own 10.0.12 runtime. Live: test-only mod `belts\probe-mod` installed by `Install-BeltProbe.ps1` as
`Mods\CrewlessLogisticsBeltProbe` (separate ID `morgott.crewless_logistics.belt_probe`; energy package untouched).

| Probe | Result | Evidence |
| --- | --- | --- |
| P1 patch targets | headless PASS; in-game run pending | loader Harmony MVID dc2e7251 loads; 4 postfixes install + verify + roll back; 0 R2R inliners of the targets; `BeltManager` type loads, bucket -11. Loader already loads DLLs from Workshop folders (modloader log 2026-10-27 14:33: 3765559239, 3299057292, 3823252178) |
| P2 endpoints | static done (§7 finding); in-game dump pending | log lines `P2 part=... src[...] sink[...]` per distinct part signature |
| P3 cost | headless PASS; in-game tick rate pending | loop 500 tiles/2000 items 3.9-7.5 us/tick; comb 400+100 sideload feeders/4000 items ~6 us/tick; 0 B alloc; packed line 800/800 per 100 s per lane (8/s) |
| P4 MP | pending (owner, 2 peers) | probe saves a tick counter on every `cosmoteer.corridor` (`Components/morgott.belt`), hashes it + protocol version |
| P5 direct insert | static PASS (§7 finding); in-game pending | opt-in `p5_parts.txt`: 1 unit / 60 ticks, logs anticipated before/after |

Attach point (not named before): Harmony postfix on the private `Ship(ShipRules,string,int,ulong?,bool,ObjectID)`
ctor (`Ship.cs:1130`) that every ship ctor chains into (new, loaded, split) adds `BeltManager`; `ComponentsCreated` is
an instance event (`Ship.cs:1103`), so no static hook exists. Patch set = this + the three §10 targets, all-or-nothing.

Owner steps (single player first):

1. `& E:\DEV\cosmoteer\Energy\belts\probe-mod\Install-BeltProbe.ps1` (already done once). In game: Mods list -> enable
   "Crewless Logistics belt probe" -> trust its library -> restart.
2. P1/P3: load a creative game. `<profile>\Logs\CrewlessLogistics.Belts.log` must show
   `P1 patched 4 targets`, then every ~10 s `P1 tick=... calls/tick=N simShips=N duplicates=0 ... P3 rate=30.0 ticks/s`.
   FAIL: `ERR patch`, `DISABLED`, `duplicates>0`, calls/tick != simShips (culled/stasis ships may lower calls).
3. P2: build one ship with storage_2x2, factory_ammo, factory_he/emp/nuke/thermal, factory_mine, cannon_med,
   cannon_large, cannon_deck, flak_cannon_large, railgun_loader, chaingun, missile_launcher (switch through all modes,
   wait 3 s each). Log: one `P2 part=...` line per part and per launcher mode.
4. P4 save: build corridors, wait 10 s, save, quit to menu, load: no errors; disable the probe, load the same save:
   loads (vanilla ignores `Components/morgott.belt`). MP: 2 peers with probe, host the save, play 2 min, client
   rejoins: no desync message; game log has no integrity mismatch.
5. P5: `Install-BeltProbe.ps1 -P5Parts cosmoteer.cannon_med,cosmoteer.factory_ammo` (single player only), restart,
   fire the cannon while crew resupply: `P5 ... got=1 anticipated=a->a` (unchanged, never negative); crew keep
   delivering, no crew stuck carrying ammo. Reinstall without `-P5Parts` afterwards.

## 15. Implementation order

1. Loader skeleton: entry, version guard, BeltManager attach per ship (no-op), integrity hash postfix. P1, P3.
2. Rules parts: belt, splitter, connector with static art; remove prototype parts/toggles.
3. Lane sim: per-tile staged moves, auto-curve, sideload, split handling; save/load postfix. P4.
4. Splitter.
5. Connector + filter UIToggle; endpoints via sources/sinks. P2, P5.
6. Ship-layer quads: animated surface, sliding items.
7. Art from Codex image gen; strings EN/RU; Workshop description + migration notice.

## 16. Owner decisions (2026-10-27)

- Q1: one belt tier in v1, K=4 per lane, ~2 tiles/s. Faster tiers later if needed.
- Q2: open (default: heat-pipe cost 4 steel + 2 coil).
- Q3: connector needs no power; throughput constant still open (tune in probe).
- Q4: open (default: no splitter priority/filter in v1).
- Q5: new connector filter = none (passes nothing until a type is chosen).
- Q6 (owner, 2026-10-28): crew walk belts (straight/curved) any way at .7 (congested .35) and belt crossings along the
  belt at .7; connectors and splitters block crew (`CrewSpeedFactor` 0, like armor). The crew pathfinder already
  weighs speed: `PathManager.GetTravelCost` = mean of `1 / Part.GetCrewSpeedFactor` of both cells, 0 = impassable.

## 17. Decisions with Codex (2 rounds, `codex exec -s read-only`, static engine reading)

| # | Claude view | Codex view | Agreed + why |
| --- | --- | --- | --- |
| D1 architecture | (A) full C# sim, vanilla-only rules components | agree; 320-comp figure is an estimate | (A): (B) can't reach cargo (`FlexResourceGrid.cs:32`) nor do mixed FIFO |
| D2 MP sync | per-ship ShipComponent like ResourceConverterManager | agree, but -24 is parallel; use -11 `Commands` (sequential) | bucket -11, integer/Tick state, lockstep inputs already the transport, hash all state + protocol version |
| D3 persistence | Harmony on Part.ReadFrom/WriteTo | `ReadFrom` doesn't exist: patch load ctor `Part.cs:827` + explicit WriteTo `Part.cs:1371`; custom component also viable via runtime injection | per-part versioned payload v1; runtime-injected `HasSaveState` component later option |
| D4 connector | back/front occupant decides; Popout filter; sources/sinks | agree; no cache, Confirmed values, type/capacity checks, returned qty; Popout has no cap but no scroll | as section 7; fixed tick rate |
| D5 lanes | segments, K=4, sleep | per-tile first, staged moves, remainder accumulator, no sleep | per-tile v1 (simpler, Codex finding accepted) |
| D6 render | `DrawingCustomOverlays` | GUI-gated, no miniviews; use `ManagedShipQuad` ship layers | ship-layer quads; overlay debug only |

Review findings (round 1): accepted 1 (wrong patch target), 2 (parallel bucket), 3 (stale endpoint cache), 4 (type
checks + returned qty), 5 (Confirmed + anticipated), 6 (split != destroy), 7 (full versioned payload), 8 (hash is
not a compat check), 9 (resync/rejoin paths), 10 (publicizer), 11 (drop segments/sleep). Rejected: none.
Not verified by Claude beyond spot checks: HalflingCore `GetAllPolymorphicSubclasses`/`FixedUpdateForBucket` serial
loop (not in local decompile), `PartsManager.cs:223`, `Ship.cs:1540,3198,2194`, `GameResyncFlow.cs`.
Spot-checked: `Part.cs:827,1371`, `SimRoot.cs:605-618`, `FixedUpdateBuckets.cs` (-11 Commands),
`ResourceManager.cs:500-506`, `StructureContiguityManager.cs:134-142`, `SimOverlayRenderer.cs:1520-1524`,
`FlexResourceGrid.cs:452`, `cosmoteer.rules:76`.

## 18. v1 implementation (2026-10-27)

- One package: `CrewlessLogistics.Belts.dll` ships in `Mods\CrewlessLogistics` next to `mod.rules`
  (`Install-Mod.ps1` builds and hash-checks it, removes the test-only `Mods\CrewlessLogisticsBeltProbe`);
  `Dependencies = ["3577650065"]`, `ModifiesMultiplayer = true`. Energy rules unchanged.
- Parts (rules, vanilla components only): `belt.rules` (rotatable vanilla conveyor body, crew speed .7 every way (Q6),
  static belt floor art on `floors`), `belt_splitter.rules` (2x1), `belt_connector.rules` (+ UIToggle
  `BeltFilter`, Popout `belt_filter_toggle.rules`: none (default), any, 21 types). Build tabs: Storage and Power.
  Art: Codex image-gen masters in `tools/belt-sprites/art` (256 px/tile), baked to 64 px/tile + icons by
  `tools/belt-sprites` (Go, trim/downscale only). Curves: C# surface quad per `Shape` (§21; the old unwired static
  `belt_curve.png` was removed in §22).
  Splitter and connector are closed machine housings (§21 supersedes: closed boxes + small chevron); direction reads from the machinery (back = bulky intake with
  hazard lip + feeder rollers, front = slim ejector shutter + green light), no painted arrows. Animation-ready layers
  (art only, not baked/wired): `belt_{splitter,connector}_base.png` (housing, moving parts removed) and
  `belt_{splitter,connector}_moving.png` (rollers, diverter shuttle/piston, shutters, lights on alpha); the game
  uses the composed `belt_{splitter,connector}.png`.
- Core (pure, headless-tested): `LaneSim` (lanes on per-part `LaneTile`s), `BeltWorld` (topology §4-§7 + tick),
  `NodeState` (payload v2). Changes vs §3/§10: speed remainder is derived from `Sim.Tick`
  (`floor((t+1)*512/30) - floor(t*512/30)`), so no accumulator is saved; drains open for one tick only (held items
  are saved); carriable types = 21 vanilla stackable resources (`BeltTypes.Ids`, append-only); connector rate
  1 item / 2 ticks (Q3); every adjacency goes through one handoff (§7a), e.g. a connector in front of a
  splitter half is that output's gate (a mismatch yields to the other output).
- Glue: `BeltManager` rebuilds from `ship.Parts` order and `ship.Parts[cell]`; `PartRoom` = live
  `ResourceManager._sources/_sinks` of the part with the P2 gate; items drawn as `ManagedShipQuad` on `doodads_low`
  with each resource's `StackSprites[0]` (updated per tick, no frame interpolation); destroyed belts drop nuggets.
- Not in v1 (curve art + animated surface: done in §21/§22):
  items on deconstructed belts are lost (destroyed belts drop them), frame interpolation of item positions.

## 19. Belt crossings over heat pipes / cables (2026-10-27, owner: belt ON TOP of the pipes)

Decisions (Claude / Codex `codex exec -s read-only`, engine-verified / agreed):

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| D1 same-cell overlap (belt placed over an existing pipe) | infeasible | AGREE | one part per cell: `CommonBasePartsManager.cs:56` `Dictionary<IntVector2,TPart> _partsByCell`, `:1955` `_partsByCell.Add` throws on duplicates; blueprints also reject occupied cells (`BlueprintPartsManager.cs:154`); `PhysicalRect`/`CellOccupancyFactor`/`IsExternal` do not bypass it. => two 1x1 crossing parts |
| D2 body, crew | inherit vanilla `heat_pipe_crossing`: stats, Left/Right doors, crew .8; belt on the part Left/Right axis so crew walk along the belt | AGREE (keep thermal nodes `NetworkAverageThermalDissipationLoadValue`/`BlueprintNetworkNode`; cable variant needs both `CrewlessLogistics*Node`) | crew-walkable at .8 like vanilla crossing and `cable_crossing` (plain belt is 1.0) |
| D3 sim | `NodeKind.Belt` + `Node.NoCurve`: rear + side-load like a straight belt, never auto-curves; front = part cell (1, 0); same payload | AGREE; noted a crossing feeding the side of a plain belt still curves that belt (receiver decides) | kept: a crossing is a belt as a source; only the crossing itself never curves |
| D4 art | belt floor on top, network visible only at the Up/Down edges entering tunnels; drop vanilla cylinder and heat glow | AGREE + also drop `Darkness_Graphics` (`doodads_high`) | nothing draws above items; thermal glow on that tile is cosmetic loss |
| D5 connections | vanilla pipe / our cable connect via `Port_Up/Down` | AGREE; `Dont_Draw_Pipe_Connector_Into_This_Part` suppresses the neighbour's connector, the crossing's own edge art must meet it | art stubs sized to the vanilla pipe (x 17..46 of 64) / cable (19..44) |

- `belt_heat_crossing.rules`: vanilla crossing Part + Components; replaces only `Graphics` (belt floor, vanilla
  crossing walls, corridor roof), empties `Darkness_Graphics`/`HeatGlow_Graphics`, own blueprint/icon.
- `belt_cable_crossing.rules`: `cable_crossing.rules` pattern (`CableElectricPort` Up/Down, both nodes,
  availability) with the belt floor instead of the cable cylinder.
- C#: `BeltState.HeatCrossingId/CableCrossingId` -> `NodeKind.Belt`, `IsCrossing` -> front (1, 0) and
  `Node.NoCurve`; `ProtocolVersion` 3. Items render as on a straight belt.
- Art: Codex image_gen masters `tools/belt-sprites/art/belt_{heat,cable}_crossing.png` drawn like `belt.png`
  (belt up), baked rotated 90 degrees clockwise into the part frame (`-crossings` preview).
- Tests: `belts/tests` matrix source/target `cross`, `Crossings` (4 rotations x rear/left/right feeder), crossing in
  the save round-trip; native `BeltPartsProbe` (registry, tabs, doors/.8, vanilla crossing stats, Up/Down port types,
  nothing above items) and the cable loop in `PackageProbe` (electric port runtime links) cover both parts.

## 20. Smart connector filter (2026-10-27, owner: offer only what the neighbour building can take / give)

Decisions (Claude / Codex `codex exec -s read-only`, engine-verified / agreed):

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| D1 classification | per component the engine's crew-supply contract: inputs = `CanBeResourceSupplierTargetForTypes` (`ResourceConsumerRules.cs:81`), outputs = `CanBeResourceSupplierForTypes` (`BaseResourceStorageRules.cs:168`, only `SuppliesResources`), `ResourceRules.Stackable` (`FlexResourceGridRules.cs:86,97`) = generic = all 21 types; `ToggledComponents` expanded only when `IsBlueprintToggleOn` (its own CanBe* unions all modes, `PartToggledComponentsRules.cs:58-63,142`); modes: live `PartUIToggle.Mode`, build mode metadata (`BaseBuildState.cs:889`) | AGREE; also apply `OnlyWithRotation`/`OnlyWithFlip`/`IncludeWhenNotUnderConstruction` like `Part.cs:778-805`, `PartToggledComponents.cs:118` | `PartFlow.Classify` over `PhysicalComponents` with those filters; unsupported toggle chain -> counted (conservative). Construction/repair/status sinks are not rules flows -> excluded (§7 P2). Generic only within `CanBe*` + 21 `BeltTypes` |
| D2 allowed set | `Out(back part) & In(front part)`; belt/empty/construction side or a part with no belt-type flow (reactor, armor) does not narrow | DISAGREE as a general rule: side belt inputs into a connector with a room behind it would be blocked by `Out(back)`; define `Allowed == 0` | `Out(back)` narrows only when no belt/splitter/connector output feeds a connector side; two buildings without a common type -> only `none` (`any` also normalises to none). Chains: each connector narrows by its own neighbours only |
| D3 normalise | sim (bucket -11, before `Filter`/`Tick`) writes `PartUIToggle.Mode`: single type -> it; choice not offered -> none; on rebuild + neighbour `ModeChanged` (launcher type) | AGREE; bump `ProtocolVersion` (sim behaviour changed); blueprint-only values are not normalised | `ProtocolVersion` 4. Unbuilt blueprint metadata is not mutated (no writes outside MP input from UI/render hooks); build UI shows the normalised value, the sim applies it once built. Old saves/blueprints with an impossible filter: reset on the first tick (owner edge case) |
| D4 UI | engine lock `GameModeManager.IsToggleChoiceLocked` is per `PartRules` (`GameModeManager.cs:487`) -> unusable; prefix `PartPopoutToggleButton.UpdateDisplayedToggleValues` (live: remove modes no selected connector allows, union) + postfix `SelectPartsState.UpdateToggleButtons` (build: `SetAvailableChoices` ∩ vanilla set, `SetMultipleSelected` normalised, under the vanilla `_updatingToggleButtons` guard) | AGREE for build; live hotkeys (`BasePartToggleButton.cs:236`), `CycleMode`, `RevertMode`->`PrevMode` bypass the visible set | kept; any bypassed choice is corrected by D3 within one tick (deterministic, all peers). UI patches are non-fatal (failure = vanilla full list, sim unaffected). Brush before placing: full list |
| D5 choices | ALL -> none, any, 21 types (unchanged); single -> only it; set M -> none, any, M | AGREE after defining 0 | 0 -> none only |

- Code: `belts/src/PartFlow.cs` (classify, `ForConnector` on any parts manager, `Allowed`, `ChoiceAllowed`, `Normalize`),
  `BeltManager` (allowed per connector in `NodeState.Allowed`, derived, not saved; neighbour toggle watch; mode write),
  `FilterUi.cs` (2 UI patches, applied after the 4 sim patches).
- Not used: `PrevMode` for resets (not serialized -> host/rejoiner could diverge).
- Tests (`belts/tests`, F1-F5): real vanilla component rules deserialized from `Data` with the game serializer; components
  whose media cannot load headless are uninitialised instances of their real rules class with flow/toggle fields read
  from Data (engine `CanBe*`/toggle code unchanged). cannon_med/cannon_large/chaingun/railgun_loader/flak in bullet;
  missile_launcher modes 0-4 in he/emp/nuke/mine/thermal (no state = 0 HE); factory_ammo sulfur->bullet; factory_emp
  iron,copper->emp; factory_coil2 copper,coil->coil2; factory_steel iron->steel; reactor_small none; storage ALL/ALL.
- Not verified: in-game UI (popout hiding, build-mode display), side-feed detection on a live ship, MP with mixed
  selections/hotkeys.
- Codex diff review: 1 bug accepted and fixed (a side belt still under construction counted as a side feeder and lifted `Out(back)`; `SideFeeds` now skips sites, like the topology). AGREE on display-only blueprint normalisation and on hotkey/revert correction by D3.

## 21. Visual overhaul: connection-aware animated belts, closed boxes, lamps (2026-10-27)

Owner reports: a connector feeding a belt side stays straight and the L does not join (`images/10.webp` left), a torn
belt stub on an unconnected splitter output (right), items drawn on top of the splitter and never on its other half
(`images/11.png`), splitter direction unreadable and connector intake only from the back (`images/12.png`).

RCA `11.png` (log `topology ... Splitter@10,4>-1,0 ... Connector@11,3/room>splitter`): only half 1 has an input,
both outputs are connectors into a room; `Route()` hands an item from either input half straight to either output
(§6) and connectors have no lanes (§7), so half 0 never holds items by design. Items showed on the housing because the
housing is a `floors` sprite and items draw on `doodads_low`. Fix = Factorio scheme: closed housing hides contents.

### Factorio 2.0.77 scheme mirrored (scheme only, no Factorio art/assets used)

| Factorio (local `data/base/prototypes/entity/transport-belts.lua`, API docs) | Ours |
| --- | --- |
| `belt_animation_set`: 20 variants = 4 straight, 8 curves (in/out pairs), 8 starting/ending caps (`:7-47`); [TransportBeltAnimationSet](https://lua-api.factorio.com/2.0.77/types/TransportBeltAnimationSet.html) | `Variant` (`belts/src/BeltRenderState.cs`): straight, join L/R/both, curve (curve right = flipU), heat/cable crossing; rotation = `Part.Direction` like vanilla `BlueprintPartSprite.cs:58`. No caps: boxes close their own ports, belt ends are part edges |
| Underground `direction_in_side_loading` / `direction_out_side_loading` (`:333,345`): own sprite when side-loaded | join variants: the receiver's rail band opens where a side input enters (belt, splitter or connector) |
| `frame_count = 16`, `speed = 0.03125`, `animation_speed_coefficient = 32` (`:15,175,180`): surface frames advance with item travel; belts always animate; [Transport_belts/Physics](https://wiki.factorio.com/Transport_belts/Physics) | 16 frames = 1 tile of travel, items move 2 tiles/s -> `AnimationInterval = 1/32 s`, `AnimationStartTime = Zero` (global phase, neighbours seamless), always animating |
| [RenderLayer](https://lua-api.factorio.com/2.0.77/types/RenderLayer.html) `floor < transport-belt < transport-belt-endings < floor-mechanics < item < lower-object < object` | Cosmoteer `terran.rules` RenderLayers: part floor `floors` (-700) < surface quad `turrets` (-600) < items `doodads_low` (-500) < crew < lamps `lights_add` (200) |
| Splitter: belt animation under (`:660`), `structure` per direction above items (`:665-711`), `structure_patch` (`:712`); [SplitterPrototype](https://lua-api.factorio.com/2.0.77/prototypes/SplitterPrototype.html) | splitter = opaque closed 2x1 box (rules floor); C# does not draw its lane items (contents hidden, same effect as structure above items) |
| Loader: belt part + `structure_render_layer = "lower-object"` (`:996`), `direction_in`/`direction_out` sheets (`:999,1009`); [LoaderPrototype](https://lua-api.factorio.com/2.0.77/prototypes/LoaderPrototype.html) | connector = closed 1x1 container box, intake ports back/left/right, output port front, direction from machinery + small stencilled chevron |
| Loaders/splitters/undergrounds count as belt inputs for curving | a connector entering a belt side is a feeder for §4 auto-curve (D5) |

### Decisions (Claude / Codex `codex exec -s read-only`, agreed)

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| D1 layers | surface quad on `turrets`, sprites via a never-instantiated Sprite (BeltItemSprites trick); housings stay `floors`, splitter items not drawn | AGREE (`PartRules.cs:1848` registers atlas sprites of uninstantiated components); excluding splitter items is mandatory | as proposed; rules floor stays the fallback without the DLL |
| D2 variants | straight, joins, curve(+flipU), crossings; no caps | AGREE; joins from every physical feeder incl. connectors; crossings keep `NoCurve` | as proposed |
| D3 art | frames/curve/joins mechanical from the Codex straight master | DISAGREE "pixel-exact": shift moved rails too, bilinear polar remap differs at the edges (44/64 px) | only the surface band scrolls (rails static); curve edges copied from the straight frame after downscale (`curveEdges`) |
| D4 animation | 16 frames, 1/32 s, global phase; optional stop when blocked | AGREE; skip the stop detector (state + false stops on full belts) | always animate (Factorio parity); superseded by §22 (animate only while the lanes move) |
| D5 connector curve | connector at a belt side counts as feeder; protocol 5 | AGREE; collect connector feeders (rear too) before the curve rule | `BeltWorld` computes connector fronts first; curved sink -> `Rear` (preferred lane instead of far lane) |
| D6 lamps | `Node.Events` counters, render-only, `Color` toggled | AGREE; count only after a successful handoff; splitter counter = output index | `Commit()` (connector chain) and `Route()` (output used); lamp lit `tick - last < 3 && (tick & 2) == 0` |
| D7 zero-alloc | quads only on rebuild | AGREE for surfaces/lamps (`flipU` fixed per quad -> recreate on rebuild); item pool still frees/creates on hide/show | surfaces+lamps rebuilt per topology epoch; `BeltRenderState.Update` 0 B (bench); item pool unchanged |
| D8 intake all sides | rooms at back/left/right, round-robin cursor `RoundRobin[0]` 0..2, shared rate; filter union | AGREE; watch side rooms, keep the end-room self-loop check, cursor `(used+1)%3`, Decode by kind; note: a single-type union auto-sets a `none` connector (§20 D3) | built; auto-set accepted (same rule as §20, refined by §22 Auto/None). Multiple outputs: possible option, not built |
| D9 markers | small chevron on splitter/connector lids | AGREE | Codex image_gen art |

- Art (Codex image_gen via `cx -Img`, masters in `tools/belt-sprites/art`): `belt_connector` (closed container, 3 intakes,
  1 output, chevron, unlit lamp), `belt_splitter` (closed 2x1 box, closed-shutter outputs, chevron, 2 unlit lamps),
  `belt_lamp` (glow on black). Mechanical only (`tools/belt-sprites`): `bakeAnim` (band scroll, polar curve,
  joins, crossings: `mod/assets/belt_anim/<variant>_<k>.png`), `bakeLamps` (glow keyed to alpha,
  pasted at the measured lens centres), `-overhaul` preview PNG + GIF. Old `_base/_moving` layers and the misaligned
  `belt_curve` master removed.
- Code: `BeltRenderState` (pure: `Select`, lamps), `BeltManager.BuildVisuals/Render`, `BeltWorld` (D5, D8, `SideIn`,
  `Events`), `PartFlow.ForConnector` (union of back/left/right room outputs), `NodeState.Decode`, `ProtocolVersion` 5.
- Tests (`belts/tests`): side connector straight/far lane vs curve, `RenderSelect` (60 cases, 4 rotations), splitter
  halves render (4 rotations x flip), render client-only (digests equal, lamps, 0 B bench), multi-side intake.
- Not verified: in-game look (atlas load of animated `turrets` sprites, quad rotation/flip, lamp glow strength), MP.

## 22. Caveat closure: Auto/None filter, still belts, blueprints, bench noise (2026-10-27)

Decisions (Claude / Codex `codex exec -s read-only`, engine-verified / agreed):

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| D1 explicit None (owner bug: §20 D3 auto-fill overrode a chosen None) | mode 0 = Auto (Default, vanilla `toggle_fire_auto.png`), append mode 23 = None (explicit stop, `cancel.png`); indices 1..22 unchanged | AGREE; the engine saves the numeric `Mode` (`PartUIToggle.cs:45`) and drops the default from metadata (`:65`) | old saves/blueprints: mode 0 (old default none) -> Auto, already auto-written types stay explicit types; an old explicit none cannot be told from the default -> Auto (accepted limit) |
| D2 Auto semantics | Mode never written for Auto; filter = the single allowed type, else nothing (old default); a ruled-out explicit choice -> Auto (was none); Auto/None always offered, never rewritten | AGREE; writing the type would lose Auto again (DISAGREE with that alternative) | `PartFlow.Normalize` (ruled-out -> 0), `PartFlow.Filter` (sim filter), `ChoiceAllowed`; `ProtocolVersion` 6; payload unchanged. Known change: an Auto connector stops when its allowed set widens from one type to several (old code kept the written type) |
| D3 future types | comment only | DISAGREE: `mode - 2` would read 23 as a type | `BeltTypes.ModeAuto/ModeAny/ModeNone`; `FilterFromMode` skips 23, appended types take modes 24+ |
| D4 still belts (owner: animation runs on blocked/empty belts) | client-only `BeltRenderState.Moving`: per belt node an FNV signature of lane counts + positions, `lastMove` on change, animate while `tick - lastMove < HoldTicks` (6 = 0.2 s) | AGREE as a cosmetic heuristic (moves can be 1-16 units at gaps/ends; a 32-bit collision only delays a stop by one tick); hold value to confirm in game | crossings use the same path; arrays sized on `Reset` only, 0 B per tick (render bench) |
| D5 pause / resume | `ManagedShipQuad.UpdateAnimationSpeedFactor(0, clock)` freezes the current frame (`ManagedShipQuad.cs:234-261`); resume `(1, clock)` + `AnimationStartTime = sprite.GetAnimationStartTime(clock)` (global phase) | AGREE (frozen-phase resume would leave seams; the resume jump can be any of 16 frames); quads created at speed 0 store a frame index (`:219`) | surfaces are created still; `Render` compares `q.AnimationSpeedFactor != 0` with `Moving` every tick (covers rebuilds) |

- Blueprints (mechanical, `tools/belt-sprites` `blueprintOf`): rank-match the part art luminance to the colour ramp
  measured on the old Codex splitter blueprint (quantiles 0/.1/.25/.5/.75/1), alpha `(255 - R) * 0.8` as before. Now
  `belt_{splitter,connector,heat_crossing,cable_crossing}_blueprint.png` follow the closed-box / crossing art (connector
  used the vanilla conveyor blueprint, crossings their colour icon); the old splitter blueprint master was removed. Belt
  keeps the vanilla conveyor blueprint (straight belt, direction chevrons). Icons were already baked from the new masters.
- Flaky `P3 bench loop 500 tiles` 96 B (4/30 runs): reproduced; tick of the allocation varies (30004, 42038); 0/30 with
  `DOTNET_TieredCompilation=0`, `DOTNET_TieredPGO=0` or `DOTNET_TC_OnStackReplacement=0` each -> the runtime's tiering
  transition on the bench thread, not sim code (a code allocation would not depend on the JIT mode). The render bench
  showed the same 96 B once. Fix (methodology): `Meter.Run` (belts/tests/Program.cs) measures 3 windows, alloc = min
  over windows (a per-tick allocation shows in every window), all windows printed.
- Removed: `belt_curve.png` (baked, never referenced since §21 surfaces).
- Tests: F3/F3b/F4 (Auto/None normalise, effective filter, offered choices), mode map incl. 23/24, rules choice order
  (auto, any, 21 types, none), `RenderMoving` (empty still, travelling items animate belt/crossing/end, exactly one
  on/off per tile, still once blocked).
- Codex diff review (`codex exec -s read-only` over the full diff): no bugs.
- Open (not small/safe): deconstructed belts lose their items (`BeltManager.OnPartRemoved` shortcut: needs removal vs
  split told apart on a live ship); item quads still free/create atlas quads on hide/show (§21 D7); no frame
  interpolation of item positions; connector with several outputs not built. Not verified: in-game look of still/moving
  surfaces and the hold value, Auto/None icons in the popout, MP.

## 23. Factorio-style splitter / connector settings (2026-10-28)

Reference: wiki.factorio.com/Splitter, /Belt_transport_system (input priority: the other input only when the priority
belt has a gap; output priority: the other output only when the priority one is full; a filter uses the output slider:
matching items to that side only, the rest to the other only; lanes split independently).

Decisions (Claude / Codex `codex exec -s read-only`, static engine reading / agreed):

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| D1 storage | 4 new vanilla UIToggles (Popout, `RememberValue`, `Default = 0`) like `belt_filter`: splitter `belt_input_priority` / `belt_output_priority` (0 none, 1 left, 2 right), `belt_splitter_filter` (0 none, 1+i = `BeltTypes.Ids[i]`); connector `belt_source_priority` (0 round-robin, 1 back, 2 left, 3 right) | AGREE; MP applies `Mode` via `MPSetUITogglesInput.cs:88`; splitter filter needs its own decoder (the connector map reserves 23) | save, blueprint copy and MP sync are vanilla toggle state; `BeltManager` copies the modes into `NodeState` every tick (`NodeState.Setting`: out of range -> 0); payload v2 unchanged; settings hashed in `NodeState.Digest` (Codex: the MP hash would miss them otherwise); `ProtocolVersion` 7 |
| D2 left/right | relative to travel: left half = `(Sx,Sy) == (Dy,-Dx)` (`BeltWorld.LeftHalf`) | AGREE (holds for rotation + FlipX) | same rule for input, output and filter |
| D3 input priority | the priority half is routed first each tick (none = half 0 first, the old code): the other input only fills spare output capacity; a backlogged priority input is ready again the tick after a take | AGREE; keep none as it was (on a flipped splitter none favours the right half) | `Route`; none stays bit-identical (golden test) |
| D4 output priority | `o` = priority side instead of the round-robin; overflow to `o ^ 1` as before | AGREE, but a deferred handoff must not count as a full output (D6) | D6 became a claim (a real block for that tick), so a refusal is a full output |
| D5 filter | matching -> output priority side (left when none, the Factorio GUI rule computed in the sim), the rest -> the other side, no overflow; a blocked side holds its lane head (head-of-line, Factorio) | AGREE | items never lost: the held drain is saved as before |
| D6 connector priority | two handoff passes (non-priority intakes deferred to pass 2) | DISAGREE: chain A -> B starves A's input when B runs first in pass 2; `Extract` self-sink is `Rear`; room dedup loses sides | **claim**: before any handoff, a priority connector computes `Pending(side)` (room supply, a held drained item of a feeding belt / splitter half, or a feeding connector with anything pending, under the chain filter and acceptable to a room end); while claimed, `Resolve` refuses every hop into it from another side and `Extract` skips its other rooms. Order-independent; rooms kept per side (pulled once per round as before); the priority room does not move the round-robin cursor |
| D7 rules / UI | one rules file per toggle, vanilla icons (alternating, rotate left/right, minimap arrow, resource icons); `FilterUi` stays connector-only | AGREE | 8 languages; splitter offers all 21 types |
| D8 visual hint | skip (lamps already flash on transfers; a static side hint needs new art) | AGREE | not built |
| D9 tests | pure world tests | DISAGREE: also chain counterexample in both node orders, multi-side room, 4 rotations x flip, blocked filter side; engine save/blueprint/MP | added all pure ones; engine-backed save/blueprint/MP not testable headless (see below) |

- Codex diff review 1: P1 (a splitter holding items for its other output, by filter or output priority, claimed the
  connector forever). Review 2: P1 (an overflow into the priority side never claimed, so an endless side room starved
  it when the connector ran first). Fixed: `SplitterOffers` = first choice (`FirstOut`, shared with `Route`) or,
  without a filter, overflow when the first choice cannot take it (`CanTake`, side-effect free). Review 3: P1 (`CanTake`
  ignored claims, so a first choice claimed by its own room looked free). Fixed: claims in two order-independent
  passes (pass 0 without overflow, pass 1 judges overflow against the pass-0 claims). Review 4: P1 (every held item saw
  the first choice free although it takes one per tick). Fixed: `SplitterFeeds` = side-effect-free trial of `Route`
  reserving one item per lane into a belt's back, one per output otherwise. Review 5: P1 (a fallback after a filter
  refusal reserved nothing). Fixed: the trial models both attempts and reserves where the item is accepted; pass 0 reads
  no claims (claims are not saved, so a rejoining peer computes the same). Regression tests red before, green after.
- Review 6: P1 (two passes miss a cascade of overflows through two splitters). Fixed: claim passes repeat until no claim
  changes (at most 8, each reading only the previous pass: order-independent); this cascade has no dedicated test.
- Known limits (`shortcut:`): a connector feeder tree deeper than 8 (the fixed pass cap was replaced in §24)
  counts as not pending. Belts feeding a round-robin connector
  still enter in node order (as before; only rooms take turns).
- Tests (`belts/tests/SettingsProbes.cs`): input priority (16 cases), output priority (16), filter (48: 4 rotations x
  flip x output none/left/right x other side open/blocked), defaults golden digest recorded on the pre-§23 code,
  connector source priority (rooms per mode, belt vs room both ways, room on two sides, Codex chain counterexample in
  both node orders, splitter holding items for its other output, overflow vs an endless room), settings outside
  the payload + rules toggles/registration. `RulesProbes.Assets` (R2, owner crash 9dac7b9): list items
  comma/newline separated, every referenced asset/rules file exists.
- Not verified: in game (popout icons and tooltips, blueprint copy/paste of the new toggles, save/load, MP late join).

## 24. Claim cascade debt: regression test and exact pass bound (2026-10-28)

Debt from e268d18 (§23 review 6): the claim-pass repetition had no test, no Codex re-check, and a fixed cap of 8.

| Decision | Claude | Codex | Agreed |
| --- | --- | --- | --- |
| D1 test | `WorldProbes.ClaimCascade`: S2 (output priority to a side facing no node) only overflows into C_R's priority right side, so C_R is claimed from pass 1; S (output priority right = C_R) then overflows into C_L (priority back, endless side room) only at pass 2. One sulfur lane (a second held item would overflow anyway, §23 review 4); 2 node orders x 4 x 4 feed/room start ticks, because the starvation needs both connector period phases aligned | AGREE: claims go (false,false) -> (false,true) -> (true,true); single lane avoids the reservation bypass | red with 2 passes (pre-fix: 8/32 cases, C_L sulfur 0), green now (32/32) |
| D2 bound | 1 + connector count passes: pass p reads only pass p-1, acyclic dependency chains have at most K-1 edges | AGREE, plus: the operator is monotone (pass 0 is a subset of any overflow pass; more claims only close first choices, adding overflow), so claims only grow, cycles included -> no oscillation | cap = 1 + connectors (`BeltWorld._claimPasses`), exact in all cases; the cyclic `shortcut:` is gone. `ClaimDepth` (feeder search depth) adds no pass delay |
| D3 cost | early exit when nothing changes keeps the usual 1-2 passes | a long cascade costs up to K+1 full passes; an exact cheaper scheme is a dirty-connector queue over the claim-read graph | kept the simple loop (no measured need); queue = upgrade path if a tick profile shows it |

- Codex diff review: no real bugs, lockstep-safe. Not measured: tick cost of a long cascade in game.

## 25. One adaptive belt crossing (2026-10-28)

`belt_heat_crossing` + `belt_cable_crossing` -> one build-menu part `morgott.crewless_logistics.belt_crossing`
(`mod/belt_crossing.rules`, Storage + Power tabs), old IDs in its `OtherIDs` (saves, blueprints).

| Decision | Claude | Codex | Agreed |
| --- | --- | --- | --- |
| D1 ports | no toggled ports (art depends on links; `PartToggledComponents` adds via `EnqueueDeterministic`): the vanilla crossing thermal `Port_Up/Down` and our `ElectricPort_Up/Down` always present; a port links only a compatible type (`PartNetworkPort.cs:70`) | AGREE (+ `PortHandler.cs:86` checks both sides); two always-available channels, mixed pipe/cable = two dead ends | as proposed; the mode is derived from neighbours, so an old heat / cable crossing loading as the new ID shows its mode with no migration |
| D2 art | MultiToggles Any[ports] select three toggled floors | DISAGREE: two adjacent crossings link both networks (`PortHandler.cs:59`), so a cable -> crossing -> crossing -> cable chain would turn heat; walk the crossing chain to the part beyond; plain rules floor + C# surface | `BeltManager.CrossingVariant`: per Up/Down edge follow connected ports through crossings to the first other part (thermal link -> heat, electric -> cable), `BeltRenderState.CrossingVariant` (none -> plain, one network -> heat/cable, heat+cable -> mixed variants `HeatCableCrossing` / `CableHeatCrossing`), re-read each render tick (cached bools) and `SetSprite` on change; rules floor = plain belt (no-DLL fallback) |
| D3 protocol | bump 7 -> 8 | DISAGREE: `ProtocolVersion` only feeds the sim hash; the part set change already changes the rules hash checked at join (`GameSetupScreen.cs:2364`) | kept 7: no sim/payload change (crossing stays a no-curve belt node) |
| D4 icon | left half heat master + right half cable | DISAGREE with a cut through the belt; keep one master's belt/rails, take the other's side entry | `mixedCrossing` (columns right of the belt band from the other master), mechanical; icon/blueprint = heat Up + cable Down |
| D5 registry | one AddMany entry, .7 crew, doors inherited, 8 languages | AGREE; set .7 explicitly (vanilla .8), fix the stale 80% texts | done (part strings + mod description) |
| review | - | P2: the second independent blueprint node shifts the thermal overlay 1/6 cell diagonally | fixed like `cable_heat_crossing.rules`: thermal blueprint routes on `CrewlessLogisticsBlueprintNode`, electric on `BlueprintNetworkNode`, `OverlaySpacingNode` |

- Art: new surface variants `plain_crossing`, `heat_cable_crossing`, `cable_heat_crossing` (`tools/belt-sprites`, mechanical from
  the Codex masters); removed the two old static floors/icons/blueprints. `BeltRenderState.Variant` order = `belt.rules`
  (test `WorldTests.CrossingVariants`, all 9 link pairs).
- Probes: `BeltPartsProbe` (thermal + electric Up/Down ports, none on the belt axis, OtherIDs), `PackageProbe` (electric ports
  `ElectricPort_*`), `LocalizationProbe` (30 keys), `NativeDeviceCatalog` added IDs.
- Not verified: in game (old saves/blueprints loading as the new part, overlay centering, art switching when a pipe/cable is added).

## 26. Damage stages, item drops on removal, part salvage (2026-10-28)

| Decision | Claude | Codex | Agreed |
| --- | --- | --- | --- |
| C1a levels | 3, like the vanilla corridor / heat pipe crossing floors (the animated conveyor doodad has 1); level = `(int)(DamageFraction * 3)` capped (`PartSpriteRules.cs:115`) | AGREE; the C# surface covers the floor, so it must switch too | belt, connector, splitter, crossing floors: `<file>`, `_33`, `_66` |
| C1b art | 2 Codex image_gen damage decals (light / heavy, magenta key) composited by `tools/belt-sprites` instead of 10 regenerated masters (alignment, seamless edges) | AGREE; mask floor from the clean master, keep decals off joining edges | `art/belt_damage_{33,66}.png` (`keyMagenta`), `damageFrame`: decal only on part pixels of the level-0 bake, vanilla `floor_33/_66` in the margins, 3 px edge fade; static sprites and every animation frame |
| C1c surface | level 2 one static frame | DISAGREE: a still belt under moving items reads as stopped; animate all levels | `BeltSurfaceSprites` = `RandomDamageLevels` `[variant][0/33/66]`, all animated; C# `BeltRenderState.DamageLevel` per render tick; items unchanged (sim unchanged); owner option 2 (2026-10-28): the decal's surface band scrolls with the surface (`surfaceDecal`: `shiftBand` step, wraps within the tile, alpha fades to 0 over 8 px at entry / exit) through each variant's own remap (curve polar map, join strips, crossing rotation), rails / frame keep the static decal |
| C2 drops | split -> keep (`StructureContiguityManager.IsSplitting`, same Part moved, `:112-145`); Health 0 (weapons and crew deconstruction both, `PartsManager.OnPartDestroyed :812`) -> vanilla `ResourceStorage.OnDestroying` parameters; intact removal (delete, replace, reset) -> everything | AGREE; intact = `(1f, destroyed false, IsHumanPlayerShip)` (`ResourceStorage.cs:197`); use the manager's ship (`part.Ship` is null at `PartRemoved`); `Sim.Rand` keeps MP deterministic | `NodeState.OnRemoved` + `BeltManager.DropItems`; tiles cleared, no double drop |
| C3 salvage | the engine drops a destroyed / deconstructed part's own `Resources` x drop chance (`PartsManager.cs:832-858`); ours inherit vanilla rules: belt / connector / crossing 4 steel + 2 coils (vanilla crossing 4 + 2, conveyor 4 + 1), splitter 8 + 4 (2 tiles) | AGREE; pin the effective inherited values | test `WorldTests.PartCosts`, no code change |
| review | - | P2: `SetSprite` on a paused quad re-phases its frozen frame | level/variant switch assigns `Sprite` (same interval, phase kept) |

- Tests: `DamageLevels`, `RemovalDrops`, `PartCosts`, `CrossingVariants` (every surface with 3 levels); `BeltPartsProbe`: 3 floor levels; `tools/belt-sprites` `go test`: level 0 unchanged, surface damage scrolls 4 px / frame, damaged tile borders = undamaged.
- Preview: `tools/belt-sprites -damage` (crossing arts + damage levels of every part; animated `.gif` next to it: scrolling damage on straight, curve, crossing).
- Not verified: in game (damage switching, nuggets on delete / replace, split keeps items, MP).

## 27. Scale: 20 big ships, off-screen / zoom LOD (2026-10-28)

Owner target: ~20 big ships full of belts without lag. Measured first (`belts/tests/ScaleBench.cs`, Release, this PC):
20 ships x 20 modules (100-tile clockwise loop with 4 curves, source connector sideloading from an endless room, splitter
with mixed §23 settings + 3-tile branch + sink connector) = 41600 lane tiles, 42000 nodes, ~119k items (saturated).

| Metric | Before | After |
| --- | --- | --- |
| fleet sim `BeltWorld.Tick` (20 ships) | 858-918 us/tick (LaneSim 435-466 of it), 0 B | 360-405 us/tick, 0 B (budget 1000 = 3% of a 30 Hz tick) |
| render state per tick (`BeltRenderState.Update` + item points, all 20 ships in view) | 1008-1118 us | same work per visible ship (~48 us/ship); 0 for ships out of every view and for coarse-LOD ships |
| quads per ship | 5934-6060 items + 2040 surfaces + 80 lamps | same when in view and fine; coarse: 0 items, surfaces frozen, lamps off |
| item quad writes (`ManagedShipQuad` SetSprite + Location per item per tick) | every ship every tick, in view or not | only ships in view at fine LOD |
| topology rebuild of one ship (2100 nodes) | 0.5-0.7 ms, once in the tick after any part change of that ship | unchanged (bounded, kept full) |
| Debug (unoptimised JIT) | - | sim 1335 us, render state 3607 us (budgets x4) |

Engine facts (Decompiled, scout + Codex):
- `ShipRenderer` culls per ship by its render bounds and skips ships with screen radius^2 < 0.0625
  (`Cosmoteer.Ships.Rendering/ShipRenderer.cs:964-997`); no per-quad culling, one batch per texture (`:962`).
- No zoom LOD for any part graphics: `ShipRenderLayerRules` has no zoom fields; storage items (`TypedResourceGrid.cs:368`)
  and part animations are plain `ManagedShipQuad`s without view checks; animation runs in the shader from
  `AnimationStartTime`. Only shield arcs check `Ship.IsInView` (`PartShieldArcsMimic.cs:121`); zoom fields exist only for
  `CustomShipIconRules.ShowAtZoom` and particle `MaxEmissionZoom` (`ParticleEffect.cs:257`, `Sim.Camera.UniformScale`).
- `ManagedShipQuad` setters compare, then rebuild the quad data at once, visible or not (`ManagedShipQuad.cs:41-88,266-271`);
  `Sprite = null` disposes the atlas quad, a sprite creates one again (`:41-72`).
- The miniview renders the same scene (`Miniview.cs:668`); `SimRoot.WorldMiniviewRect` (`SimRoot.cs:433,796`);
  `Ship.IsNearOnScreen` = main view rect inflated by its size vs `WorldBoundingCircle` (`Ship.cs:683`). Ship icons /
  screenshots (`ShipRenderer.cs:316,408`) draw all layers.
- Per-frame hook: `SceneComponent.Update`, registered when overridden (HalflingCore `SceneComponent` override detection by
  delegate `DeclaringType`); parallel update buckets are 8/10/12/15 (`SimRoot.cs:606-609`), ours is the default 0.

Decisions (Claude / Codex `codex exec -s read-only`, agreed):

| # | Claude | Codex | Agreed + why |
| --- | --- | --- | --- |
| S1 | `BeltWorld` loops only nodes that act (`_active`: not a belt, or a belt with a handoff output) and claim passes only connectors (`_conns`), ship order kept | AGREE; lists after the topology is final, claim phases stay separate | built; bit-identical |
| S2 | `LaneSim` closes drains only on `_drainTiles` = belt ends (next < 0) + tiles still holding an item after `Build` (MoveLane drains only at ends) | AGREE; `OpenDrain` is public: restrict it to ends (documented) | built; bit-identical |
| S golden | protocol bump + golden digest | AGREE (bump 7 -> 8) | `ScaleTests.FleetGolden`: fleet digest after 9000 ticks = value from the pre-§27 code (8c073fa worktree), plus all existing claim / held-drain tests |
| S3 sleep | no idle-segment sleep: saturated / blocked lines still move every tick, wake rules (downstream frees, inserts, rebuild) = determinism risk | AGREE | deferred; sim is 3% of the tick budget for the target |
| R1 | skip render work of a ship in no view: `IsNearOnScreen` or miniview rect (not `IsWithinSightOfLocalPlayer`) | DISAGREE with per-tick only: a paused game / camera move would show stale quads | render moved to per-frame `Update`; heavy work only on a new tick, on coming into view, on an LOD change, or after a rebuild |
| R2 | coarse LOD above `CoarseTiles` = 160 ship tiles across the view (~7 px/tile at 1080p, item < 2 px), 10% hysteresis | count both cameras: the most detailed view showing the ship wins; skip signatures / item points / quad loops entirely when coarse | coarse: item quads freed (once), surfaces frozen, lamps off, nothing per tick; static rules floor stays |
| R2b | - | skipped `Update`s leave old transfer counters: false lamp flash on return | `BeltRenderState.Rebase()` (no allocation) on coming into view / leaving coarse |
| R3 | keep surplus item quads resident transparent instead of freeing the tail on every count change | AGREE; transparent quads are still drawn: bound it | up to `PoolSlack` = 64 transparent per ship, the rest freed (closes the §21 D7 / §22 churn gap) |
| R4 | crossing port IDs built once | AGREE | static `ID<PartComponentRules>`; was a string concat + ID per crossing per tick (allocation) |
| P1 | perf line in the belts log | split sim / render, count only shown ships | every 300 ticks with the P1/P3 line: `perf beltShips tiles sim us/tick render us/tick (us/frame, fps) shownShips/tick coarse quads/tick items surfaces lamps` |

- Codex extra risk, not changed: crossing art walks each crossing chain from every crossing per rendered tick (O(n^2) on
  long chains, bound 1024); only ships in view at fine LOD pay it now. Upgrade path: cache per chain, invalidate on port change.
- Tests: `ScaleTests.FleetBench` (budgets: sim 1000 us, render state 2000 us all in view, rebuild 20 ms, 0 B; Debug x4),
  `FleetGolden`, `RenderLod` (hysteresis, no view = coarse, rebase: no stale lamp / move, activity returns).
- Not verified: in game (quad write cost per item, LOD threshold feel, miniview, pause + pan, frame time with 20 ships), MP.
