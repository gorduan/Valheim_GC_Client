# GC Client (Valheim mod)

Client-side quality of life, one DLL per feature: equipment comparison, compass, sorting, auto doors, area harvest, carry limit, trash slot, dungeon map. Includes GC Option, an in-game settings menu for all mods.

Version 1.6.1 · Author: Gorduan · License: MIT

## Included mods

Each mod is a separate DLL in the package folder. Server owners keep only the ones they want.

| File | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern.dll | 0 Core | always, on server and clients | GC core |
| GC_Kompass.dll | 1 Very vanilla | client only | Compass |
| GC_Portalschutz.dll | 1 Very vanilla | client only | Portal guard |
| GC_Vergleich.dll | 1 Very vanilla | client only | Equipment comparison |
| GC_KisteLeeren.dll | 2 Vanilla | client only | Empty chest |
| GC_Sortieren.dll | 2 Vanilla | client only | Sorting |
| GC_Traglimit.dll | 2 Vanilla | client only | Carry limit |
| GC_Vernichten.dll | 2 Vanilla | client only | Trash |
| GC_DungeonKarte.dll | 3 Vanilla+ | client only | Dungeon map |
| GC_Einsammeln.dll | 3 Vanilla+ | client only | Collect into vehicle |
| GC_Tueren.dll | 3 Vanilla+ | client only | Automatic doors |
| GC_UmkreisErnte.dll | 3 Vanilla+ | client only | Area harvest |

## Rules

- **GC_Kern.dll** must always stay. It ships in every GC package; BepInEx loads it once and reports the other copies as "duplicate". That is intended.
- **Removing DLLs:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you remove DLLs, your players must remove the same ones.
- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.

## Requirements

- Valheim 1.0.20 with BepInExPack Valheim 5.4.2350 or newer
- Jötunn 2.30.2
- Optional: Guilds_Vehicle (some mods do more when installed)

## Building

1. Install the .NET SDK 9 or newer.
2. Copy `GamePaths.user.props.example` to `GamePaths.user.props` and set `GamePath` (your Valheim folder with BepInEx) and `JotunnDll`.
3. Run `powershell -ExecutionPolicy Bypass -File build.ps1`. The package folder and a zip for r2modman ("Import local mod") end up in `build-output/`.

`Kern/` is the GC core shared by all GC packages (the GC Option menu, settings, texts, permissions). `Patcher/` holds the preloader that lets GC Option switch mods off, `Mods/` one folder per mod. Code comments and `SDK.md` are in German.

## GC packages

- [Valheim_GC_Server](https://github.com/gorduan/Valheim_GC_Server)
- [Valheim_GC_Client](https://github.com/gorduan/Valheim_GC_Client) (this repository)
- [Valheim_GC_ValheimPlusPlus](https://github.com/gorduan/Valheim_GC_ValheimPlusPlus)

## License

MIT for the code, see [LICENSE](LICENSE).
