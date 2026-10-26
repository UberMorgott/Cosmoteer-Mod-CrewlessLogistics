# Crewless Logistics

A Cosmoteer mod for game version 0.30.5. Cables carry whole batteries from reactors and Power Capacitors straight to powered devices, freeing crew from battery runs. Normal crew delivery still works alongside the electrical network.

**Steam Workshop:** https://steamcommunity.com/sharedfiles/filedetails/?id=3827751334

## Reporting issues

**Report bugs only on GitHub, not in Workshop comments.** Open an issue with the **Bug report** template:
https://github.com/UberMorgott/Cosmoteer-Mod-CrewlessLogistics/issues

Include the mod version from the Mods list, the game version from the bottom-left corner of the main menu, install source, all other enabled mods, and whether you were playing single-player or multiplayer (host/client and whether everyone has the same mod version). Give steps to reproduce, expected and actual results, and screenshots. Attach the `.ship.png` and save when relevant, plus the newest log from the affected session in `%USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Logs\` (`log YYYY-MM-DD HH_mm_ss.txt`).

## Features

- **Electrical cable:** four automatic connections; blocks crew passage. Its dark plated casing is cosmetic, with a plain hull roof.
- **Crew crossing:** two opposite electrical ports; crew can walk perpendicular to the cable at 80% speed. Rotate to choose the cable direction.
- **Heat/electric crossover:** carries heat along one axis and electricity along the other. Rotate to swap axes. Blocks crew passage. Available in **Power** and **Heat Management**.
- All three cable parts have vanilla Heat Pipe stats: **1000 HP, density 1, no armor category and no EMP absorption**. Each costs **4 coils + 2 steel**.
- **Electrical Network** switch on every reactor, capacitor and powered device: **off by default**, with its setting saved. Each participant needs its own switch on.
- **Reactors supply first.** Capacitors supply only while no reachable reactor has a whole battery. Capacitors charge through the network only from reactors, after devices have been supplied.
- **Engine Rooms** receive network power and pass batteries to the engines and thrusters they buff. The room's Electrical Network and power distribution switches must be on, as must each recipient's Electrical Network switch.
- Supports all vanilla powered devices, plus parts from the optional Workshop mods **Diagonal Thruster Plus** (3 thrusters) and **Diagonal Cockpits** (4 control rooms). Compatibility patches apply only if those mods are installed.
- Green lamps on electrical cables and crew crossings light up when a connected source has a whole battery.
- Rules-only: no DLL, no mod loader. UI text in all eight game languages.

Mod ID `morgott.crewless_logistics`, package folder [`mod/`](mod).

## Install

- **Steam Workshop:** subscribe, enable *Crewless Logistics* in the in-game Mods list, restart the game.
- **Manual:** copy the `mod` folder to `%USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Mods\CrewlessLogistics`, enable it in the Mods list, restart. Or run `.\Install-Mod.ps1` (auto-detects the single profile; pass `-ModsDirectory` otherwise).

Multiplayer: every player needs the same mod version and game version.
After an update, restart the game before joining.

## Use

1. Build cables from the **Power** category between reactors, capacitors and devices. Use the crew crossing where crew must walk through, the crossover where a heat pipe has to cross.
2. Turn on the **Electrical Network** switch on each device that should be connected.

## Build and verify

Requires Windows, PowerShell 7, the .NET SDK and an installed Cosmoteer.

```powershell
& .\tools\Update-DeviceCompatibility.ps1            # regenerate compatibility + run native checks
& .\tools\Update-DeviceCompatibility.ps1 -Install   # ...then install into the game profile
```

Parameters: `-GameRoot` (default `D:\Steam\steamapps\common\Cosmoteer`), `-ModsDirectory` (default: the single profile under `%USERPROFILE%\Saved Games\Cosmoteer\`). The script reads the enabled-mod list, regenerates `mod/generated.actions.rules` and `docs/*.json`, and runs the isolated native probe in `prototypes/whole-cell-native` against the game's own assemblies (copied to `artifacts/`, never committed). Details: [`docs/TECHNICAL.md`](docs/TECHNICAL.md), [`docs/PORT_COVERAGE.md`](docs/PORT_COVERAGE.md).

## Notes

- Only whole batteries move. A device waits until a whole battery fits.
- Devices from mods that ship DLLs or use their own energy types are not supported.
- Upgrading from Powergrid (the old name): cables load, but Electrical Network switches reset to off. Remove the old Powergrid folder.
- Ammo and resource logistics are planned, with no promises or dates.
- History: [`CHANGELOG.md`](CHANGELOG.md). Workshop texts: [`docs/workshop/`](docs/workshop).
- The cable sprites in `mod/assets` are derived from Cosmoteer game art (recoloured vanilla heat pipe sprites, see `tools/Bake-CableSprites.ps1`) for modding purposes. Cosmoteer and its assets belong to Walternate Realities.
