# GC Client (Valheim mod)

Item comparison (EpicLoot too), compass, sorting, carry limit, trash, auto doors, area harvest, dungeon map. GC Option menu edits every mod's settings, replaces Mod Menu. More on GitHub. Gorduan Community (GC). Free to use, modify, re-upload (MIT).

Version 1.7.0 · Author: Gorduan · License: MIT

## Included mods

All mods of this package are in GC_Client.dll, next to GC_Kern.dll (the shared core).

| Mod | Level | Runs on | Feature |
|---|---|---|---|
| GC_Kern | 0 Core | always, on server and clients | **GC core**: GC Option menu for all GC mods (pause and main menu), switch single mods off, server level, mod check on joining, optional boss progression. Explanations for every feature in DeepTip windows. |
| GC_Kompass | 1 Very vanilla | client only | **Compass**: Compass at the top of the screen with map markers, other players and a golden target set with Alt+left click on the map. |
| GC_Portalschutz | 1 Very vanilla | client only | **Portal guard**: Asks before a portal is renamed and remembers the previous name. |
| GC_Vergleich | 1 Very vanilla | client only | **Equipment comparison**: Shows next to the tooltip what you are wearing: gains in green, losses in red. |
| GC_KisteLeeren | 2 Vanilla | client only | **Empty chest**: Shift+E on a chest takes everything without opening it. |
| GC_Sortieren | 2 Vanilla | client only | **Sorting**: Sorts inventory and open chests by button or key. Locked items and the hotbar stay, equal stacks are merged. |
| GC_Traglimit | 2 Vanilla | client only | **Carry limit**: Picking up and Ctrl+click from chests take only as much as you can still carry. |
| GC_Vernichten | 2 Vanilla | client only | **Trash**: Destroy items via a trash slot in the inventory or by key, with confirmation. |
| GC_DungeonKarte | 3 Vanilla+ | client only | **Dungeon map**: Map inside dungeons: small in the corner, large with the map key. |
| GC_Einsammeln | 3 Vanilla+ | client only | **Collect into vehicle**: Loose items go into your cart while you pull it, or into your ship. |
| GC_Tueren | 3 Vanilla+ | client only | **Automatic doors**: Doors open in front of you and close behind you. They stay shut while enemies are near or during a raid. |
| GC_UmkreisErnte | 3 Vanilla+ | client only | **Area harvest**: Holding E harvests all plants and pickables of the same kind around you. |

## Compatibility

Works together with these mods and extends some of them. All of them are optional, unless the table above says a mod requires one.

- **[EpicLoot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/)**
  - GC_Kern: Optional boss progression counts EpicLoot as at least +10 %.
  - GC_Vergleich: Compares with EpicLoot's magic values and replaces its Ctrl comparison while active.
  - GC_Sortieren: Enchanted items are never merged.
- **[ValheimPlus](https://thunderstore.io/c/valheim/p/Grantapher/ValheimPlus_Grantapher_Temporary/)**
  - GC_Kern: Shows the ValheimPlus settings in the GC Option menu; change them in the main menu.
- **[World Advancement Progression](https://thunderstore.io/c/valheim/p/VentureValheim/World_Advancement_Progression/)**
  - GC_Kern: Boss progression counts the bosses each player has defeated (private keys).
- **[PlantEverything](https://thunderstore.io/c/valheim/p/Advize/PlantEverything/)**
  - GC_Kern: Its texts come back after changing the language in game.
  - GC_UmkreisErnte: Also knows the plants PlantEverything adds.
- **[Mod Menu](https://thunderstore.io/c/valheim/p/Dismonder/ModMenu/)**
  - GC_Kern: The GC Option menu replaces it: switch any mod on or off, with profiles and dependencies. Its list and profiles are taken over once.
- **All other mods**
  - GC_Kern: The GC Option menu shows and edits the settings of every installed mod, server rules (Jotunn, ServerSync) only for admins. On joining, the mod check lists every mod that differs from the server.

## Rules

- **Requires [DeepTip](https://thunderstore.io/c/valheim/p/Gorduan/DeepTip/)** (since 1.9.0): every GC feature explains itself in DeepTip windows. Rest the mouse on a function name in the GC Option menu or on a highlighted term in a tooltip.
- **GC_Kern.dll** must always stay. It ships in every GC package so that each package works on its own; BepInEx loads it once and reports the other copies as "duplicate". That is intended.
- **Switching mods off:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you switch such a mod off on the server, your players must switch it off too.
- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.

## Requirements

- Valheim 1.0.20 with BepInExPack Valheim 5.4.2350 or newer
- Jötunn 2.30.2
- Optional: Guilds_Vehicle (some mods do more when installed)

## Building

1. Install the .NET SDK 9 or newer.
2. Copy `GamePaths.user.props.example` to `GamePaths.user.props` and set `GamePath` (your Valheim folder with BepInEx) and `JotunnDll`.
3. Run `powershell -ExecutionPolicy Bypass -File build.ps1`. The package folder and a zip for r2modman ("Import local mod") end up in `build-output/`.

`Kern/` is the GC core shared by all GC packages (the GC Option menu, settings, texts, permissions). `Patcher/` holds the preloader that lets GC Option switch mods off, `Mods/` one folder per mod, `Pakete/` the project that merges the mods into one DLL (ILRepack). Code comments and `SDK.md` are in German.

## GC packages

- [Valheim_GC_Server](https://github.com/gorduan/Valheim_GC_Server)
- [Valheim_GC_Client](https://github.com/gorduan/Valheim_GC_Client) (this repository)
- [Valheim_GC_ValheimPlusPlus](https://github.com/gorduan/Valheim_GC_ValheimPlusPlus)

## License

MIT for the code, see [LICENSE](LICENSE).
