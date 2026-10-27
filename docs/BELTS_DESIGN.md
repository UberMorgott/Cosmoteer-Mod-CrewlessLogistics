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
|---|---|---|
| `morgott.crewless_logistics.belt` | 1x1 | belt, auto straight/curve, 2 lanes |
| `morgott.crewless_logistics.belt_splitter` | 1x2 | 2 in / 2 out, split + merge, lane-preserving |
| `morgott.crewless_logistics.belt_connector` | 1x1 | directional room<->belt/room transfer, resource filter |

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
- Feeder into the back of a splitter / connector: see 6/7.
- Merge priority when two inputs compete for one lane gap: rear first, then alternate sides by `Tick` parity
  (deterministic, no Rand needed).

## 6. Splitter 1x2

- Two input cells (back of each half), two output cells (front of each half). Lane-preserving: L stays L, R stays R.
- Per lane, items from both inputs alternate between both outputs (round-robin counter per lane, saved).
  Blocked output -> all goes to the free one. One input only -> even split. One output connected -> merge.
- No filter/priority in v1 (Factorio priority/filter = open question Q4).
- Replaces old merger + 3-way splitter.

## 7. Connector (replaces extractor, loader, inserter, underground)

Directional 1x1, arrow back -> front. Behaviour from what occupies the back cell and the front cell
(`Ship.Parts[cell]` returns the part covering that cell, any cell of a multi-cell part: same lookup as
`ProxyHandler.cs:157-158`):

| back | front | behaviour |
|---|---|---|
| part (room) | belt | extractor: pull filtered type from part sources, put on belt |
| belt | part (room) | loader: take filtered items from belt end, insert into part sinks |
| part | part | direct room-to-room transfer |
| belt | belt | pass-through belt tile with filter (non-matching items block) |
| empty / other | any | idle |

- Belt end condition: back belt must point into the connector; front belt must not point back into it.
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

## 8. Endpoints (examples, all via sources/sinks — no whitelist)

| part | component (vanilla) | as source | as sink |
|---|---|---|---|
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

Headless: `belts\tests\Run-BeltProbes.ps1` (game's own .NET 10.0.12 runtime + real 0.30.5 `Cosmoteer.dll` + loader
`0Harmony.dll`). Live: test-only mod `belts\probe-mod` installed by `Install-BeltProbe.ps1` as
`Mods\CrewlessLogisticsBeltProbe` (separate ID `morgott.crewless_logistics.belt_probe`; energy package untouched).

| Probe | Result | Evidence |
|---|---|---|
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
- Q6: belts are crew-walkable like corridors.

## 17. Decisions with Codex (2 rounds, `codex exec -s read-only`, static engine reading)

| # | Claude view | Codex view | Agreed + why |
|---|---|---|---|
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
- Parts (rules, vanilla components only): `belt.rules` (rotatable vanilla conveyor body, crew speed 1 every way,
  static belt floor art on `floors`), `belt_splitter.rules` (2x1), `belt_connector.rules` (+ UIToggle
  `BeltFilter`, Popout `belt_filter_toggle.rules`: none (default), any, 21 types). Build tabs: Storage and Power.
  Art: Codex image-gen masters in `tools/belt-sprites/art` (256 px/tile), baked to 64 px/tile + icons by
  `tools/belt-sprites` (Go, trim/downscale only). `belt_curve.png` is baked but not wired: floor art is static per
  part, so curves need a C# floor quad chosen from `Shape.CurveLeft/CurveRight` (CurveRight = mirrored).
- Core (pure, headless-tested): `LaneSim` (lanes on per-part `LaneTile`s), `BeltWorld` (topology §4-§7 + tick),
  `NodeState` (payload v2). Changes vs §3/§10: speed remainder is derived from `Sim.Tick`
  (`floor((t+1)*512/30) - floor(t*512/30)`), so no accumulator is saved; drains open for one tick only (held items
  are saved); carriable types = 21 vanilla stackable resources (`BeltTypes.Ids`, append-only); connector rate
  1 item / 2 ticks (Q3); a connector straight in front of a splitter half is fed by the splitter's
  routing (its filter acts as that output's gate; a mismatch yields to the other output).
- Glue: `BeltManager` rebuilds from `ship.Parts` order and `ship.Parts[cell]`; `PartRoom` = live
  `ResourceManager._sources/_sinks` of the part with the P2 gate; items drawn as `ManagedShipQuad` on `doodads_low`
  with each resource's `StackSprites[0]` (updated per tick, no frame interpolation); destroyed belts drop nuggets.
- Not in v1: curve/splitter surface art per state (rules art is a straight arrow), animated surface,
  items on deconstructed belts are lost (destroyed belts drop them), frame interpolation of item positions.
