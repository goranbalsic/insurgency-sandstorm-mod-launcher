Local play launcher for Insurgency: Sandstorm: bots, mods, mutators and full match rules offline.

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
