# ETS2 Utilities

A Windows app for Euro Truck Simulator 2 (1.62) that switches a few things on and off for truck and coach profiles:

The app has one page per mod or feature, grouped in a list on the left:

| Page | What it does | How |
| --- | --- | --- |
| **Overview** · Mods & plugins | Every mod and plugin the app makes, its file and state; rebuild all mods, (re)install or remove the plugin | |
| **Overview** · Game & profiles | Where the game, mods and profiles are | |
| **Mods** · No damage | No damage or wear (trucks, coaches, trailers, cargo) | Generated mod `ets2u_nodamage_<tag>.scs` |
| **Mods** · Bigger earnings | Bigger truck job pay and XP (x1 to x50) | Generated mod `ets2u_economy_<tag>.scs` |
| **Mods** · Coach passengers | How much g-force passengers take (a normal and a harsh limit for each of cornering, bumps, and accelerating and braking, and the job-failure limit, with presets); choose what lowers satisfaction; board at 100% | Generated mod `ets2u_coach_<tag>.scs` |
| **Mods** · Route advisor | Route advisor map position (trucks and coaches) | Generated mod `ets2u_ui_<tag>.scs` |
| **Mods** · Destination display | Your own text (and route number, colour, size) on the coaches' front destination display, job or no job; a searchable list of saved texts of any size | Generated mod `ets2u_busdisplay_<tag>.scs` |
| **Mods** · Livery pack | Packs many livery mods (local files or folders, and Steam Workshop items, added by link without subscribing) into one optional mod; rebuilt on start when a source mod changes | Generated mod `ets2u_liveries_<tag>.scs` |
| **With the plugin** · Speed overlay | Speed in km/h and mph together | Telemetry plugin `ets2u_telemetry.dll` + on-screen overlay |
| **With the plugin** · Driving alerts | GreenRoad-style alerts: yellow or red cards for harsh acceleration, braking and cornering, red for speeding (limit + 10% + 2 km/h); separate switches and thresholds for coaches and trucks; defaults 0.35 / 0.7 g (yellow / red) for coaches and 0.4 / 0.8 g for trucks, in every direction; each vehicle can instead use the coach mod's g-force limits (yellow = normal, red = harsh) or its own numbers, and the defaults or coach mod can be relaxed 10% or 20%, separately for coaches and trucks; a floating live g-force meter shows now and peak in each direction | Telemetry plugin + on-screen overlay |
| **With the plugin** · Trip summary | GreenRoad-style trip summary: press Start capturing and Stop capturing in the app; every harsh acceleration, braking, cornering and speeding event is listed with its time, a yellow or red icon and how it happened (e.g. "Braking to a stop", "Accelerating while exiting a turn"), with details on click; driving time, distance, top speed, counts per category and a safety score (events per 10 hours, red counting double; green under 20, yellow under 40); copy as text | Telemetry plugin, uses the Driving alerts thresholds |
| **Tools** · Money & XP | Set or add money, truck XP and coach XP | Edits the save in place: binary (the game's default) or text |
| **Tools** · Game information | Fuel price per country, in any currency the game's options offer (at the game's own rates), and every city of the installed game with its name ready to copy for the console's `goto` | Read from `def.scs` (currencies from `def/economy_data.sii`), the installed `dlc_*.scs` and `locale.scs` |
| **Tools** · Backups | Undo any change the app made (except the livery pack, which is rebuilt from its sources) | Copies taken before every write |

Mods are built from the game's own files, read straight out of its `.scs` archives (a few milliseconds,
nothing extracted). When the game updates, the app rebuilds them on its next start. Every generated mod
is marked `mp_mod_optional`, so you can join Convoy sessions that allow optional mods without the other
players having them.

## Coach g-forces

The game decides when coach passengers complain in `def/bus_economy_data.sii`, in m/s² per direction
(x across the coach, y up, z along it): a normal limit (6 m/s², 0.61 g, in every direction), a harsh one
(only along the coach, 11 m/s², 1.12 g) and a job-failure limit (along the coach, 22 m/s²). The Workshop's
"Realistic Coach G-Force" mod raises the two limits to 13 m/s². This app's version lets you set each
direction and tier on its own, in g, with Stock / Forgiving / Relaxed starting points, and keeps every
penalty and reward (only where they start moves). Every direction gets a harsh limit: the game has one for
braking only, so a corner taken far too fast costs no more than a slightly brisk one. The Stock starting
point keeps the game's numbers and uses its braking harsh limit (1.12 g) in every direction.

The driving alerts can follow these limits, for coaches, trucks or both (an option on the Driving alerts
page): yellow at the normal limit, red at the harsh one (accelerating and braking use the limit along the
coach, cornering the one across it), as they are or 10% / 20% looser. Until the coach mod changes the limits, that uses the Stock ones.

The plugin works out cornering g itself: the SDK's `truck.local.acceleration.linear` is how the velocity seen
from the truck changes, which stays near zero through a steady corner, so the plugin adds the turning part
(angular velocity x velocity, i.e. speed x yaw rate), as an accelerometer in the cab would feel it. It's part of the Coach comfort mod, because both change the same
file; turn off other mods that change `bus_economy_data.sii`, or only one of them wins.

## Destination display

The coaches' front display ("standard destination display" upgrade) is a small UI window
(`ui/dashboard/bus_panel_generic.sii` in `dlc_bus.scs`) whose one text element the game fills with the
job's destination, through the template `txt.dlc_bus.bus_panel` in `ui/template/dashboard_text.dlc_bus.sii`:
font, size, colour, and `%0` for the game's text. The mod ships that template with your text instead of
(or around, with `{destination}`) the `%0`, so it shows without a job too, whenever the ignition is on.

Limits: one line of up to 60 characters, in the game's own font (Latin script; other scripts may not
show). The game reads UI files when it starts, so a new text needs a restart; the SDK gives plugins no
way to write to the display while driving. The saved-texts list is a plain text file
(`%APPDATA%\ets2u\destination_texts.txt`, one `route | text` per line), searched from disk, so it can hold
millions of lines.

## Livery pack

Every new livery comes as its own mod. The Livery pack page puts the ones you choose into one mod,
`ets2u_liveries_<tag>.scs` (your own tag, like every mod here, and optional for Convoy). Sources can be
local mods (`.scs` as zip or HashFS v2, `.zip`, or an unpacked folder) or Steam Workshop items. For a Workshop
item the app picks the package the game would load for its version (`versions.sii`). Files at a mod's root
(manifest, pictures) are left out; if two mods hold the same file, the one higher in the list wins and the
clash is shown. A source that's gone stops a rebuild instead of silently dropping its liveries.

### Workshop items without subscribing

Paste a Workshop link or ID and press **Check**. The app asks Steam's public Web API (no key or login) about
the item and shows its title, creator, size, update date and tags, whether it's for ETS2 and whether it's
tagged as a paint job, before anything is downloaded. **Download and add** then downloads it into
`%LOCALAPPDATA%\ets2u\workshop\<id>\` with [DepotDownloader](https://github.com/SteamRE/DepotDownloader),
so you don't subscribe and no stray mod appears in Mod Manager.

- DepotDownloader runs as its own program. The app uses a copy installed with winget
  (`winget install SteamRE.DepotDownloader`) if there is one, otherwise a `DepotDownloader\` folder beside
  `ets2u.exe`, otherwise it downloads its own: release 3.4.0 from GitHub, checked against a pinned SHA-256,
  into `%LOCALAPPDATA%\ets2u\DepotDownloader\`.
- ETS2 Workshop content needs a Steam account that owns the game. The first download shows a QR code in the
  app; scan it in the Steam mobile app (Steam Guard → Scan a QR code). DepotDownloader remembers that
  login (in .NET isolated storage), so later downloads, including the update checks on start, run without
  asking. The app only keeps the account name. If the remembered login stops working, the next download you
  start asks for a QR code again; the check on start never does, it only reports it. **Forget sign-in**
  drops the account name; to end DepotDownloader's session, remove it from Authorized devices in your
  Steam account settings.
- On every start (with the pack switched on), the app asks Steam when each downloaded item last changed,
  downloads the ones that did and rebuilds the pack. **Download missing** does the same on demand.
- Items you're subscribed to are still read from Steam's own copy, which Steam keeps up to date. If you
  unsubscribe from one, the app downloads its own copy.

**Only repack Workshop items whose creators allow editing and redistribution.** The app asks you to
confirm this before it builds a pack that contains Workshop items, and lists every creator in the pack's
description. Don't share a pack with other people's work in it without their permission. After enabling
the pack, turn off any of the original livery mods you're subscribed to in Mod Manager.

## Install

1. Download `ets2-utilities-v<version>-win-x64.zip` from the Releases page and unzip it anywhere.
2. Keep the `plugin` folder next to `ets2u.exe` (it holds the speed plugin).
3. Close ETS2, run `ets2u.exe`. Changes to mods apply the next time the game starts; enable each new
   "ETS2 Utilities" mod once in Mod Manager.

Every change is backed up first (Backups page). Don't use edited saves or the plugin on TruckersMP.

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
| Coach comfort and g-force limits | Applies to your own passengers only |
| Destination display | Only changes what you see on your own coach |
| Livery pack | Others see your livery only if they have it too; the pack is optional for them |
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

Install the plugin (Mods & plugins page), tick **Let the mouse drive** on the Driving alerts page, then press F8
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
| Saved destination display texts | `%APPDATA%\ets2u\destination_texts.txt` |
| Workshop items downloaded by the app | `%LOCALAPPDATA%\ets2u\workshop\<id>\` |
| DepotDownloader (when the app installed it) | `%LOCALAPPDATA%\ets2u\DepotDownloader\` |

Never commit game files or SCS tools; `.gitignore` excludes them.

## Notes

- Coaches share the truck bank (`money_account`) but level up separately (`bus_experience_points`).
- Coach jobs have no pay or XP rates in the game files, so "bigger earnings" is trucks only.
- Coach satisfaction rules live in `def/bus_economy_data.sii`; the coach mod zeroes its penalties only, not its rewards,
  and moves the g-force limits (see Coach g-forces).
- Save decryption and the binary save (BSII) value types follow [SII_Decrypt](https://github.com/TheLazyTomcat/SII_Decrypt).
  Game 1.62 writes BSII version 3, which adds types 0x3B-0x3E (IDs and arrays of IDs). Money (s64) and both XP
  counters (u32) are fixed-size, so a binary save is edited by overwriting those bytes; the edited file is written
  back unencrypted, still binary. An unknown value type stops the edit with a message instead of guessing.
- Money limit: when a binary save declares `money_account` as s64, money can go up to 9,223,000,000,000,000,000
  (just under the s64 maximum of 9,223,372,036,854,775,807, leaving headroom so job income can't overflow it). Text saves, and any other field type, keep the
  2,000,000,000 limit, because a text save doesn't show how wide the field is.
- The archive reader (HashFS v2) follows the format documented by
  [Archive::SCS](https://metacpan.org/pod/Archive::SCS::HashFS2); CityHash64 is Google's algorithm (MIT).

## Licence

GPL-3.0; see [LICENSE](LICENSE). The speed plugin is built against SCS Software's telemetry SDK (MIT), which
is not included here. DepotDownloader (GPL-2.0, SteamRE) is not included either: it's run as a separate
program, downloaded from its own GitHub release when needed. Euro Truck Simulator 2 and its game files belong to SCS Software; this project ships none of them.
