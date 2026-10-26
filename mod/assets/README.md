# Electrical network button glyph

- File: `network_toggle.png`.
- Generated with the built-in image generation tool; no CLI/API fallback.
- Selected output: `exec-ad980d54-e80d-446e-b425-39c6a289ebfc.png`.
- White glyph on transparency, tinted grey/off and cyan/on by the native game UI, displayed at 64x64.
- Concept: one male plug with two protruding contacts and one female socket with recessed holes, facing each other along a diagonal. The earlier two-male-plug version was rejected.
- Final prompt: Create an electrical connect/disconnect toggle UI icon, white solid glyph on transparent background. EXACT physical design: ONE MALE PLUG in bottom-left, with exactly TWO metal prongs protruding toward upper-right into a small clear central gap. ONE FEMALE SOCKET in upper-right facing the male plug; its facing edge is FLAT and has TWO RECESSED DARK/TRANSPARENT HOLES aligned to accept those prongs. THE FEMALE SOCKET HAS ZERO PROTRUDING PRONGS OR PINS. Each body is a rounded semicircle and has one short cable stub outward. Their axes align diagonally and the halves could slide straight together and connect correctly. Minimal clean vector pictogram, compact square, smooth sharp edges, all foreground white, generous transparent border. No texture/no speckles/no glow/no frame/no text/no watermark. Show one plug and one socket, never two male plugs.

Since 0.3.1, `cable_body_*`, `cable_lamp_*` and `cable_crossing_{body,lamp}*` are baked by `tools/Bake-CableSprites.ps1` from the vanilla adaptive/crossing pipe PNGs (mechanical recolour and lamp-mask extraction, no drawn art). Lamp RGB holds a quarter of the emission; the rules restore it with `VertexColor = [1020, 1020, 1020, 255]` on `lights_add`. The shader sections below are history.

## Code-native cable materials and icons

- `cable_icon.png`, `cable_crossing_icon.png`: rendered at 64x64 by the former `tools/Render-CablePreview.ps1 -ExportIcons` (removed in 0.3.1), from actual native-resolved sprite geometry in `artifacts/cable-appearance-native.json`. These reuse the game's armor/floor textures and the same code-native casing, glass and steps as the part rules.
- Cable/icon rendering does not use image-generation fallback or manually edit user-provided images. `network_toggle.png` remains the built-in image-generation asset documented above.

## 0.2.3 native material

- `cable_material.shader` uses the game's `base_atlas.shader` custom-pixel hook, retaining native vertex animation, alpha and vertex-color handling.
- Only the dedicated `CrewlessLogisticsCableMaterial` render layer uses this transform. Original vanilla PNGs remain unchanged. Roof graphics retain original armor/corridor materials and contain no conduit overlays.
- Six adaptive floor shapes and crossing doodads come directly from `Data/ships/terran/heat_pipe_adaptive` and `heat_pipe_crossing`.
- The previous broad rectangle artwork is replaced by detailed source sprites.
- Seven built-in image-generation recolor trials were rejected because they changed connection geometry or baked in energized light. They are development-only under `artifacts/rejected-cable-art-0.2.3`; none is referenced by the installed package.

## 0.2.4 masked neon

`cable_glow.shader` samples unchanged original sprite pixels and classifies exposed axial highlights by surrounding copper colors. The same native shape/damage sprites supply base art and glow geometry. A separate high-stage material draws transparent crossing machinery above native walls; the opaque adaptive floor material remains low. Native `OperationalToggle` gates the glow sprite component using `CrewlessLogisticsAvailable`. No source PNGs are modified, no generated raster trials are installed, and no glow/cable art is attached to the roof.

## 0.2.5 additive luminous glass

The base material darkens the same exposed groove mask to green glass. The glow material uses the exact vanilla `lights_add` blend with an HDR core and a two-pixel local halo restricted to exposed copper. Grille and clamp interiors remain excluded. The crossover's orange cylinder uses a native material at bucket 175, above blue machinery at 125 and electrical emission at 150; native thermal light remains at 200. Crossing cylinders have a two-source-pixel axial overlap at each seam. Original texture files and plain exterior roofs remain unchanged. CPU previews emulate additive composition; live GPU atlas sampling and bloom remain unverified.

## 0.2.7 visible halo and placement sprites

The glow now searches six source pixels around the original exposed groove, certifies the emitter, and adds broad Gaussian green reflection with a yellow-green core. Direct light remains excluded from metal hardware. The kernel uses uniform texture sampling and is compiled by the exact bundled game compiler. Native adaptive placement sprites are referenced unchanged; fixed crew/mixed placement previews reuse their generated cable icons through native `BlueprintSprite` rules. The mixed crossing background is native dark armor at original brightness.

## 0.2.8 central glass lamp

The lamp replaces only original grille interiors, preserving their metal rims. Source-pixel coordinates are reconstructed from native TEXCOORD6 and atlas UV derivatives, accounting for native UV rotations and mirroring. Fixed source probes identify the existing native grille layouts; procedural glass and additive light use those silhouettes. The previous groove-emitter mask and transverse halo search are removed. Original PNGs, pipe geometry and plain roofs remain unchanged. Native shader/compiler and CPU image checks are not live GPU proof.

In 0.2.9 the lamp silhouette has rounded ends and diffuse green emission. The old axial highlight is removed by sampling adjacent casing colors and applying the same blue material transform. End-clamp pixels remain unchanged. Shader source mapping and native availability gating are retained.

In 0.2.10 the glass is covered by metallic crossbars. Additive emission comes from the gaps beneath the grille and softly lights nearby casing. A junction has one longitudinal inset, rather than a luminous T or plus. Original source PNGs, native quad mapping, availability gates and plain exterior roofs remain unchanged.

In 0.2.11 the full native junction grille is covered by glass and light, including each side arm. Metal ribs follow the selected arm's axis and remain opaque. The 0.2.10 single-inset policy left gray unfinished branches and is superseded. Straight, corner, stub and hub silhouettes retain their existing glass and ribs.
