# Automation ideas (research, 2026-10-28) — Cosmoteer 0.30.5, Crewless Logistics

Status: ideas only, nothing built. Claude + Codex (`codex exec -s read-only`, 2 rounds, static source reading; no builds,
game runs or MP tests). Engine refs under `E:\DEV\cosmoteer\Decompiled`; `V/` = `D:\Steam\steamapps\common\Cosmoteer\Data`;
`R/` = this repo. Refs marked (C) were cited by Codex and not re-read by Claude; the rest were read by Claude.
Factorio used for mechanics only (circuit network, requester/provider/buffer chests, wiki.factorio.com), no assets.

## 1. Key finding: vanilla already ships a rules-level signal bus

- Part networks carry scalar values between parts: `NetworkValue` publishes a local component value, `NetworkValueReceiver`
  queries the whole reachable subnetwork (`Cosmoteer.Ships.Networks/PartNetworkValueReceiver.cs:87`) and reduces it with
  `Mode` = Add / Multiply / Min / Max / Average (`:95-101`, `MultiValueMode`). Vanilla use: railgun overclock,
  `V/ships/terran/railgun_loader/railgun_loader.rules:291-374`.
- Logic library (rules `Type`): `ThresholdToggle` (Min/Max/Invert window, `PartThresholdToggleRules.cs:17-29`),
  `ValueOperation` (Divide/Power/Modulo/Subtract), `MultiValue`, `MultiToggle`, `Timer`, `DelayTrigger`, `ToggleTrigger`,
  `TriggeredToggle` (state/latch), `ValueAccumulator`, `ToggledComponents`, Value/Toggle/Trigger proxies.
- Storage level is a value: `BaseResourceStorage` implements `IPartComponentValue`; raw counts via
  `ProvidedValueRange = 0`, `ProvidedValuePerResource = 1` (C: `BaseResourceStorage.cs:242`, `BaseResourceStorageRules.cs:112,116`).
- Our energy cables already are part-network buses (`R/mod/cable.rules:20`, `R/mod/common.rules:21`). Port `Types` decide
  connectivity, `Categories` only filter publishers at query time (C: `PartNetworkPort.cs:70,87`, `PartNetworkFilter.cs:40`)
  -> signals can ride the existing power cables as separate categories (channels).
- Limits (C): `ToggleProxy` only reads a neighbour (`PartToggleProxy.cs:111`) -> actuation must be a gate injected into the
  target part. `ThresholdToggle` has no hysteresis (`PartThresholdToggle.cs:120`) -> latch = two thresholds +
  `ToggleTrigger` -> `TriggeredToggle`. `ValueOperation` re-evaluates on change, no implicit tick delay -> feedback loops
  need explicit delay. Empty reductions: Add 0, Multiply 1, Min +inf, Max -inf, Average NaN. Cargo holds
  (`FlexResourceGrid`) expose no value -> mixed-cargo sensing needs C# (`FlexResourceGrid.GetTotalQuantity(type)`, C: `:1259`).
- Injection into vanilla parts is a proven mod pattern: `R/mod/generated.actions.rules` (AddBase on 60 parts). For gates
  also targeted `Add`/`Replace` actions are needed, AND-ing into the part's existing `OperationalToggle`/`MultiToggle Mode = All`
  (C: `ModAddAction.cs:75`, `ModReplaceAction.cs:39`); keep vanilla conditions (C: `V/.../factory_ammo.rules:54-58`).

## 2. What vanilla automates vs where crew micromanagement hurts

| Area | Vanilla today | Pain | Evidence |
| --- | --- | --- | --- |
| Resource / ammo hauling | Crew `ResourceTransferJob`, supply-chain assignments, sink `HighPriority`, per-resource ship limits | Walking time, combat starvation, no stock targets | `Cosmoteer.Ships.Crew.Jobs/ResourceTransferJob.cs:33`, `Cosmoteer.Ships.Resources/ResourceManager.cs:32,87` |
| Power | Crew-fed reactors/batteries; mod: crewless cables | No load priority on brownout | `R/mod/capacitor.rules`, `R/mod/source-components.rules` |
| Factories | `ResourceConverter` stops only when its own output fills | No remote demand, overproduction eats steel | C: `ResourceConverter.cs:329,340` |
| Repair | Crew `RepairPartJob`, AI self-repair module | Crew pulled from guns | `RepairPartJob.cs:24`, `AISelfRepairModule.cs` |
| Fire | Status `cosmoteer.fire`, crew job ExtinguishFire, `fire_extinguisher` part fed by crew | Crew + extinguisher refills mid-combat | `V/statuses/fire/fire.rules:5-6,59-60` |
| Salvage | Crew `SalvageJob` + tractor beam | Unloading/sorting into production | `SalvageJob.cs:32` |
| Doors | Crew-driven open/close hold count | Not a logistics problem (no sealing semantics) | `Cosmoteer.Ships.Doors/Door.cs:192,210` |
| Priorities | Crew roles + job priorities | Global, not per-stock or per-state | `Cosmoteer.Ships.Roles/*` |
| Overflow | Manual eject-excess | Manual | C: `ResourcesToolbox.cs:332`, `Airlock.cs:114` |

## 3. Ranked top 10 (agreed after merges)

1. **Signal kit + Governor (rules-first).** Sensor parts publish stock / capacitor charge / heat as `NetworkValue` on a
   channel category over the existing cable bus; Governor parts latch a band (low/high threshold, UIToggle presets) and
   drive gates injected into vanilla factories, weapons, shields. Brownout load shedding, thermal governor and
   "low steel -> start factory" are presets of one mechanism (Claude C3, Codex agreed). Value: crewless "if X then Y" is
   the core of the mod's theme; Factorio decider/arithmetic combinators map onto `ThresholdToggle`/`ValueOperation`.
   MP risk low (vanilla components, vanilla state). Risks: feedback timing, empty-channel defaults, gating input power
   does not stop a part running on its own buffer, idle drains (C: `factory_ammo.rules:104,149`).
2. **Demand ports (requester / provider / buffer).** Extend belt connectors with target stock band, provider reserve and
   priority; ammo-bank balancing is a preset (Claude C5, agreed). Value: stops "fill everything" belts, keeps weapons
   topped up without shuttling. C# in `BeltManager` (-11 bucket), reuse pickup/delivery reservations
   (C: `R/belts/src/PartRoom.cs:60,93,108`); settings via UIToggle (`MPSetUITogglesInput.cs:88`), all latches and cursors
   saved + hashed. Risk: oscillation, competing requesters double-counting stock.
3. **Commissioning + flow dashboard.** Client-only overlay: why a part is idle (disabled, filter mismatch, reserved,
   destination full, disconnected, governor off), items/s per belt, starved consumers. Makes 1 and 2 debuggable. MP risk
   low if it never feeds the sim (counters allowed, not hashed; precedent C: `R/belts/src/BeltWorld.cs:71-72`).
4. **Reserve floors.** Protect a minimum of steel / FTL fuel / munitions from routine production; repair and weapons may
   dip in, factories may not. Rules on dedicated buffers first, C# for cargo. Vanilla limits cap maxima, not minima.
   Risk: deadlocking repairs.
5. **Mixed-cargo sensor (DLL-optional bridge).** C# reads `FlexResourceGrid.GetTotalQuantity(type)` and writes a
   synthetic-resource `ResourceStorage` on a rules-only sensor part, so the value enters the vanilla bus. `StaticValue`
   is unusable as a writable bridge (C: `PartStaticValue.cs:13,18,34`). Prototype first: never duplicate a real
   commodity in ship totals; snapshot then publish in stable order; stale value without DLL needs a validity flag.
6. **Jam-aware batch coordination.** Upstream factories pause when the downstream chain is blocked, resume on capacity.
   A Governor policy fed by belt/connector state, not a new component set. Risk: dependency deadlocks.
7. **Salvage receiving depot -> belts.** A depot part that crew/tractor salvage lands in, unloaded crewless onto belts
   with filters/overflow. No autonomous world pickup (scope + arbitration risk, C: `SalvageJob.cs:586`).
8. **Crewless fire suppression.** Extinguisher reservoir fed by belt/network sprays reachable burning parts through the
   native `StatusResourceSink.ReceiveResources` (C: `StatusResourceSink.cs:147-169`), consuming real resource. C#, M.
   Must keep range and combat cost; replaces a crew service.
9. **Programmable logic editor.** Free-form channels, constants, operators, wiring UI. Deferred until presets +
   diagnostics prove demand and the feedback model is fixed. Heavy custom UI.
10. **Opt-in overflow jettison.** Auto-eject above a cap, with exclusions and reserves; low priority (irreversible loss,
    recollection loops; vanilla manual eject exists).

Rejected / narrowed: door automation (Codex: no sealing/O2 semantics in `Door.cs:188,206`; Claude agrees), free HP
regeneration (balance), autonomous salvage collection (H risk), more transport primitives alone (§23 already covers them).
Material-paid auto-repair from storage: not rejected, deferred (separate job/reconstruction logic, C: `RepairPartJob.cs:59-64`).

| # | Idea | Value | Fit | Rules / C# | MP risk | Effort | UI | Vanilla overlap | Main hook |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Signal kit + Governor | high | strong | rules (+generated gates) | L | M | UIToggle presets, lamp | primitives only | `PartNetworkValueReceiver.cs:87`, `R/mod/generated.actions.rules` |
| 2 | Demand ports | high | strong | C# | M | M | band, reserve, priority | crew supply chains | `R/belts/src/PartRoom.cs`, `IResourceSink.cs:175,192` |
| 3 | Dashboard | high (debug) | strong | C# client | L | M | overlay, labels | transfer lines | C: `SimOverlayRenderer.cs:1561` |
| 4 | Reserve floors | med-high | strong | rules / C# | M | M | reserve amount | limits (max only) | `ResourceManager.cs:32` |
| 5 | Mixed-cargo sensor | med | good | C# bridge | M | M | resource pick | none | C: `FlexResourceGrid.cs:1259`, `ResourceStorage.cs:42-61` |
| 6 | Jam-aware batching | med | good | rules + C# state | M | M | timeout, reason | output-full stop | C: `ResourceConverter.cs:340` |
| 7 | Salvage depot | med | good | C# | M | M | accepted types | crew salvage | `SalvageJob.cs:32` |
| 8 | Fire suppression | med | good | C# | M | M | coverage, supply | crew + extinguisher | `V/statuses/fire/fire.rules:55-63` |
| 9 | Logic editor | high ceiling | moderate | C# UI | M-H | L | full editor | none | C: `PartValueOperationRules.cs:19` |
| 10 | Overflow jettison | low | situational | C# | M | M | enable, exclusions | manual eject | C: `Airlock.cs:114` |

## 4. Recommended next 3 (agreed order)

1. **Brownout vertical slice, rules-only.** One capacitor -> existing cable bus -> band latch -> 2 vanilla factories with
   Essential / Normal / Optional UIToggle. Capacitor value is in battery cells, not percent
   (C: `V/ships/terran/power_storage/power_storage.rules:143-152`, `R/mod/capacitor.rules:7`). Verify: manual off still
   wins, residual buffer, idle drain, cable cut, empty channel, save/load, blueprint copy. Mass coverage via the generator
   only after this works.
2. **Demand port, one resource.** Target band + provider reserve + two competing requesters on existing reservations;
   ammo presets after.
3. **Dashboard for 1 + 2.** Stop reasons and real throughput; overlay never drives the sim.

## 5. Claude vs Codex

| Topic | Claude | Codex | Outcome |
| --- | --- | --- | --- |
| Signal net rules-only | yes | qualified yes; no for mixed cargo, generic neighbour actuation, editor UI | Codex limits adopted (§1) |
| Actuation | proxy beside target | proxy only reads; inject gate into target | gates via generated Add/Replace actions |
| Wiring | signals on power cables | agree for MVP; separate signal cable later for independent topology | cables first |
| Brownout / thermal / stock control | merge into Governor | agree | merged into #1 |
| First slice scope | gates on every consumer | one capacitor + few factories first | Codex scope |
| Ammo balancing | merge into demand ports | agree | merged into #2 |
| Fire | C#, direct status handling | C#, but via native `StatusResourceSink` | Codex method |
| Cargo sensor bridge | C# writes StaticValue or storage | StaticValue unusable; synthetic ResourceStorage only | prototype gate |
| Auto-repair | rejected | rejected only free regen; paid repair deferred | deferred |
| Doors | listed | disagree, weak value | dropped |
