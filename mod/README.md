# Crewless Logistics 0.3.5

Ordinary Cosmoteer `.rules` mod for game 0.30.5. No gameplay DLL or mod loader required.

## Devices

- Enable **Electrical Network** on each participating reactor, capacitor and consumer. The remembered switch defaults off. Thermal overclock is separate.
- All 47 vanilla powered consumers, three reactor sizes and the capacitor are integrated.
- Generated compatibility also covers standard-battery consumers from the enabled mods inspected when the package was built. This build includes four diagonal control rooms and three diagonal thrusters.
- Electrical ports follow permitted physical perimeter edges. Native prohibited construction zones and reserved barrel/exhaust cells are excluded. Original doors and crew access are preserved.
- Consumers are receiving terminals. Capacitors and reactors relay the electrical network through native bidirectional buses. An empty reactor with generation off can relay energy when its electrical switch is on; it still cannot receive cells into its reserve.

## Cables

- Armored cable: black vanilla armor, four automatic connections, vanilla heat pipe durability (1000 HP, armor look only), no crew passage. Cost: 4 coils and 2 steel.
- Crew crossing: white floor with native metal fins and crew passage, two opposite cable ports, perpendicular doors and native 80% crew speed. Rotate to change cable direction.
- Detailed navy cylinders and metallic clamps reuse the original vanilla pipe geometry. Rounded green glass sits beneath a metal grille; the old thin axial stripe is removed. Metal end clamps remain unlit. Exterior roofs show plain vanilla hull.
- The glass lamps light when an enabled connected reactor or capacitor exposes at least one complete standard cell. This availability query does not consume energy.
- Cable art is plain PNG sprites baked from the vanilla pipe art, drawn on vanilla render layers with no custom shader. The lamp is a native additive light sprite switched by source availability.
- Thermal pipes and electrical cables remain separate networks. Two ordinary cable styles and one independent thermal/electrical crossover are registered.

## Energy

- One network unit is one whole cell. Vanilla storage still uses 1000 internal units per cell.
- A partial deficit waits until an entire cell fits. Native integer IO selects recipients and accounts for each withdrawn and delivered cell.
- Consumers fill before capacitors. Reactors supply consumers first; capacitors supply only while no reachable reactor holds a whole cell. Reactor-to-reactor and capacitor-to-capacitor charging are filtered out.
- Engine-room delivery skips recipients whose Electrical Network switch is off.
- Rocket module aliases and scaled engine-room crew distribution are not exposed as overlapping or multiplied capacity. Boost charging lockout and original crew paths remain native.

## Loading and testing

After updating, save your session and reload the game. Enable Electrical Network on every device in the test circuit. Isolate crew battery delivery when checking the network itself.

Test a reactor and several consumers, then place one or two capacitors inline. Check continued consumer supply, capacitor recharge, reactor disconnection, cable cut/reconnect, rotated crossings, both floor/roof views, and an independent thermal branch.

The developer build command `tools/Update-DeviceCompatibility.ps1 -Install` reads enabled mod rules, regenerates compatibility, verifies it, and backs up/replaces only Crewless Logistics. Repeat it when enabled mods, versions or paths change. The installed package performs no runtime discovery. DLL-created parts, custom energy resources and unknown storage behavior require separate compatibility work.

Native regression checks exercise real parser, storage, IO, topology and CPU preview helpers using synthetic ownership and manually stepped scheduling. They do not establish full game rendering, scheduling, persistence, destruction or multiplayer behavior.

The editor's shifted internal electrical spokes and compatible-device markers are suppressed. Exact native primary placement markers and physical cable artwork remain; permanent room socket dots are removed. Diagnostic electric cable routes are hidden. The mixed crossing retains a centered native thermal indicator.


## More

- Version history: `CHANGELOG.md` in the source repository.
- Source, issue tracker and bug reports: https://github.com/UberMorgott/Cosmoteer-Mod-CrewlessLogistics
- Cable and icon sprites in `assets/` are derived from Cosmoteer game art for modding purposes.
