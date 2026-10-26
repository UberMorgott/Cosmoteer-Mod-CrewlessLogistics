# Expanded 0.2.0 native checks

The current host applies 55 real native mod actions, validates all 47 consumer adapters and 217 sockets (201 existing thermal positions plus 16 new service sockets), parses the three reactor/capacitor rules, and exercises native source queries and directional routing. Additional partial fixtures cover cable voltage and cut/reconnect, exact green glass tile extents, capacitor charging/supply priorities, normal/overclock reserve proxies, isolated capacitors, and gated electrical delivery heat.

Native per-device physical battery fixtures preserve original IDs and capacities but omit media effects because graphics/audio factories require full game startup. Proxy membership is explicitly supplied for isolated consumer tests; source/cable topology tests use real spatial discovery. Converter ticks are stepped manually. No live game, renderer, actual overclock controller, save/load, ship-split lifecycle or multiplayer is exercised. Fresh counts and scenario results are recorded by `Run-Probe.ps1` in `artifacts/whole-cell-native/result.json` and the coverage reports.

The following sections describe the original focused spike and earlier count snapshots.
# Whole-cell native component spike

- Status: passed as an isolated native-component prototype on 2026-10-25. This is not an installable Crewless Logistics mod or a live-game test.
- Result: whole standard cells can be transferred by existing rule-defined components using the vanilla network distributor, without a Crewless Logistics gameplay DLL or a custom graph, allocation algorithm, or fairness queue.
- Network accounting uses one unit per cell. Real reactor and consumer storages retain vanilla units and fractional consumption; native boundary conversion maps one network cell to 1000 internal battery units.

## Run

```powershell
& .\prototypes\whole-cell-native\Run-Probe.ps1
```

- Requires the existing .NET 8 SDK and local Cosmoteer installation. The runner copies the game's runtime/assemblies into ignored `artifacts/whole-cell-native/runtime` and runs the fixture on the game's actual .NET 10 runtime.
- It does not install or enable a mod, install a loader, change the game folder, attach to the running game, or open saves.
- `components.rules` is parsed by the native ObjectText serializer. The C# executable supplies synthetic ownership and manual pulses. Original whole-cell tests seed connected membership; the shared-port tests use native spatial discovery and topology without seeded membership. This executable is a test harness, not code intended to load into the game.
- Detailed results are written to `artifacts/whole-cell-native/result.json`, including a verified match between loaded and installed game assembly hashes.

## Native component composition

- Source: original `battery` storage -> `InlineResourceConverter` with `FromQuantity = 1000`, `ToQuantity = 1` -> provide-only `NetworkResourceStore`.
- Receiver: original `battery` storage plus a full 999-unit native `fake` tracking storage -> `MultiResourceStorage` with real battery first -> `InlineResourceConverter` with the same 1000:1 mapping -> receive-only `NetworkResourceStore`.
- Network: `NetworkResourceOutput` -> ordinary `ResourceConverter` transferring exactly one unit -> `NetworkResourceInput`. Both IO components use the vanilla `DistributeProportionallyByAvailable` mode.
- All cell-unit views use the existing hidden `fake` resource inside the dedicated network, rather than changing `battery` accounting or defining a new resource. Source and receiver categories remain distinct.
- For actual receiver content `R` and capacity `M` divisible by 1000, the receiving view exposes `ceil(R / 1000)` occupied slots and `M / 1000` total slots. Free slots are exactly `floor((M - R) / 1000)`.
- The 999-unit tracking storage is not energy, is never a supply source, and remains full. Its rules disable draining, drops and resource death/response effects. The test separately asserts it never changes and checks native tracked totals as well as real energy.
- A one-cell converter requires one complete source cell and one complete destination slot. Source rounding is applied per source before native allocation, so two source half-cells cannot combine into a cell.

## Verification

- Negative control: a plain receiver converter with no rounding correction advertises a false slot at 3500/4000 and loses 500 units during transfer. The fixture reproduces and detects this failure.
- Exhaustive source levels: all 8001 values from 0 through 8000 expose the correct whole-cell count.
- Exhaustive receiver levels: all 4001 values from 0 through 4000 expose the correct full-cell free capacity; tracking state stays constant.
- Native IO matrix: 154 combinations around partial, empty, full and exact-cell boundaries.
- Explicit circuits: half-cell receiver deficits wait; two receiver half-deficits do not combine; two source half-cells do not combine; one cell across five empty targets goes to the first native entry; five cells across ten one-cell targets yield five full-cell deliveries.
- Odd-count batches use the native allocator: three cells in one call produce `[2,1]` for two equal receivers; five produce `[3,2]`. Three separate one-cell calls produce `[3,0]`, demonstrating native rounding and packet-size dependence.
- Capacitor isolation uses native category filters over the same connected candidates. Charge queries see reactor sources and capacitor receivers; supply queries see capacitor sources and consumer receivers. With one stored cell and a full consumer, 100 idle cycles retain capacitor contents `[1,0]`; later charging fills the other capacitor and later consumption draws a full cell to the consumer only.
- Additional randomized exercise: 3000 native conversion pulses, with simulated fractional stock changes applied through native storage APIs between pulses. These are not actual reactor generation or weapon-consumer updates.
- Each successful-path pulse checks exact physical energy conservation, per-storage changes divisible by 1000, capacity bounds, unchanged tracking storage, and unchanged native tracked resource totals.
- Latest run including shared-port, package and device-switch tests: 81,987 assertions passed; zero build warnings or errors. Independent read-only reviews covered the fixtures and package within their documented boundaries.

## Installable package checks

- `PackageProbe.cs` opens every package rules file with the native ObjectText parser and applies six native mod actions to an in-memory vanilla rules tree.
- It reads 33 selected component rules, resolves both cable IDs through the modified registry and compares all eleven device socket positions/directions with vanilla. Device ports require their own `CrewlessLogisticsToggle` in physical/editor mode; passive cables have no such gate. Native compatibility rejects thermal pipe pairing.
- `NetworkSwitchProbe.cs` runs actual native UIToggle Mode setters/events against package ports, IO and receiver components, testing default-off, independent switching, retained energy, whole-cell refill and empty-consumer bootstrap. It bypasses remembered-state metadata in temporary fixture objects and resource-catalog/crew registration; these remain live integration checks.
- GUI definition fields and icon paths are read through the native parser. Full GUI texture construction requires game graphics startup and is not claimed by this fixture.
- It checks the reactor still produces into its original `BatteryStorage` and the laser retains 4000 internal battery units of capacity.
- Preview/redprint `SituationToggles` must point to actual native ports with blueprint and destroyed components. Runtime-only `MultiToggle` objects are used solely by physical adaptive graphics.
- Electrical overlay rules must select the static capped channel material, cyan line/caps, a neutral texture tinted cyan for the connected marker, and connected-only routes. Cable electrical icons and inherited thermal overlays are hidden. These assertions verify configuration, not rendered pixels.
- Physical routing nodes must have `HasPhysicalComponent = true`. The 0.1.0 package omitted `IncludeInPhysical = true`, and the user hit `No component named 'CrewlessLogisticsNode'` on placement. The old shared-port fixture called `CreateComponent` directly and bypassed the native construction filter; it now checks the flag too.
- The added construction regression selects actual package components using the native physical-inclusion property, creates all selected routing/toggle components, and invokes native `OnAddedToPart` with inactive synthetic ownership. Both cable variants initialize fourteen such components. Before the package fix this reproduced the exact game exception; 0.1.1 passes. This covers non-rendering dependency linking, not the full part constructor, rendering or live placement.
- Additional evidence: `artifacts/whole-cell-native/package-validation.json`. This check does not start the game or install the package; use the separate `Install-Mod.ps1` to install it.

## Shared physical thermal/electrical connector

- Result: two distinct native `NetworkPort` components can share the same `Location` and `Direction` while connecting independently. Reuse one visible connector location, with independent `thermal` and `morgott.crewless_logistics.electric` port types and route nodes. One port component with both labels is not the tested composition.
- `SharedPortProbe.cs` builds a three-part, 1x1-per-part fixture: source, dual-channel cable and destination. The real `ShipPartNetworkManager`, `PortHandler`, native spatial port geometry, subnetworks and IO queries discover all connectivity. No candidate members or route edges are supplied by the harness.
- Checked all four rotations, with and without mirroring, and two per-part port registration orders: 16 successful cases.
- The source thermal port copies vanilla thermal types, output-only routing and overclock gating. A native `DebugToggle` provides the off/on signal; the electrical port has no thermal operational or blueprint toggle inheritance.
- With the thermal toggle off, native electrical capacity is discovered and exactly 1000 battery units transfer; no heat transfers. Turning the signal on connects heat without changing the electrical peer. Each channel then transfers independently with exact expected source/destination balances.
- Turning heat off again removes only its connection; another whole electrical cell still transfers. Re-enabling heat reconnects it. Deregistering the cable's electrical output port removes electrical capacity while heat still transfers; native re-registration restores electrical delivery.
- Each case starts with 4500 battery units and 23 heat units. It ends with batteries `[500,4000]`, heat `[21,2]`, and unchanged 999-unit receiver bias. Thermal and electrical nodes remain in separate subnetworks, and exact peer/capacity assertions guard against cross-pairing.
- Negative control: an electrical port that incorrectly inherits the thermal operational toggle cannot connect or transfer with overclock off. This reproduces the configuration error the separate electrical component avoids.
- The cable fixture exposes both independent channels at the shared edge. Carrying both simultaneously through one adjacent tile requires a part with both channels; this does not allow two ordinary cable parts to occupy the same tile.
- Limits: synthetic 1x1 parts, manually invoked converter ticks and a native debug toggle rather than the actual overclock controller. Graphics, blueprint port picking/overlays, complete vanilla-part integration and live simulation are not exercised.

## Limits and next integration check

- Original whole-cell cases supply connected members to native query objects. Shared-port cases additionally exercise physical port discovery, native graph/query rebuilding, port operational callbacks and disconnect/reconnect. Full part construction/destruction and ship splitting remain untested.
- Native conversion ticks are called manually. Automatic update timing, throughput, immediate demand response and complete source/consumer/capacitor priority scheduling are not proved.
- Receiver capacity is fixed and divisible by 1000. Actual capacitor overclock proxies and arbitrary modded capacities need additional integration tests.
- Actual reactor production, crew-delivery events, capacitor heat costs, save/load and multiplayer are not tested.
- Shared-connector rendering, blueprint interaction and actual overclock-mode GUI/controller behavior are not tested.
- The next step is a minimal ordinary `.rules` mod circuit with physical ports and native automatic updates. This spike provides no evidence that YAML is required for whole-cell transfer; it also does not claim the full mod is complete.

