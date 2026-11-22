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
| **Mods** · HUD | Route advisor map position (trucks and coaches) | Generated mod `ets2u_ui_<tag>.scs` ("ETS2 Utilities: HUD") |
| **Mods** · Destination display | Your own text (and route number, colour, size) on the coaches' front destination display, job or no job; a searchable list of saved texts of any size | Generated mod `ets2u_busdisplay_<tag>.scs` |
| **Mods** · Livery pack | Packs many livery mods (local files or folders, and Steam Workshop items, added by link without subscribing) into one optional mod; rebuilt on start when a source mod changes | Generated mod `ets2u_liveries_<tag>.scs` |
| **Mods** · Make optional | An optional copy of any mod that isn't optional for Convoy (e.g. an engine or tire mod from the Workshop): its files as they are, with `mp_mod_optional: true` added to the manifest; rebuilt on start when the original changes | Copy `ets2u_opt_<id>_<tag>.scs` |
| **With the plugin** · Speed overlay | Speed in km/h and mph together | Telemetry plugin `ets2u_telemetry.dll` + on-screen overlay |
| **With the plugin** · Driving alerts | GreenRoad-style alerts: yellow or red cards for harsh acceleration, braking and cornering, red for speeding (limit + 10% + 2 km/h); separate switches and thresholds for coaches and trucks; defaults 0.35 / 0.7 g (yellow / red) for coaches and 0.4 / 0.8 g for trucks, in every direction; each vehicle can instead use the coach mod's g-force limits (yellow = normal, red = harsh) or its own numbers, and the defaults or coach mod can be relaxed 10% or 20%, separately for coaches and trucks; a floating live g-force meter shows now and peak in each direction; shown as the cards, a traffic light (green, yellow and red lamps in a row, green on the left, flashing on an event and steady for the last 30 minutes' safety score), both or neither | Telemetry plugin + on-screen overlay |
| **With the plugin** · Trip summary | GreenRoad-style trip summary: press Start capturing and Stop capturing in the app; every harsh acceleration, braking, cornering and speeding event (speeding only while its alerts are on) is listed with its time, a yellow or red icon and how it happened (e.g. "Braking to a stop", "Accelerating while exiting a turn"), with details on click, sorted by time, type, level or duration, and each can be removed; driving time and distance in game time (with the real ones beside them), top speed, counts per category and a safety score (events per 10 hours of game time, red counting double; green under 20, yellow under 40); copy as text; also a summary of every coach job, kept automatically from when you take the job to when it's delivered or cancelled (last 200) | Telemetry plugin, uses the Driving alerts thresholds |
| **Tools** · Money & XP | Set or add money, truck XP and coach XP | Edits the save in place: binary (the game's default) or text |
| **Tools** · Game information | Fuel price per country, in any currency the game's options offer (at the game's own rates), and every city of the installed game with its name ready to copy for the console's `goto` | Read from `def.scs` (currencies from `def/economy_data.sii`), the installed `dlc_*.scs` and `locale.scs` |
| **Tools** · Backups | Undo any change the app made (except the livery pack and optional copies, which are rebuilt from their sources) | Copies taken before every write |

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

## Coach job summaries

With **Also keep a summary of every coach job** on (Trip summary page, on by default), the app keeps a trip
summary for each job without any buttons: the same events, totals and score as a captured trip, plus how
the job ended (delivered, with revenue and XP, or cancelled, with the penalty) and a line about the job (pay,
planned distance, passengers). Pick a job under **Show**; **Copy summary** and **Forget this job** work on it.

- The plugin passes on the game's `job` and `bus_job` configurations and its gameplay events as plain
  `name=value` text. The SCS SDK doesn't document the bus job's attributes, so the app keeps whatever the
  game sends (in `coach_jobs.json`), but shows only the few that matter to a player. In 1.62 a bus job lists its stages as arrays
  (`source_stop.name[i]`, `destination_stop.name[i]`, planned and actual times, passengers), and ends with
  `bus_job.completed` (revenue, XP, `passenger.satisfaction` from 0 to 1, distance); truck jobs end with
  `job.delivered` or `job.cancelled`. The route runs from the first stage's start to the last one's end.
- When a coach job starts, the app says so and the Trip summary page switches to it, unless you're
  capturing a trip by hand.
- A job starts when its configuration appears and ends when it clears (or the next job starts after a
  delivered or cancelled event). Bus jobs are kept, and any job driven only in a coach; truck jobs aren't.
  A job that was never driven (a profile loaded and left) isn't kept.
- The app and the plugin must be running for the whole job: a job is only seen while the app is open.
  Closing the game mid-job and coming back to it carries on the same summary as long as the app stayed open.
- Summaries are kept in `%APPDATA%\ets2u\coach_jobs.json` (newest 200).

## Safety events list

The Trip summary page lists the trip's or job's events. **Sort by** orders them by time, type
(acceleration, braking, cornering, speeding), level (yellow, red) or how long each lasted, ascending or
descending; ties go by time. The order is remembered. **Remove** on an event takes it off the list, and it
no longer counts in the totals or the score: on a trip being captured or a job being driven (an event
still going on stays off when it turns red), and on a saved job, whose file is updated.

Speeding is only detected while **Speeding alerts** is ticked on the Driving alerts page. Unticked, it
isn't counted anywhere: not as cards, not on the traffic light and not in trip or job summaries. (Before
0.12.4 trips and jobs counted it anyway.) Events already in a summary stay until removed.
- This needs plugin version 5 (0.12.1): install the plugin again from the app.

## Safety score

The trip and job summaries score the driving the way GreenRoad does: harsh events per 10 hours of driving,
a red one counting as two yellows.

    score = (yellow + 2 x red) / hours driven x 10

Green is under 20, yellow under 40, red from 40. The hours (and the distance) are the game's, not the real
ones: ETS2's map is smaller than the world it stands for and its clock runs faster, by the game's time
scale (the SDK's `local.scale`, about 19 on roads and less in towns). A route packs the corners, junctions
and stops of a real 1½-hour run into a few real minutes, so counting real minutes would make every drive
look 15 to 20 times worse than the same driving on a real road. In game time the summary's driving time
and distance match the game's own job figures. The events themselves are measured live, in real g and real
km/h, as a box in a real cab would. The score shows from 10 game minutes of driving.

Jobs saved by 0.12.0 counted real time; the app converts them using the job's own distance from the game.

## Traffic light

The Driving alerts page can show the alerts as cards, as a traffic light, as both or as neither. The light
is three lamps in a row, green on the left, and shows two things:

- **An event**: when harsh acceleration, braking, cornering or speeding reaches yellow or red, the light
  switches to that lamp and flashes it (a quarter of a second on, a quarter off) while the event lasts and
  for the same 4 seconds after it as a card. With several at once, the worst one flashes.
- **Between events**: one lamp stays lit, steady, for how the recent driving has gone. It's the
  [safety score](#safety-score) of the last 30 minutes of driving (15 or 60 can be picked instead), rolling
  forward as you drive: green under 20, yellow under 40, red from 40. One harsh moment doesn't change it,
  but a run of them keeps it yellow or red until they fall out of the window behind you.

The window counts real minutes spent moving, so a break at a stop doesn't wash a bad stretch away and the
paused game doesn't age it; the score itself is per 10 game hours, as in the summaries. Until the window
holds 2 game hours of driving (about 6 real minutes on roads), the score is taken over 2 hours anyway, so
one event in the first minute doesn't turn it red: it takes 4 points (yellow 1, red 2) to show yellow and 8
to show red then. Over a full 30-minute window on roads (about 9½ game hours) yellow takes about 19 points
and red about 38. Only the events the alerts show count (speeding only with its alerts on), and only while
the alerts are on for that vehicle. The score starts afresh each time the app starts.

It's click-through, hidden while the game is paused and in a vehicle whose alerts are off. Pick its corner,
size and window on the same page.

## The game's g-force meter

The game has its own switch to hide the coaches' g-force meter on the route advisor, in its options, so no
mod is needed. 0.12.0 to 0.12.2 could hide it with a mod (first Coach comfort, then HUD); from 0.12.3 the HUD
mod no longer ships those files, and the app says so once when it rebuilds the mods for someone who had it on.

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

## Make optional

Convoy only lets players without a mod join when the mod's `manifest.sii` says `mp_mod_optional: true`.
Many mods that only change your own vehicle don't, such as engine, gearbox or tire packs. The Make optional
page copies such a mod into the mod folder as `ets2u_opt_<id>_<tag>.scs` (`<id>` is the Workshop ID, or the
file name of a local mod): every file as it is, picture and description included, and the manifest with
`mp_mod_optional: true` added and "(optional)" after its name. Nothing else in the mod changes.

- The original isn't touched. Editing a Workshop item in place wouldn't last: Steam puts its own files back
  when the item updates or the game files are verified, and Convoy tells Workshop mods apart by their ID.
- Pick subscribed Workshop items from the list, or add a local `.scs`, `.zip` or folder. Workshop titles are
  read from Steam, since many manifests have no name.
- On start, a copy whose original changed (Steam updated it) is rebuilt. Unticking a mod removes its copy;
  **Remove** also forgets it.
- In Mod Manager, enable the copy and switch the original off, or the mod loads twice. The page says which
  profiles still have the original on.
- Physics of your own vehicle (engine, gearbox, tires) is worked out on your PC, so you get the full effect.
  What players without the mod see or hear of your vehicle depends on the game; test it in a convoy.
- The copies are for your own use. Don't share them, and if a creator asks that their mod isn't modified,
  ask them first.

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
| Make optional | The copy is optional for others; what you change (an engine, tires) applies to your own vehicle |
| HUD (map position) | Only changes your HUD |
| Bigger earnings | Applies to your own jobs; not checked for jobs shared by the host |
| Money & XP | Edits your own profile's save; other players aren't affected |
| Speed overlay, driving alerts, traffic light, trip and job summaries, mouse driving | Run on your PC only |

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
| Generated mods and optional copies | `Documents\Euro Truck Simulator 2\mod\ets2u_*.scs` |
| Speed plugin | `<game>\bin\win_x64\plugins\ets2u_telemetry.dll` |
| Mouse driving settings (read by the plugin) | `%APPDATA%\ets2u\mouse_drive.ini` |
| Saved destination display texts | `%APPDATA%\ets2u\destination_texts.txt` |
| Coach job summaries | `%APPDATA%\ets2u\coach_jobs.json` |
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
