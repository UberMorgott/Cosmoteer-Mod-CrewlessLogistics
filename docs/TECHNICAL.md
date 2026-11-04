# Technical notes

How Crewless Logistics works internally and how it is verified. User-facing overview: `README.md`; history: `CHANGELOG.md`.

Native electrical networks for Cosmoteer 0.30.5. The energy network is ordinary `.rules`; the only DLL is the belt sim `CrewlessLogistics.Belts.dll` ([`BELTS_DESIGN.md`](BELTS_DESIGN.md)), loaded by Yet Another Mod Loader (Workshop 3577650065).

## Behavior

- One network unit is one complete battery cell. The boundary adapter uses the game's 1000 internal battery units; fractional deficits wait until a whole cell fits.
- Native network IO distributes energy. Existing generation, consumption, crew delivery and doors remain native.
- Every participating reactor, capacitor and consumer needs its remembered **Electrical Network** switch enabled. It defaults off and is independent of thermal overclock.
- Consumers receive energy before capacitor charging. Reactors supply consumers first; capacitors supply only while no reachable reactor holds a complete cell, so full reactors keep generating. Native filters prevent reactor-to-reactor and capacitor-to-capacitor charging.
- An engine room with Electrical Network on delivers cells only to recipients whose own switch is on.
- Capacitors and reactors relay the network through native bidirectional buses, including empty reactors with generation off; the electrical switch gates transit.
- Two ordinary cable styles (black armor and white crew crossing) plus a separate orthogonal heat/electric crossover. All three appear directly in the native Power category and cost four coils plus two steel. Cable health, density and penetration equal the vanilla heat pipe counterparts (1000 HP); the armor look is cosmetic only.
- Detailed native pipe geometry with navy/silver casing. Rounded green glass lamps replace the grille interiors; the old axial stripe is removed. Lamps indicate an available complete source cell without consuming energy. Exterior roofs remain plain vanilla hull.

Cable art is plain PNG sprites on vanilla render layers, with no custom shader. `tools/Bake-CableSprites.ps1` recolours the vanilla heat-pipe sprites (every adaptive shape, damage frame and the crossing cylinder) and extracts the lamp light into `mod/assets`; dark glass is part of the body art. The lamp is a native `lights_add` sprite switched by `OperationalToggle = CrewlessLogisticsAvailable`. Rerun the script after a game update changes the vanilla pipe art.

## Device discovery and ports

Installation-time discovery reads the native part registry after the enabled mods' declarative actions. The shared generator adds electrical ports to permitted physical perimeter edges, excluding reserved barrel/exhaust cells and matching native prohibited-build zones. Existing thermal attachment locations can also carry an independent electrical port.

The current catalog contains the 96 vanilla parts plus 10 modded parts. It finds all 47 vanilla consumers, three reactors, the capacitor, and seven added standard-battery consumers: four diagonal control rooms and three diagonal thrusters. The other three added parts are ammunition magazines.

Discovery runs in the game, once per load: `CrewlessLogistics.Belts.dll` postfixes the private `Assets.ApplyPreLoadMods(OTFile)`, which runs after every enabled mod's native actions and before the rules are read, and adds the adapters to each registered part's own Components overlay (`belts/src/NetworkCoverage.cs`, see `docs/PORT_COVERAGE.md`). Without the DLL (loader missing, game update breaking a patch target) devices get no sockets; the log says so. Custom DLL-created parts, custom resources and unknown storage transformations are skipped with a logged reason.

## Build, verify and install

```powershell
dotnet test; & .\Install-Mod.ps1
```

`dotnet test` includes the coverage fixture and the equivalence check against `belts/tests/coverage-baseline.tsv`. The installer builds the Release DLL, backs up the previous Crewless Logistics directory and checks copied files by SHA-256 (the `.workshop` item link is kept). Running processes, saves and unrelated mods are preserved. Save your session and reload the game yourself to load the update.

## Verification boundaries

The isolated host invokes the installed game's parser, mod actions, storage, queries, converters, integer distributor and topology code. Regression cases cover whole-cell conservation, consumer/source priorities, inline capacitors, source indication, independent heat/electric networks, preview quads, perimeter geometry and native overlay placement helpers. Evidence is written under `artifacts/whole-cell-native/`; perimeter geometry and device coverage are tested in `belts/tests` (`CoverageTests`, `GameTests.NetworkCoverageMatchesBaseline`).

Ownership and lifecycle are synthetic and converter scheduling is stepped manually. Media factories are omitted from storage fixtures. Full GPU rendering, live scheduling, save/load, destruction, ship splitting, multiplayer and other mods' DLL behavior still need in-game testing.

The vanilla editor shifts independent network diagrams inside the same device by one-sixth of a tile. Device electrical overlays therefore retain exact primary placement markers and suppress the shifted compatible-device icons/internal spokes. Physical sockets and cable artwork remain; diagnostic cable routes are hidden. This is not a replacement renderer for a complete aligned dual-network diagram.

See `mod/README.md`, `docs/PORT_COVERAGE.md` and the catalog snapshot `docs/device-catalog.json` (from `prototypes/whole-cell-native/Run-Probe.ps1 -Catalog`) for details. Native research and regression hosts are development tools and are not installed in the gameplay package.
