# Sandstorm Local, Server & Mod Manager

Set up an Insurgency: Sandstorm match once (map, bots, rules, mutators) and play it offline or run it on a dedicated server. No console typing.

[![Build](https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher/actions/workflows/build.yml/badge.svg)](https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/goranbalsic/insurgency-sandstorm-mod-launcher)](https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher/releases/latest)
[![License: open source, non-commercial](https://img.shields.io/badge/license-open%20source%2C%20non--commercial-blue.svg)](LICENSE)

![Play page](docs/screenshots/play.png)

## Features

- Every official map and scenario plus mod maps, day or night, Hardcore Checkpoint
- Squad, enemy counts and AI difficulty; 100+ rules per mode; all official and mod mutators
- Presets: play styles, official rulesets and the official online playlists
- Saved setups: the whole match in one file
- Live page: restart rounds, change rules mid-match, any console command
- Dedicated server: install with SteamCMD, server types (Co-op, Hardcore, Frenzy, Push, Competitive), map cycle, mods, RCON player control, join info, or your own .bat options
- Any language, from a CSV table
- Offline except the update check and the buttons that call Valve or mod.io

| Rules | Playlists | Live |
| --- | --- | --- |
| ![Rules](docs/screenshots/rules.png) | ![Playlists](docs/screenshots/playlists.png) | ![Live](docs/screenshots/live.png) |
| **Mutators** | **Squad** | **Server** |
| ![Mutators](docs/screenshots/mutators.png) | ![Squad](docs/screenshots/squad.png) | ![Server](docs/screenshots/server.png) |

## Download

Zip from [Releases](https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher/releases/latest), extract, run `SandstormModLauncher.exe`. No installer, no admin rights. It updates itself.

Needs Windows 10/11, Insurgency: Sandstorm and .NET Framework 4.8 (in Windows 10 1903+).

## How it works

1. Rule changes go to `Game.ini` while the game is closed (backed up first), one line per key.
2. The game is started if needed; the launcher follows its log to the main menu.
3. `open <map>?...` is sent through the game's RCON (127.0.0.1 only, random password), as a `defer` console command. The game confirms the load in its log. Nothing is typed.
4. If the game is already running, rules are set over RCON.

Rules already in your own `Game.ini`? Switch on Settings > Launching > "Use the rules in my Game.ini". The launcher then writes only its RCON section there and sends no rules, bots or AI teammates: Play gives the map, scenario, lighting and mutators. The game reads `Game.ini` when it starts, so restart it after editing the file. Rules the launcher wrote earlier stay in the file until you remove them (by hand, or with "Remove launcher rules from Game.ini", which removes every rule line).

Cheats, AI difficulty and after-load commands use the same path. Typing into the console is a fallback for a game RCON can't reach: it checks the console line on screen before each key and adds F10 as a console key for layouts without `` ` ``.

Settings > Launching starts the game's own exe or a command of yours instead of Steam.

## Dedicated server

The Server page installs the server with SteamCMD (about 5 GB, folder of your choice) and updates it. An existing install is found or picked.

1. Pick a server type or set up the match on Play.
2. Fill in name, ports, slots, password, admins, mods and a GSLT if you want it in the browser.
3. Start. The launcher writes `Game.ini` and `Admins.txt` (backed up first), starts `InsurgencyServer.exe` and waits for RCON.

The match's mutators go on the command line (`-mutators=`), so map cycle entries with their own `?Mutators=` add to them. Rules already in the server's `Game.ini`? Switch on "Use the rules in the server's Game.ini" on the Match card: the launcher then writes only RCON and vote kick there. Game stats need a token from gamestats.sandstorm.game, a GSLT and no join password.

Optional: a message of the day (`Motd.txt`), the tick rate (`NetServerMaxTickRate` in `Engine.ini`), and restarting the server when it crashes (while the launcher is open).

Already have a .bat? Switch on "Start with my own options" and paste its options or import the file. They are used as-is; the launcher only adds a waiting security code, and its RCON to `Game.ini` if you have none.

Running, the page lists players (kick, ban, unban, chat, timed messages, restart round, any admin command). It also shows the join address, ports to forward and the Windows Firewall state, and says whether the server loaded its mods.

Mods (game update 1.20+): the server needs its own mod.io account, separate from the one you play with. Enter its e-mail on the Mods card, press Send code, type the 5-digit code and start the server: it logs in once (`-SecurityCode=<code>`, later starts use `-SecurityCode=none`) and loads the mods that account is subscribed to. Subscribe subscribes it to the mod list; Import Mods.txt fills the list from an old Mods.txt. Mods.txt and the access token are no longer read by the server.

A server on another PC: switch to "On another PC (RCON)" and enter address, port and password.

## Translations

Settings > Language > "Save a translation file...", fill in the Translation column in a spreadsheet (keep `{0}`, `{1}` and line breaks), save as CSV UTF-8 into the languages folder, press Reload. Send it as a pull request to `Resources/Languages/<code>.csv`. Untranslated texts stay English.

New texts in code: `T("...")`, or `F("... {0} ...", v)`. `--cli translation-template <repo>` updates the table; `translation-check` fails when it's stale.

## What it touches

- Reads game paks, configs, log and the mod folder.
- Writes match rules and its RCON settings to `Game.ini`, and F10 to `Input.ini`, only while the game is closed, with backups.
- Keeps copies of your key bindings before sending any key; never edits them.
- Server page only: the server's config files (see above) and `InsurgencyServer.exe`. It reads the server's mod.io login state from `%LOCALAPPDATA%\mod.io\254\ModServer` (never shows the token).
- Settings and logs in `%APPDATA%\SandstormModLauncher`.
- Problem reports are sent only when you press Send, after showing you all of it.
- Network: the update check (release zip verified against SHA256SUMS.txt; Settings > About turns it off). SteamCMD from Valve (signature checked) and the server download, only when you press Install or Update. mod.io, only when you press Send code or Subscribe.
- No telemetry. Game files and anti-cheat are not touched. Local play only.

The exe isn't code-signed, so SmartScreen may warn (More info > Run anyway). Verify a download:

```powershell
Get-FileHash .\SandstormModLauncher-vX.Y.Z.zip -Algorithm SHA256
gh attestation verify .\SandstormModLauncher-vX.Y.Z.zip --repo goranbalsic/insurgency-sandstorm-mod-launcher
```

## Troubleshooting

Settings > "Something went wrong?" saves a report to `%APPDATA%\SandstormModLauncher\logs\reports` (no user names, IDs, IPs or screenshots). "Send a report..." shows it first; "Post it on GitHub instead" opens the [issue form](https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher/issues/new?template=problem-report.yml) filled in.

"Access to the path is denied" on `Game.ini`: another program has it open or the folder is protected.

## Build

.NET SDK 8+ on Windows:

```
dotnet build -c Release
```

Exe: `bin\Release\net48\`. Only release builds (`-p:OfficialBuild=true`) replace themselves on update.

CLI (see `Services/Cli.cs`):

- `--selftest report.txt`, `--render folder` (pages to PNG), `--data folder` (separate settings/logs)
- `--cli status | presets | apply | set | mutators | plan | save | reset`
- `--cli torture [steps] [seed]`: random stress test of the setup logic
- `--cli rcon-torture [steps] [seed]`: RCON code against a fake, misbehaving game
- `--cli server-status | server-plan | server-start | server-stop | server-players | server-rcon "cmd" | server-install | server-type <id>`
- `--ui-torture out.txt [steps] [seed]`: drives the real window at random and checks screen, profile and launch plan agree. Use with `--data` on a copy.

## Contributing

Forks and pull requests welcome. Run the tests that fit the change, then open a PR against `main`. See [CONTRIBUTING.md](CONTRIBUTING.md).

Releasing: raise `<Version>` in the csproj, add a section to `packaging/release-notes.md`, push to `main` or push a `vX.Y.Z` tag. The [workflow](.github/workflows/release.yml) builds and publishes.

## FAQ

**Mods from the in-game browser?** Subscribe in game, start it once so they download; they show up here.

**Does it change online play?** No. The `Game.ini` block only affects matches you host. Settings > "Remove launcher rules from Game.ini" takes it out.

**Why does the launcher want to restart the game?** The game reads AI teammates, the official ruleset and your own Game.ini lines only at startup (with "Use the rules in my Game.ini", the whole file). The launcher asks first.

**Steam or Epic?** Tested with Steam; Epic installs are detected.

**Own MapCycle.txt on the server?** Server page > map cycle > "Use another file...".

## License

Open source for non-commercial use, under the [PolyForm Noncommercial License 1.0.0](LICENSE). Use, change, fork and share it for anything that doesn't make money; selling it or using it in a commercial product or service is not allowed. Keep the license and its `Required Notice` line with every copy. Earlier versions keep the license they were released with. The Oswald font is under the SIL Open Font License ([Assets/OFL.txt](Assets/OFL.txt)).

Insurgency: Sandstorm is a trademark of its owners. Not affiliated with New World Interactive or Focus Entertainment.
