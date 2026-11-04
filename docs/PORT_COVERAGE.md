# Crewless Logistics device and port coverage

## Current attachment policy

Electrical cables may connect to every eligible physical perimeter edge of participating equipment. Coverage excludes adjacent cells reserved inside the normal part size and cells covered by native prohibited-build rectangles for the cable's prohibition category. Directional restrictions are expanded from `Size` exactly as the native `PartRules` constructor does. Rotations and mirroring remain native.

`belts/src/PortGeometry.cs` contains the geometry (regression cases in `belts/tests/CoverageTests.cs`); the prohibition categories are the cable part's `ProhibitedBy` (native default `default`). Existing thermal ports can occupy the same physical edge: electrical and thermal port types remain independent. Port geometry is never shifted to compensate for the editor's diagram layout.

## Discovery

`CrewlessLogistics.Belts.dll` (`belts/src/NetworkCoverage.cs`) postfixes the private `Cosmoteer.Data.Assets.ApplyPreLoadMods(OTFile)`: it runs once per game load after every enabled mod's native actions and before `Read<Rules>`. It reads each registered part through native ObjectText resolution (inheritance context kept), classifies it structurally (`belts/src/DeviceClassifier.cs`, no part IDs) and adds the adapter template as an inheritance base of the part's own Components group, the same edit `ModAddBaseAction` makes, plus local override and socket nodes. Every plan is built and its text parsed before the first edit; parts are processed in registry order, so all multiplayer clients with the same mods produce identical rules.

- Consumer: one battery `ResourceConsumer` resolving to one local physical battery store (whole cells). Rocket-style aggregates collapse to their one direct reserve.
- Engine room: a `GridBuffProvider` with `BuffType = EnginePowerDistribution` and a battery aggregate reaching it through `ViaBuffs`.
- Reactor: `reactor` category with a battery-producing converter into a verified local store.
- Capacitor: the vanilla Power Capacitor contract only (`ConsumptionToggle`, `BatteryReceiverStorage`, `IsOverclocked`, `Overclock_DeliveryHeatProducer`); other `provides_power` parts are skipped.

Anything else with battery components is skipped with its reason in `Logs\CrewlessLogistics.Belts.log`. A registered part inheriting another covered part gets its own complete adapter locally, and inherited adapter children it must not have are voided (native `Name;` nodes, skipped by `PartRules`). Parts already holding `CrewlessLogistics*` components are left alone, which makes the hook idempotent.

`belts/tests/coverage-baseline.tsv` is the resolved-component fingerprint of the former generated rules (vanilla plus Diagonal Thruster Plus and Diagonal Cockpits); the hook reproduces it with zero differences. `prototypes/whole-cell-native/Run-Probe.ps1 -Catalog` still writes the declarative `docs/device-catalog.json` snapshot.

## Storage constraints

A standard electrical endpoint must resolve to a native `battery` reserve or a validated unscaled receiver. EMP drain components alone do not make armor an electrical consumer.

- Engine room: target the local physical reserve; preserve the separate scaled crew-distribution path.
- Rocket modules: target unique local physical capacity, not overlapping neighboring aggregate aliases.
- Boost thruster: preserve its native gated charging proxy.
- Capacitor: use the validated unscaled normal/reserve receiver, preserve crew conversion, and apply network-charge heat only during native overclock.
- Reactors: preserve generation and withdraw whole cells from actual reserves.

## Verification limits

Native checks validate the generated adapters, port rules, storage boundaries, source priorities and energy conservation without a custom gameplay distributor. Geometry tests cover reserved/prohibited cells, categories and rotated/mirrored cases. Native CPU overlay helpers verify exact primary placement coordinates.

Complete GPU appearance, game scheduling, persistence, destruction, ship splitting, multiplayer and DLL interactions remain live-test requirements. The device electrical overlay hides the vanilla-shifted compatible markers and internal spokes; exact native primary attachment markers remain during cable placement. No permanent green socket graphics are added to rooms. Eligible edges are selected by the perimeter/prohibition policy, independently of doors.

## Native transit and orthogonal crossover

Crewless Logistics 0.2.4 uses a physical native `CrewlessLogisticsNode` in all three reactor sizes, with an electrical-switch-gated Omni router. Ports share that bus; native producing-only source stores and generation remain unchanged. The specialized heat/electric crossover keeps original native thermal up/down ports/node separate from its left/right electrical basic node. Transform/transfer regressions cover rotations and mirroring; original device perimeter policy is unchanged.

The 0.2.9 engine-room adapter keeps all existing ports and the local receiving endpoint. Separate native whole-cell delivery draws from that local battery and uses the original engine-power buff provider to discover recipient adapters. No overlapping aggregate recipient capacity is advertised to the electrical network. Crew storage/proxies, device doors and all perimeter coordinates are unchanged.
