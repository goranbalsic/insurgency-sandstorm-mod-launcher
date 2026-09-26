Local play launcher for Insurgency: Sandstorm: bots, mods, mutators and full match rules offline.

**Changes in 1.6.0**

- Launching no longer types into the game. The launcher now talks to the game through its own remote console (RCON, the one server admins use), on this PC only (127.0.0.1) with a random password. Maps load and rules are set without any key presses, the game can stay in the background, and the game confirms every step. This fixes the command that sometimes stayed in the console until you pressed Enter yourself, and the focus problems around it.
- Rules changed in a running match are set over RCON too, and the game answers with each new value. The Live tab's round restarts, live rule changes, "Read current values" and "Count bots" work the same way.
- Cheats and the versus AI difficulty still need the game's console (the game takes them from nowhere else). That typing is safer now: every key press is checked on screen before the next one, Enter is only pressed on a line that is verified, and the console is recognised even when the game's suggestion box covers part of it (the cause of the failures right after a map loaded). Settings can turn all typing off.
- New in Settings: "Game connection (RCON)" shows whether the launcher reaches the game, with a Check button. A game started before this update is offered a one-time restart.
- Extra URL options (Advanced) now replace the launcher's value for the same key instead of sending it twice.
- The game is closed over RCON when the launcher needs to restart it.

**Changes in 1.5.1**

- Fixed: in versus, "Fill teams with bots" could not be turned off.
- Fixed: official versus rulesets and playlists no longer switch the bots off (online they are played without bots; offline that left an empty match).
- Fixed: extra Game.ini lines that add to a list (+Key=...) were written again on every launch, and removed ones stayed in Game.ini. Free For All rules are now cleaned up like every other mode.
- Fixed: two profiles whose names differ only in characters Windows does not allow in file names (like a/b and a?b) overwrote each other, and names like CON could not be saved.
- Fixed: a custom mutator ID, extra URL options or a mode override with a space broke the open command. They are now checked, and the launcher says what it left out.
- Fixed: after "Save setup" the match summary kept showing the previous preset name. It now also says "(changed)" once you change something after applying a preset.
- Live rule changes only accept one clean value per setting, so a stray | or space can no longer run a second console command.
- The launch plan warns when minimum enemies is above maximum enemies.

**Changes in 1.5.0**

- Presets rebuilt from the ground up. A rules preset now replaces the rules of the preset before it and sets exactly its own values, instead of adding to whatever was there. Squad presets only change the bot values, so a squad and a rules preset can be combined in any order.
- New Squad tab in Play: squad presets (Lone Wolf, Fireteam, Squad Leader, Full Platoon, 1 v 1 to 16 v 16, relaxed or elite bots) next to the teammate, enemy and AI difficulty values.
- "Save setup" keeps the whole match (map, scenario, day or night, bots, every rule and the mutators) as one preset under Rules > Presets > Saved, and loading it brings all of it back.
- Calmer Play layout: a "Your match" column shows the whole setup on every tab; click a line to change it. At small window sizes or large text it makes room, and the page no longer scrolls sideways at 150%.
- Fixed: "access to the path is denied" when Game.ini is read-only. The launcher writes it and keeps it read-only. A Game.ini that another program briefly holds open is retried instead of failing.
- Fixed: versus with bots and a team size of 0 could start with no bots at all. The smallest team size is now 1.
- Values in hand-edited or older profiles are checked when loading; anything the game or the open command cannot take is removed.
- Send a problem report straight from the launcher: Settings > "Something went wrong?" > "Send a report...". You see the whole cleaned report first and nothing leaves your PC until you press Send. Posting it on GitHub still works.
- Mods tab: "Load the mutators" switch next to the mutator list.

**Changes in 1.4.0**

- Fixed: the Styles presets did nothing when a versus scenario was picked (they only changed co-op modes). Styles now follow the scenario on the map: co-op styles (Lone Wolf, Fireteam, Squad Leader, ...) for co-op, versus styles (1 v 1, 5 v 5, 10 v 10, 16 v 16, relaxed or elite bots, quick rounds, ...) for versus.
- Playlists are now a preset tab in Play > Rules (Styles, Official, Playlists, Mine) instead of their own page. Applying one adds its mutators and rules to the map you picked; it no longer jumps to a random map.
- Every preset is tagged Co-op (PvE: solo or with AI teammates) or Versus (played against bots offline).
- Presets that change nothing are gone: 56 playlists that only picked maps, and official rulesets whose changes only exist in the game-start ruleset option (Advanced).

**Changes in 1.3.9**

- Small maintenance release: same launcher as 1.3.8, published for Windows and Linux on mod.io so the page shows the current version everywhere.

**Changes in 1.3.8**

- Playlists has its own page again, in the left bar between Play and Settings, using the full width. Play keeps Map, Rules, Mods, Live and Advanced.
- The updater also installs a release that was rebuilt without a new version number.

**Changes in 1.3.7**

- Fixed: Ambush and Free For All stayed at "waiting for players" with no bots. Both modes wait for two human players before the match starts, and bots only join after that. With bots on, one player is now enough, the modes get a bot count (5 when none is set) and enough player slots for all bots.
- Mods are now a tab of Play (Map, Rules, Playlists, Mods, Live, Advanced), next to everything else you set up for a match. The left bar has Play and Settings.
- The Mods tab keeps its lists readable at large text sizes.

**Changes in 1.3.6**

- New license: source available, all rights reserved. The code stays public so anyone can read and check it, but it may not be copied, redistributed or reused in other projects (personal ones included) without permission. Versions up to 1.3.5 keep their MIT license.

**Changes in 1.3.5**

- Large text sizes: every page scrolls when it no longer fits the window (the Rules tab could not be scrolled at 150%). The Rules categories wrap under the search box, and the setup column, rule rows and live rule rows give up width instead of squeezing their text.

**Changes in 1.3.4**

- Fixed: drop-down lists that show a name (saved key binding copies, presets) showed a program name instead of the entry.

**Changes in 1.3.3**

- Text size: Settings > Text size (90% to 150%), or Ctrl + / Ctrl - / Ctrl 0 and Ctrl + mouse wheel anywhere. Scales the whole window, tooltips and menus. The smallest labels are also a bit bigger by default.
- Shorter descriptions everywhere, so pages are less crowded.
- New versions are found within minutes: while the launcher is open it checks every 3 minutes with a tiny request (just where the latest release page points to, no data downloaded unless there is a new version). Offline, it stays quiet.

**Changes in 1.3.2**

- Report a problem on GitHub from the launcher: Settings > Something went wrong? > Report on GitHub (or "Report this problem" after a failed launch). You see the whole report first; it has no user or PC name, user folders, Steam IDs, account lines, screenshots or console history. Your browser opens the issue form filled in, and nothing is posted until you press Submit.
- Fixed "A keyboard key is held down" when no key is pressed: a key Windows reports as held for seconds while you are in the launcher (a remapping tool, macro software or a controller mapped to keys) is now ignored. Shift, Ctrl, Alt and Windows still have to be let go, and a key held while you are in the game still stops the send. The message now names the key.

**Changes in 1.3.1**

- New look: plain Windows-style layout with square corners, normal text and classic tabs.
- The Play tabs no longer jump around: the tab row is fixed, and the scenario and squad column stays on every tab.
- Console: faster and calmer. The line is only cleared as far as needed, the quick console reopen before Enter no longer waits on a step that never shows, and the launch waits 2 s less for the main menu.

**Changes in 1.3.0**

- Updates itself: every few hours the launcher looks at this project's GitHub releases, checks the new zip against its SHA-256 and quietly puts the new exe in place. It is used from the next start; "Update ready" next to the version (bottom left) restarts right away. Settings > About turns it off.
- Fixed: renaming a profile to other letter case, switching profiles while mods were being read, co-op AI difficulty compared with a fixed 0.5, official playlists counting default values as rule changes, key binding copies of profiles with a dash in the name.
- Saving a rules preset under an existing name now asks first. "Remove launcher rules" checks the game is closed before asking.
- Disabled menu items look disabled, focused text boxes keep their border, and help texts point at the real pages.

**Changes in 1.2.4**

- Co-op with AI teammates always gets at least 8 player slots (teammates did not join with only 2 or 3), and the teammate count is also sent after the map loads.

**Changes in 1.2.3**

- Co-op AI teammates: the match is now started with bots enabled (bBots), which co-op modes have off by default.

**Changes in 1.2.2**

- AI teammates: the solo-game flag stopped them from joining; it is left out when co-op has teammates.
- Console: a long command no longer confuses the console check (it opened the big console and gave up before Enter). If Enter still fails, the launch waits for you to press it in the game.
- Versus is played against bots by default. Squad settings apply to all co-op modes together and to all versus modes together.

**Changes in 1.2.1**

- Fixed: the command was typed into the console but Enter did not run it. Right after the game window comes to the front, the game's menu can take the keyboard back, and then Enter presses a menu button instead. The launcher now closes and reopens the console right before Enter (the console takes the keyboard when it opens) and checks the line is still there. Checked in the game at the main menu and in a match.
- Waits longer for the main menu to settle before using the console.

**Changes in 1.2**

- AI teammates work again: Game.ini had collected many copies of the same settings (the game rewrites the file and drops the launcher's markers), and the game used the oldest one. Settings are now written by key, one line each, and old copies are cleaned up. Game.ini is only written while the game is closed.
- Player slots grow automatically to fit you plus your AI teammates.
- Console: uses ` / ~ when your keyboard has it and F10 otherwise, tries the other key if the first shows nothing, and uses a console that is already open.
- Commands after the map loads wait for the loading screen to finish (they were sent under it before).
- Play has Map, Rules, Playlists, Live and Advanced tabs; Mods has Mutators and Installed mods. Official mutators are grouped by the co-op and versus playlists that use them.

**Changes in 1.1**

- The console is now checked on screen: keys are only typed after the console line is seen open, otherwise nothing else is pressed. Earlier versions could send Backspace/Enter into the game menus when the console did not open (for example on a non-English keyboard layout).
- F10 is added as a console key automatically while the game is closed, so every keyboard layout works.
- Copies of your game key bindings are kept before any key is sent, with a restore option in Settings.
- Play and Rules are one page (Map, Rules and Advanced tabs); nothing is shown twice.
- Versus with bots uses team sizes (1v1, 5v5, 10v10 or any size).
- Fully offline: no mod.io connection, mod logos come from the game's own cache.
- Logs for every run and a one-click problem report (Settings > Something went wrong?).
- The game folder is detected in more places, and the Settings page now shows it right away.

**Download** `SandstormModLauncher-{VERSION}.zip`, extract it and run `SandstormModLauncher.exe`. Windows 10/11, no installer, no admin rights. `readme.txt` has the details.

This release was built by GitHub Actions from the source at this tag: {RUN_URL}

**SHA-256**

```
{SHA256}
```

**Check your download**

```powershell
Get-FileHash .\SandstormModLauncher-{VERSION}.zip -Algorithm SHA256
gh attestation verify .\SandstormModLauncher-{VERSION}.zip --repo {REPO}
```
