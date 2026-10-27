# Changelog

All notable changes to Crewless Logistics (formerly Powergrid). Versions match `mod/mod.rules` `Version`.

## 0.3.5

- Fix: switching a hyperdrive onto the electrical network no longer drains the network into its battery. Like crew delivery in the base game, the network now charges a hyperdrive only while it spools for a jump (FtlDrive), also via engine rooms.

## 0.3.4

- Localization: German, Spanish, French, Japanese, Brazilian Portuguese and Simplified Chinese part names, descriptions and switch tooltips (`mod/strings`), using the game's own terms. Russian heat pipe term aligned with the game (теплопередатчик). The localization check now resolves every key in all eight game languages.
- Steam Workshop descriptions for all eight languages in `docs/workshop/`; bug reports go to GitHub issues.
- Public repository preparation: issue templates, `.gitignore` for source only, README/CHANGELOG split, no developer-specific default paths in scripts or generated catalogs.

## 0.3.3

Renamed to Crewless Logistics: mod ID `morgott.crewless_logistics`, install folder `Mods\CrewlessLogistics`. Old cable part IDs load through `OtherIDs`; electrical network switches on existing ships reset to off. Remove or disable the old Powergrid folder.

## 0.3.2

### Heat pipe durability
Cables and the heat/electric crossover no longer inherit armor stats (4000 HP, density 3, EMP absorption, armor category); they use vanilla heat pipe stats (1000 HP).

Cables use vanilla heat pipe stats (1000 HP) instead of armor stats.

## 0.3.1

### Baked cable sprites
Cable shaders are replaced by baked PNG sprites on vanilla layers (`doodads_low`, `doodads_high`, `lights_add`) with the same look; game updates can no longer break cable rendering through shader changes.

Cable shaders replaced by baked PNG sprites on vanilla layers; same look, no shader to break on game updates.

## 0.3.0

### Reactor-first supply
Reactors now supply consumers before capacitors (capacitor-first left full reactors idle while capacitors drained). Engine-room delivery respects each recipient's network switch. Ports are generated for every device, including reactors and the capacitor, with deterministic `CrewlessLogisticsPort_{Direction}_{x}_{y}` IDs (port components carry no save state). Generated actions are inherited by `mod.rules` as one list.

Reactor-first supply: full reactors no longer idle while capacitors drain. Engine-room delivery respects each recipient's switch. Electrical port IDs are now `CrewlessLogisticsPort_{Direction}_{x}_{y}` on every device. Port components carry no save state; network switches keep their remembered values.

## 0.2.12

### Portable Workshop compatibility
Seven optional compatibility actions previously contained absolute paths into the developer's Steam library. On another computer these actions could silently skip their targets, producing different simulation rules despite the same displayed mod version. Targets now use the game's supported relative file paths from `Data/cosmoteer.rules` to `../../../workshop/content/799600/`. Generation rejects nonstandard external mod locations instead of emitting machine-specific paths. All seven integrations and existing component IDs remain intact.

Cosmoteer compares game version, gameplay mod ID/version sets, then the portable hash of deserialized simulation rules. Native fixture checks reproduce the old applied/skipped patch mismatch and compare corrected actions across relocated Steam libraries. These checks do not constitute a successful live multiplayer join. Both players must replace their old Crewless Logistics folder with the same release archive and restart, with matching game and gameplay-mod versions. Standard Steam libraries are supported; manually relocated dependencies require a separate portable integration.

### Multiplayer / Сетевая игра
Version 0.2.12 removes machine-specific paths from the seven optional Workshop integrations. Both players must use the same archive, game version and gameplay mods, and restart after replacing Crewless Logistics. Matching displayed mod versions alone does not prove matching simulation rules. Standard Steam Workshop library layout is supported. Native portability/hash regression checks are separate from a successful live multiplayer connection.

В версии 0.2.12 убраны пути с компьютера разработчика из семи интеграций с Workshop. Оба игрока должны установить один и тот же архив, использовать одинаковые версии игры и игровых модов и перезапустить игру после замены Crewless Logistics. Одного совпадения номера версии в меню недостаточно. Поддерживается стандартное расположение Workshop в Steam-библиотеке. Автоматические проверки переносимости не заменяют проверку подключения в самой игре.

## 0.2.11

### Complete junction grilles and RU/EN
Three-way and four-way glass now fills every arm of the native grille, with opaque metal ribs oriented along each branch and diffuse green light beneath them. The unfinished gray side branches from 0.2.10 are removed. Checks cover all four three-way rotations, the four-way cross and healthy/damaged sprites; a negative control detects the previously omitted branch glass.

Part names, descriptions and network-switch tooltips use complete Russian and English string tables through the game's native language selection. Mod-list name and description are bilingual plain strings because native ModInfo does not expose localized metadata keys. The native parser resolves all eight UI keys independently in both languages, without requiring an English fallback. `artifacts/cable-junction-grille-preview.png` and `artifacts/whole-cell-native/localization-validation.json` record the checks; live rendering and language switching still require in-game confirmation.

### Junctions and languages
Three-way and four-way grilles now have glass and light beneath every arm, with metal ribs following each branch. No gray unfinished branch remains. Russian and English names, descriptions and switch tooltips follow the game language automatically. The mod-list name and description include both languages. Restart after installation; final rendering and language switching require an in-game check.

## 0.2.10

### Grated glass light
The previous flat T/plus-shaped lamps were rejected. The new inset follows the pipe with rounded glass, opaque metallic crossbars and green additive light beneath the grille. Branching pipes retain a single longitudinal light rather than a luminous branch symbol. The old thin axial stripe stays removed; tile geometry, end clamps, roofs, network behavior and engine-room delivery are unchanged. CPU previews and compilation with the game's bundled shader compiler are separate from live game rendering.

### Grated glass light
Rounded glass now sits under metallic crossbars, with diffuse green light beneath them. Junctions no longer use T/plus-shaped glowing panels. Native pipe geometry, plain hull roofs and power distribution are unchanged. Restart the game after installation; final appearance requires an in-game visual check.

## 0.2.9

### Rounded green lamps and engine-room supply
Glass lamps now have rounded ends and diffuse cell-green emission. The old thin axial highlight is replaced with adjacent native blue casing shading; metal end clamps remain unchanged.

Electrical input still fills the engine room's own physical reserve in whole cells. A native `ResourceConverter` now transfers one unbiased local cell to the existing engine-power buff recipients' whole-cell adapters. It retains one complete cell for the room, respects its electrical and native distribution switches, and leaves native crew delivery and overclock proxies unchanged. The wire path is unscaled: one source cell becomes one recipient cell. Recipient discovery uses the original `PowerDistributionBuffProvider`; no custom graph or allocator is installed.

Regression checks cover an empty room charged from all reactor sizes, room-to-engine transfer, fractional deficits, the local reserve, switches, two rooms plus direct engine wiring, and native recipient callbacks. Full live adjacency scanning, scheduler/crew behavior and remote rocket extension aliases remain unverified. `--inspect-save` in the development probe reads native ObjectBits save data into local artifacts without changing the user's save.

### Engine-room delivery and rounded lamps
Lamps have rounded ends and diffuse green light. The old thin stripe is removed from the blue casing.

The engine room now delivers whole cells from its local reserve to adjacent recipients recognized by its native power-distribution buff. Enable Electrical Network on the room and retain its ordinary distribution switch and operational requirements. The room keeps one whole cell for itself. Incoming power remains a local receiving endpoint; outgoing delivery uses native components with an unscaled one-cell-to-one-cell transfer, preserving energy when an engine also has a direct cable or a second supplying room. Native crew delivery and overclock paths remain intact. Full live scheduling, adjacency lifecycle and remote rocket extension arrangements still require in-game testing.

## 0.2.8

### Framed lamp instead of grille
The central grille interiors become continuous green glass lamps within the original metal frames. Without an available full source cell the glass is dark; native availability gates its additive illumination. The former exposed-groove glow and broad transverse reflection kernel are removed. Six fixed source-color probes select the original grille silhouette, including corner and branch layouts; no source PNGs or network mechanics change.

Both materials use the native `AtlasVert.SpriteUV` TEXCOORD6 field. A screen-derivative Jacobian reconstructs original source-pixel coordinates under UV rotation, mirroring, physical rotation and axial seam overlap. Native quad alignment checks supplement shader compilation and CPU composition; GPU drawing remains unverified.

### Lamps in the original grille frames
The grille interiors are now continuous glass lamps, with the original metal rim and pipe clamps retained. Glass stays dark without an available whole source cell and illuminates through the native availability toggle. Previous exposed-groove light and transverse flare are removed. Native sprite mapping keeps each lamp aligned with straight, angled and branching pipe shapes. The roof, placement previews, room-marker policy and network behavior remain unchanged. CPU previews and exact bundled shader compilation still require a live game visual check.

## 0.2.7

### Placement artwork and armored crossover
Adaptive placement previews now use the vanilla six-shape `BlueprintToggledBlendSprites` rules rather than transparent zero-area placeholders. The disconnected held-part state remains visible; all 16 connection states select original native blueprint artwork. Fixed crew/mixed crossing previews reuse their matching assembled cable images through the native `BlueprintSprite` component, replacing inherited plain armor or absent sprites. The mixed heat/electric crossing uses unmodified dark armor inside; the crew crossing retains its white walking floor and steps. Exterior roofs remain plain hull.

The neon has a broad six-pixel soft reflection around certified exposed groove emitters and a yellow-green core; the CPU comparison is `artifacts/cable-neon-comparison-preview.png`. Permanent room socket graphics are removed without changing any physical port record. Native placement markers remain. `tools/Render-PlacementPreview.ps1` displays the actual placement sprite export, and regression checks execute the disconnected held-part native quad path. Live game/GPU rendering remains unverified.

### Placement preview and dark mixed crossing
Adaptive cables reuse the native six-shape thermal placement preview, including visible disconnected placement and 16 connection states. Fixed cable previews use their matching cable art instead of blank or inherited armor squares. The mixed thermal/electric crossing has a native dark armor substrate; only the crew crossing retains the white floor/steps. Plain exterior roofs and independent networks are unchanged.

Exposed glass now has a broad six-source-pixel soft halo and a bright yellow-green core. Direct emission stays off metal grilles and clamps. Permanent green room dots are removed; physical ports, doors, and native placement overlays retain their existing behavior. Live GPU appearance still requires in-game verification.

## 0.2.6

### Bundled shader compiler crash fix
The 0.2.5 glow shader failed at game startup with `X4014: cannot have divergent gradient operations inside flow control`. Offline checks had silently used the newer Windows compiler; the game loads `Bin/d3dcompiler_47.dll` version 6.3.9600.16384. Shader verification now binds the exact game DLL exports by absolute path, records its version/hash, and reproduces the old divergent-gradient failure as a permanent negative control. Both materials evaluate texture samples in uniform control flow; masks and additive emission remain unchanged. This verifies compilation, not live rendering.

### Startup shader correction
Removed pixel-varying texture-sampling branches rejected by Cosmoteer's bundled shader compiler with X4014. Offline validation now uses that exact compiler DLL, rather than the newer system DLL, and retains a divergent-gradient regression fixture. Geometry, networks and glow masks remain unchanged. Game startup/rendering still requires a live check.

## 0.2.5

### Connected crossover and additive glass
The mixed crossover also appears in the native `Thermal` category. The orange pipe is now above the blue cable, using a dedicated native layer that occludes electrical emission. Native crossing floor/frame art replaces the dark armor substrate. A two-source-pixel axial overlap moves the original sprite's transparent end rows onto adjacent cylinders, covering seams without changing physical ports or tile size. Connected orange diagnostic routes are restored through the crossover.

The electric groove has a dark green glass baseline and a source-gated HDR core/local halo using the same additive blend as vanilla `lights_add`. Hardware interiors remain excluded from the emissive mask. Original game PNG files remain unchanged; CPU composition and native shader compilation remain separate from live GPU proof.

### Crossover and luminous glass correction
The mixed crossover is also available in the native Thermal category. Its interior uses the original crossing floor/frame at native brightness, and the orange thermal cylinder renders above the blue cable and its emission. Crossing cylinders retain native widths and damage sprites with a two-source-pixel axial overlap at each tile seam. The thermal connection indicator remains native and connected through the part. The electrical groove uses dark green glass with native additive emission instead of a flat alpha-colored line; metal clamps and grilles remain opaque. Exterior roofs remain plain hull.

CPU preview and native component checks do not establish live GPU bloom, atlas filtering or game scheduling.

## 0.2.4

### Crossing and masked neon correction
The crew crossing now uses the exact native crossing floor, walls/frame, location and three damage sprites. Its transparent machinery renders above walls/doors like the native thermal crossing, while the adaptive opaque floor stays in its low layer. This restores the visible floor boundary and the matching cylinder ends without changing tile size or ports. Exterior roofs remain plain hull.

Continuous rectangle traces are removed. A source-gated native sprite component renders a shader mask over the same original geometry, selecting exposed axial highlights surrounded by copper and rejecting metallic grilles/clamps. Sprite geometry, rotation, damage selection and source availability remain native. The unpowered branch clears its quads through the actual native component lifecycle. The material and glow shaders compile for all native profiles; previews remain CPU approximations requiring a live GPU check.

The 0.2.4 build also adds an armored orthogonal thermal/electric crossover. Original vanilla up/down thermal ports and their node are retained; left/right electrical ports use an independent native physical node. Rotate the part to swap axes. It blocks crew and retains plain armor roof graphics. Reactors now relay the electrical network through every socket without receiving cells from other sources or changing generation.

All three part buttons remain directly in the native electricity (`Power`) category. Parent/popout sections are disabled. The ordinary cable icon shows a vertical pipe. Every cable/crossover recipe is `[[coil,4],[steel,2]]`.

### Visual corrections
The white-floor crossing retains the exact vanilla thermal crossing floor, surrounding walls/frame and cylinder sprites. Its machinery draws at the native high stage so walls do not cover the pipe ends. The low layer for opaque adaptive floor sprites stays separate. Powered indication uses the original pipe geometry with an exposed-groove mask; no continuous green rectangle is drawn over clamps or grilles. Native availability switches the glow off when no whole source cell is accessible. Exterior appearance remains plain vanilla hull.

### Reactor transit and thermal/electric crossover
The new armored crossover passes native heat up/down and electricity left/right. Rotate it to exchange axes. The networks share neither a routing node nor resource storage. Crew passage is blocked and the roof remains plain armor. Reactor transit requires the remembered Electrical Network switch, independently of generation operation; reactors still do not charge one another.

All three part buttons are directly in the standard electricity category, without a custom or parent section. The main cable icon depicts a vertical cylinder. Each recipe costs **4 coils and 2 steel**.

## 0.2.3

### Appearance correction
The broad flat metal/glass rectangles and diagnostic route overlays have been replaced by the original detailed adaptive pipe sprites. Native 16-state sprite selection preserves the six source shapes and their exact tile-edge endpoints. A dedicated native ship atlas material recolors warm pipe surfaces navy/silver without changing the source PNGs or other parts' materials. The thin source indication follows native diagonal corners. Both cable styles retain plain vanilla roof textures, with no exterior pipe art or green lights.

`tools/Test-CableShader.ps1` compiles/reflection-checks the material with the native Windows compiler for all three game shader profiles and rejects an invalid-HLSL negative control. CPU previews also emulate the color transform; they are not live GPU proof. Raster-generation trials failed geometry acceptance and are retained only as development artifacts.
