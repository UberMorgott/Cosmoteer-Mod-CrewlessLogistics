# Crewless Logistics

A Cosmoteer mod for game version 0.30.5 (current version 0.4.3). Cables carry whole batteries from reactors and Power Capacitors straight to powered devices, freeing crew from battery runs, and conveyor belts move resources between rooms. Normal crew delivery still works alongside both.

**Steam Workshop:** <https://steamcommunity.com/sharedfiles/filedetails/?id=3827751334>

**Requires** [Yet Another Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065) (Workshop 3577650065), installed with its `Installer.bat`. Trust the Crewless Logistics library in the in-game Mods list.

## Reporting issues

**Report bugs only on GitHub, not in Workshop comments.** Open an issue with the **Bug report** template:
<https://github.com/UberMorgott/Cosmoteer-Mod-CrewlessLogistics/issues>

Include the mod version from the Mods list, the game version from the bottom-left corner of the main menu, install source, all other enabled mods, and whether you were playing single-player or multiplayer (host/client and whether everyone has the same mod version). Give steps to reproduce, expected and actual results, and screenshots. Attach the `.ship.png` and save when relevant, plus the newest log from the affected session in `%USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Logs\` (`log YYYY-MM-DD HH_mm_ss.txt`).

## Features

- **Electrical cable:** four automatic connections; blocks crew passage. Its dark plated casing is cosmetic, with a plain hull roof.
- **Crew crossing:** two opposite electrical ports; crew can walk perpendicular to the cable at 80% speed. Rotate to choose the cable direction.
- **Heat/electric crossover:** carries heat along one axis and electricity along the other. Rotate to swap axes. Blocks crew passage. Available in **Power** and **Heat Management**.
- All three cable parts have vanilla Heat Pipe stats: **1000 HP, density 1, no armor category and no EMP absorption**. Each costs **4 coils + 2 steel**.
- **Electrical Network** switch on every reactor, capacitor and powered device: **off by default**, with its setting saved. Each participant needs its own switch on.
- **Reactors supply first.** Capacitors supply only while no reachable reactor has a whole battery. Capacitors charge through the network only from reactors, after devices have been supplied.
- **Engine Rooms** receive network power and pass batteries to the engines and thrusters they buff. The room's Electrical Network and power distribution switches must be on, as must each recipient's Electrical Network switch.
- Supports all vanilla powered devices and powered parts from other mods that use standard battery storage (for example **Diagonal Thruster Plus** and **Diagonal Cockpits**). Sockets are added at game load; unsupported parts are listed in `Logs\CrewlessLogistics.Belts.log`.
- Green lamps on electrical cables and crew crossings light up when a connected source has a whole battery.
- **Resource logistics** (Storage and Power tabs): crew-walkable two-lane conveyor belts that curve automatically and accept side-loading, a 1x2 splitter with input/output priority and a filter, a directional connector that moves one chosen resource between a room, a belt or another room (filter Auto by default), and a straight belt crossing that adapts to a Heat Pipe or electrical cable under it (crew walk along the belt at 70% speed).
- The energy network is rules-only; belts run in a small C# library (`CrewlessLogistics.Belts.dll`) loaded by Yet Another Mod Loader. UI text in all eight game languages.

Mod ID `morgott.crewless_logistics`, package folder [`mod/`](mod).

## Install

- **Steam Workshop:** install Yet Another Mod Loader, subscribe, enable *Crewless Logistics* in the in-game Mods list, trust its library, restart the game.
- **From source:** run `.\Install-Mod.ps1`. It builds `CrewlessLogistics.Belts.dll` and installs it next to `mod.rules` with the `mod` folder in `%USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Mods\CrewlessLogistics` (auto-detects the single profile; pass `-ModsDirectory` otherwise). Enable it, trust the library (every rebuild changes it), restart.

Multiplayer: every player needs the same mod, mod loader and game version.
After an update, restart the game before joining.

## Use

1. Build cables from the **Power** category between reactors, capacitors and devices. Use the crew crossing where crew must walk through, the crossover where a heat pipe has to cross.
2. Turn on the **Electrical Network** switch on each device that should be connected.

## Build and verify

Requires Windows, PowerShell 7, the .NET 10 SDK, an installed Cosmoteer, Yet Another Mod Loader (its `0Harmony.dll` is referenced from the Workshop folder) and the publicizer tool `dotnet tool install -g BepInEx.AssemblyPublicizer.Cli` (see `belts/CrewlessLogistics.Belts.csproj`).

```powershell
dotnet build; dotnet test                            # belt sim, network coverage + native probe tests (CrewlessLogistics.sln)
& .\Install-Mod.ps1                                 # build Release DLL, install into the game profile
```

`Install-Mod.ps1 -ModsDirectory <path>` overrides the default single profile under `%USERPROFILE%\Saved Games\Cosmoteer\`. Device network sockets are added at game load by the DLL (`belts/src/NetworkCoverage.cs`), for vanilla and modded parts alike; nothing is regenerated per enabled-mod set. Details: [`docs/TECHNICAL.md`](docs/TECHNICAL.md), [`docs/PORT_COVERAGE.md`](docs/PORT_COVERAGE.md).

## Notes

- Only whole batteries move. A device waits until a whole battery fits.
- Devices from mods that ship DLLs or use their own energy types are not supported.
- Upgrading from Powergrid (the old name): cables load, but Electrical Network switches reset to off. Remove the old Powergrid folder.
- Belt design and implementation notes: [`docs/BELTS_DESIGN.md`](docs/BELTS_DESIGN.md).
- History: [`CHANGELOG.md`](CHANGELOG.md). Workshop texts: [`docs/workshop/`](docs/workshop).
- The cable sprites in `mod/assets` are derived from Cosmoteer game art (recoloured vanilla heat pipe sprites, see `tools/Bake-CableSprites.ps1`) for modding purposes. Cosmoteer and its assets belong to Walternate Realities.
