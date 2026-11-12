# ETS2 Utilities

A Windows app for Euro Truck Simulator 2 (1.62) that switches a few things on and off for truck and coach profiles:

| Tab | What it does | How |
| --- | --- | --- |
| Gameplay | No damage or wear (trucks, coaches, trailers, cargo) | Generated mod `ets2u_nodamage.scs` |
| Gameplay | Bigger truck job pay and XP (x1 to x50) | Generated mod `ets2u_economy.scs` |
| Gameplay | Coach passenger satisfaction never drops; board at 100% | Generated mod `ets2u_coach.scs` |
| Money & XP | Set or add money, truck XP and coach XP | Edits the save in place: binary (the game's default) or text |
| HUD & Dash | Route advisor map position (trucks and coaches) | Generated mod `ets2u_ui.scs` |
| HUD & Dash | Speed in km/h and mph together | Telemetry plugin `ets2u_telemetry.dll` + on-screen overlay |
| Backups | Undo any change the app made | Copies taken before every write |

Mods are built from the game's own files, read straight out of its `.scs` archives (a few milliseconds,
nothing extracted). When the game updates, the app rebuilds them on its next start. Every generated mod
is marked `mp_mod_optional`, so you can join Convoy sessions that allow optional mods without the other
players having them.

## Install

1. Download `ets2-utilities-v<version>-win-x64.zip` from the Releases page and unzip it anywhere.
2. Keep the `plugin` folder next to `ets2u.exe` (it holds the speed plugin).
3. Close ETS2, run `ets2u.exe`. Changes to mods apply the next time the game starts; enable each new
   "ETS2 Utilities" mod once in Mod Manager.

Every change is backed up first (Backups tab). Don't use edited saves or the plugin on TruckersMP.

## Requirements (to build)

- .NET 8 SDK to build (`winget install Microsoft.DotNet.SDK.8`)
- For the speed plugin only: the Visual Studio C++ build tools and SCS's telemetry SDK
  ([scs_sdk_1_16.zip](https://download.eurotrucksimulator2.com/scs_sdk_1_16.zip), MIT) unpacked in `tools/scs_sdk`

## Build and run

```
dotnet test
dotnet run --project src/Ets2u.App
```

One self-contained exe (the publish folder also gets `plugin\ets2u_telemetry.dll`; keep it next to `ets2u.exe`):

```
dotnet publish src/Ets2u.App -c Release -o publish
```

The speed plugin:

```
plugin\build.cmd        builds plugin\bin\ets2u_telemetry.dll
plugin\test.cmd         builds it and checks it in a fake game host
```

Read-only checks against the real game install and saves on this PC:

```
set ETS2U_LOCAL=1
dotnet test
```

## Where things go

| What | Where |
| --- | --- |
| Settings | `%APPDATA%\ets2u\settings.json` |
| Backups (newest 20) | `%LOCALAPPDATA%\ets2u\backups\` |
| Log | `%LOCALAPPDATA%\ets2u\log.txt` |
| Generated mods | `Documents\Euro Truck Simulator 2\mod\ets2u_*.scs` |
| Speed plugin | `<game>\bin\win_x64\plugins\ets2u_telemetry.dll` |

Never commit game files or SCS tools; `.gitignore` excludes them.

## Notes

- Coaches share the truck bank (`money_account`) but level up separately (`bus_experience_points`).
- Coach jobs have no pay or XP rates in the game files, so "bigger earnings" is trucks only.
- Coach satisfaction rules live in `def/bus_economy_data.sii`; the coach mod zeroes its penalties only, not its rewards.
- Save decryption and the binary save (BSII) value types follow [SII_Decrypt](https://github.com/TheLazyTomcat/SII_Decrypt).
  Game 1.62 writes BSII version 3, which adds types 0x3B-0x3E (IDs and arrays of IDs). Money (s64) and both XP
  counters (u32) are fixed-size, so a binary save is edited by overwriting those bytes; the edited file is written
  back unencrypted, still binary. An unknown value type stops the edit with a message instead of guessing.
- The archive reader (HashFS v2) follows the format documented by
  [Archive::SCS](https://metacpan.org/pod/Archive::SCS::HashFS2); CityHash64 is Google's algorithm (MIT).

## Licence

GPL-3.0; see [LICENSE](LICENSE). The speed plugin is built against SCS Software's telemetry SDK (MIT), which
is not included here. Euro Truck Simulator 2 and its game files belong to SCS Software; this project ships none of them.
