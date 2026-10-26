# Crewless Logistics device and port coverage

## Current attachment policy

Electrical cables may connect to every eligible physical perimeter edge of participating equipment. The generator excludes adjacent cells reserved inside the normal part size and cells covered by native prohibited-build rectangles for the cable's prohibition category. Directional restrictions are expanded from `Size` exactly as the native `PartRules` constructor does. Rotations and mirroring remain native.

`tools/Generate-PerimeterPorts.ps1` contains the shared geometry function and regression cases. Existing thermal ports can occupy the same physical edge: electrical and thermal port types remain independent. Port geometry is never shifted to compensate for the editor's diagram layout.

## Discovery

`prototypes/whole-cell-native/Run-Probe.ps1 -Catalog` uses the installed game's ObjectText parser and native mod actions to inspect the final registered parts after enabled declarative mods. It does not load mod DLLs or start the game. `docs/device-catalog.json` records source/mod ownership, geometry, storage graph, classification and unsupported reasons.

The inspected registry contains 106 parts: 96 vanilla and 10 added by enabled mods. Electrical scope includes 47 vanilla consumers, three reactors, one capacitor and seven added consumers. The latter are four `evans.diagonal_cockpits` control rooms and three `Diagonalworks.DiagonalThrusterPlus` thrusters. Three `rustydios.chainguncornermag` parts are ammunition magazines and receive no electrical adapter.

The compatibility generator creates ordinary native rules. Rebuild with `tools/Update-DeviceCompatibility.ps1 -Install` after enabled mods, versions or paths change. Declarative discovery cannot cover arbitrary future DLL-created devices or unknown custom resource/storage semantics automatically.

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
