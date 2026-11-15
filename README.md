# ETS2 Utilities

A Windows app for Euro Truck Simulator 2 (1.62) that switches a few things on and off for truck and coach profiles:

| Tab | What it does | How |
| --- | --- | --- |
| Gameplay | No damage or wear (trucks, coaches, trailers, cargo) | Generated mod `ets2u_nodamage_<tag>.scs` |
| Gameplay | Bigger truck job pay and XP (x1 to x50) | Generated mod `ets2u_economy_<tag>.scs` |
| Gameplay | Choose what lowers coach passenger satisfaction (driving style, lateness, speeding, crashes, wear, offences), turn off g-force job failure, board at 100% | Generated mod `ets2u_coach_<tag>.scs` |
| Money & XP | Set or add money, truck XP and coach XP | Edits the save in place: binary (the game's default) or text |
| HUD & Dash | Route advisor map position (trucks and coaches) | Generated mod `ets2u_ui_<tag>.scs` |
| HUD & Dash | Speed in km/h and mph together | Telemetry plugin `ets2u_telemetry.dll` + on-screen overlay |
| Driving | OMSI-style mouse driving: the mouse's place across the screen steers (heavier with speed), above or below the middle is throttle or brake | Input device in `ets2u_telemetry.dll`; steering lines in each profile's `controls.sii` |
| Driving | GreenRoad-style alerts: yellow or red cards for harsh acceleration, braking and cornering, red for speeding (limit + 10% + 2 km/h); separate switches and thresholds for coaches and trucks (0.4 / 0.6 g for coaches, 0.45 / 0.65 g for trucks) | Telemetry plugin + on-screen overlay |
| Game Information | Fuel price per country, and every city of the installed game with its name ready to copy for the console's `goto` | Read from `def.scs`, the installed `dlc_*.scs` and `locale.scs` |
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

## Playing in Convoy (SCS multiplayer)

By default Convoy makes every player run the same mods. Every mod this app generates is marked
optional for multiplayer (`mp_mod_optional: true` in its manifest), so you can keep yours on and still
join players who don't have them, provided the session allows optional mods.

Each player's mod files get their own name (`<tag>` above: six characters made once from your Steam
account, or at random). The game tells local mods apart by file name, so your Coach comfort and a
friend's, built from different settings, count as two separate optional mods rather than one mod in two
versions. The tag never changes with your settings, so a mod stays enabled in Mod Manager when you change
them. v0.4 and earlier used names without a tag; on first start the app replaces those files, and you
enable the mods once more in Mod Manager.

When you host from inside the game, tick **Enable optional mods** on the Convoy create screen.

**If you host a dedicated Convoy server**

1. In your own game, enable the mods everyone must have (map mods are always required), open the
   console and run `export_server_packages`.
2. Copy the exported `server_packages.sii` and `server_packages.dat` to the server's config folder.
3. In the server's `server_config.sii`, set `mods_optioning: true`. Without it, optional mods are treated
   as required.
4. Restart the server (it reads its config only at start).

**As a player**

- In the Convoy session browser, turn on the filter that shows sessions with optional mods.
- Your ETS2 Utilities mods can stay enabled; other players don't need them.

**What each feature does in a convoy**

| Feature | In Convoy |
| --- | --- |
| No damage | Applies to your own truck or coach only |
| Coach comfort | Applies to your own passengers only |
| Map position | Only changes your HUD |
| Bigger earnings | Applies to your own jobs; not checked for jobs shared by the host |
| Money & XP | Edits your own profile's save; other players aren't affected |
| Speed overlay, driving alerts, mouse driving | Run on your PC only |

Don't use any of this on TruckersMP, which has its own rules and anti-cheat.

## Mouse driving

> **Currently disabled.** The UI, the app's calls and the plugin's input device are commented out (search for
> `MOUSE DRIVING DISABLED`; the plugin's part is behind `ETS2U_MOUSE_DRIVING`, plus the two `scs_input_*`
> lines in `plugin\ets2u_telemetry.def`). The Core classes (`MouseDriveConfig`, `MouseDriveControls`) and
> `MouseDriveIndicator` are kept but unused. The text below describes the feature as it was.

Install the plugin (HUD & Dash tab), tick **Let the mouse drive** on the Driving tab, then press F8
(or Insert, or the middle mouse button) while driving to switch it on and off. It works the way
OMSI 2 does; the maths follows [openOMSI](https://github.com/openOMSI-org/openOMSI)'s port of Omsi.exe:

- The window's width is full left to full right lock at walking pace. Above 10 km/h the steering is
  divided by the speed in tens of km/h, so at 50 km/h the same hand movement turns the wheel a fifth
  as far. The wheel eases to the mouse over the first second after switching on, then follows within ~60 ms.
- From the middle of the window up is the throttle, down is the brake (with a 5% dead zone so you can coast).
- Hold the right mouse button to look round: the wheel and pedals stay put, as in OMSI.
- ETS2 reads the mouse through DirectInput and keeps it from other programs while it has the focus,
  so the plugin reads it the same way from inside the game, as a second (non-exclusive) DirectInput mouse.
  The app doesn't need to be open.

Applying it (with ETS2 closed) changes the camera's two mouse lines in each profile's `controls.sii` (for a
Steam Cloud profile that's the local copy in `Documents\Euro Truck Simulator 2\steam_profiles\<id>`),
backed up first and put back when you turn it off: `camlr` and `camud` take the mouse's movement times
`(1 - semantical.camlr?0)` / `(1 - semantical.camud?0)`. The plugin reports 1 on those while the mouse
steers, so the camera holds still, and 0 otherwise (switched off with F8, right button held, menus), so the
mouse looks round as usual. Steering and keyboard steering are left alone. The app sets this up again on
start if the game reset its controls.
The steering itself goes
through the game's `semantical.steering`, `aforward` and `abackward` inputs. While it's on, the plugin
writes `%LOCALAPPDATA%\ets2u\mouse_trace.csv` (what the mouse asked for and what the wheel did).

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
| Mouse driving settings (read by the plugin) | `%APPDATA%\ets2u\mouse_drive.ini` |

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
