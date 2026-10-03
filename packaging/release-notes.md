**1.12.0**

- Settings > Launching > "Use the rules in my Game.ini": your game keeps the rules, bots and AI teammates you set in Game.ini. The launcher writes and sends none; Play gives the map, scenario, lighting and mutators.
- The dedicated server still gets Play's rules (it has its own switch for its Game.ini).

**1.11.0**

- Server: keep the rules in its own Game.ini, game stats token, match mutators on the command line.
- AI teammates: the number shown is the AI you get (the game counts you too).
- Rules put back to default reach a running game; switched-off rules stay off on map loads.
- Hand edits to the map cycle, vote kick lines and Admins.txt are kept.
- Problem reports hide the server token and mod.io code; names from mod files are checked.

**1.10.0**

- Server mods work again. Since game update 1.20 the server logs in to mod.io with an e-mailed security code and loads the mods its own account is subscribed to.
- Mods card: send the code, enter it, subscribe the server's account to your mod list. Mods.txt can be imported into the list.
- The old token and Mods.txt are no longer used; the old token section is removed from the server's ini files.
- Start with your own options: paste your server's command line or import your .bat.
- Mod status also shows a refused code, mods still downloading and a server on your game's own account.

**1.9.1**

- Server page shows whether the server actually loaded its mods (reads its log).
- mod.io token is written to Engine.ini and GameUserSettings.ini and restored on every start.
- Warns before start if mods are on and no token is saved.

**1.9.0**

- Install and update the dedicated server from the Server page (SteamCMD, only when you press the button).
- Server types: Co-op, Co-op Hardcore, Co-op Frenzy, Push, Competitive.
- Join info: LAN address, ports to forward, Windows Firewall state.
- Options: vote kick, official rules.

**1.8.1**

- Saved setups keep their preset, so the next preset removes its mutators (Hardcore no longer sticks).
- Checkpoint playlists set hardcore like the official ones; mutator playlists switch mutators on.
- Free For All, Ambush and Defusal show the 5 bots the launch sends. Rule count matches the Rules tab.
- Setups on a deleted custom map or missing mod map load on the scenario's map and count as changed.
- Only the applied preset is marked in use when names repeat.

**1.8.0**

- Renamed to Sandstorm Local, Server & Mod Manager.
- A preset removes the previous preset's rules, mutators, night and hardcore.
- Saved setups replace profiles and mutator presets. Old ones are converted.
- The big button can start the dedicated server. Live is its own page.
- Installed mods lists only mods on disk.
- Preset search. Added the missing night playlists.

**1.7.0**

- Server page: run the Play match on a dedicated server, start/stop, control over RCON (players, kick/ban, chat, round restart, any command).
- Map cycle editor.
- Translations from a CSV table.
- Start the game without Steam (own exe or command).

**1.6.1**

- Fix: stuck on the class screen after launching from the main menu. The map is now opened as a typed console command.
- Cheats, AI difficulty and Live buttons go over RCON. Typing is only a fallback.
- Fix: false "no RCON" during long map loads. Mod names like "Authentic" no longer hide the map load.

**1.6.0**

- Launch and rule changes go over RCON (127.0.0.1, random password) instead of typing into the game.
- Settings: RCON status and check.
- Typing fallback verifies each key on screen.
- Extra URL options replace the launcher's value for the same key.

**1.5.1**

- Fix: versus "Fill teams with bots" could not be turned off. Official versus presets no longer turn bots off.
- Fix: stale and duplicate extra Game.ini lines.
- Fix: profile names that collide as file names (a/b, a?b, CON).
- Fix: spaces in custom mutator IDs, URL options and mode override broke the open command. Now validated.
- Live rule values accept one clean value only.

**1.5.0**

- Rules presets replace the previous preset instead of adding to it. Squad presets change bot values only.
- Squad tab. "Save setup" keeps the whole match. "Your match" column on every tab.
- Fix: read-only Game.ini. Fix: versus with team size 0 started without bots.
- Problem report from the launcher, shown in full before sending.

**1.4.0**

- Fix: Styles presets ignored versus scenarios.
- Playlists moved into Play > Rules, tagged Co-op or Versus.
- Removed presets that change nothing.

**1.3.9**

- Same as 1.3.8, published for Windows and Linux on mod.io.

**1.3.8**

- Playlists page back in the left bar. The updater installs releases rebuilt under the same version.

**1.3.7**

- Fix: Ambush and Free For All waited for players with no bots.
- Mods became a tab of Play.

**1.3.6**

- New license, see LICENSE. 1.3.5 and earlier stay MIT.

**1.3.5**

- Pages scroll at large text sizes.

**1.3.4**

- Fix: drop-downs showed a type name instead of the entry.

**1.3.3**

- Text size 90-150% (Ctrl +/-/0, Ctrl + wheel).
- Update check every 3 minutes (a HEAD request, no download unless newer).

**1.3.2**

- Report a problem on GitHub from the launcher.
- Fix: false "A keyboard key is held down".

**1.3.1**

- Plain Windows-style layout, fixed tab row, faster console.

**1.3.0**

- Self-updater, SHA-256 checked. Settings > About turns it off.
- Fixes: profile rename by letter case, key binding copies, AI difficulty compare, playlist rule counts.

**1.2.4**

- Co-op with AI teammates gets at least 8 player slots.

**1.2.3**

- Co-op starts with bBots on so AI teammates join.

**1.2.2**

- Solo-game flag no longer blocks AI teammates.
- Console: long commands, Enter fallback.
- Versus is played against bots by default.

**1.2.1**

- Fix: Enter ignored after the game window came to the front. The console is reopened before Enter.

**1.2**

- Game.ini is written by key, one line each (duplicates broke AI teammates), only while the game is closed.
- Player slots fit the AI teammates.
- Console key ` / ~ or F10. Commands wait for the loading screen.

**1.1**

- Console verified on screen before typing. F10 added automatically.
- Key binding backups with restore.
- Play and Rules merged. Fully offline. Logs and problem report.

**Download** `SandstormModLauncher-{VERSION}.zip`, extract, run `SandstormModLauncher.exe`. Windows 10/11, no installer.

Built by GitHub Actions from this tag: {RUN_URL}

**SHA-256**

```
{SHA256}
```

**Verify**

```powershell
Get-FileHash .\SandstormModLauncher-{VERSION}.zip -Algorithm SHA256
gh attestation verify .\SandstormModLauncher-{VERSION}.zip --repo {REPO}
```
