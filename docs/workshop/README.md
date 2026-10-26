# Steam Workshop texts

One file per Steam language. Line 1 is the item title (always `Crewless Logistics`); the rest is the description in Steam BBCode, at most 7999 UTF-8 bytes.

| File | Steam language code | Game strings |
| --- | --- | --- |
| `english.txt` | `english` | `en` |
| `russian.txt` | `russian` | `ru` |
| `german.txt` | `german` | `de` |
| `spanish.txt` | `spanish` | `es` |
| `french.txt` | `french` | `fr` |
| `japanese.txt` | `japanese` | `ja` |
| `brazilian.txt` | `brazilian` | `pt-br` |
| `schinese.txt` | `schinese` | `zh-cn` |

Keep all eight in sync when features change. Each text keeps the "do not report bugs in the comments" block pointing to
https://github.com/UberMorgott/Cosmoteer-Mod-CrewlessLogistics/issues. Terms follow the game's own strings (`Data/strings/<lang>.rules`).

Keywords: power, energy, cables, automation, logistics, crewless.

## In-game uploader (Cosmoteer 0.30.5, decompiled)

- Mods list, right-click a **User** mod (installed in `Saved Games\Cosmoteer\<SteamID>\Mods\<folder>`), then **Upload to Workshop**. Only shown for user-installed mods while Steam is running (`Cosmoteer.Gui/ModsDialog.cs:515`, `:568-572`).
- Uploads the whole installed folder `Mods\CrewlessLogistics` as item content (`WorkshopUploaderDialog.cs:535`), not the repo `mod\` folder. Install first with `tools\Update-DeviceCompatibility.ps1 -Install`.
- Title, description and preview default to `mod.rules` `Name`, `Description` and `Logo` (`WorkshopUploaderDialog.cs:283-284`); all three can be edited before upload. The uploader sets only one language; the other languages are set on the Steam item page or by a publisher tool.
- Tags: always exactly `Mods` (`:530`).
- Visibility: dropdown, default Public (`:211-226`). Min/max game branch version: dropdowns (`:227-231`, `:542`).
- Change note: text field on updates; the first upload uses the game's built-in "first version" string (`:150-151`, `:558`). For updates, paste the version's entry from `CHANGELOG.md`.
- The Workshop item ID is written to a hidden `.workshop` file in the uploaded folder (`:39`, `:104-108`, `:548-549`). If it exists, the next upload updates the same item. `Install-Mod.ps1` never deletes it. Back it up; losing it means a new item on the next upload.
- Preview image: PNG or JPG (`:483`); PNG is converted to JPG before upload (`:430-437`). Steam's preview limit is 1 MB. `mod.rules` `Logo` is also drawn at native pixel size in the in-game Mods list (`ModsDialog.cs:683-695`).
