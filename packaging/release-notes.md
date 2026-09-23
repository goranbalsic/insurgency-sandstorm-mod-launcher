Local play launcher for Insurgency: Sandstorm: bots, mods, mutators and full match rules offline.

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
