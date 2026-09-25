Local play launcher for Insurgency: Sandstorm: bots, mods, mutators and full match rules offline.

**Changes in 1.3.7**

- Fixed: Ambush and Free For All stayed at "waiting for players" with no bots. Both modes wait for two human players before the match starts, and bots only join after that. With bots on, one player is now enough, the modes get a bot count (5 when none is set) and enough player slots for all bots.
- Mods are now a tab of Play (Map, Rules, Playlists, Live, Mods, Advanced), next to everything else you set up for a match. The left bar has Play and Settings.
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
