Sandstorm Local, Server & Mod Manager
=====================================

Match setup, mods, rules, live control and the dedicated server for
Insurgency: Sandstorm. Other languages: Settings > Language.

Needs: Windows 10/11, Insurgency: Sandstorm, .NET Framework 4.8.


Use
---
1. Extract the zip anywhere and run SandstormModLauncher.exe. No installer.
2. Play: pick map and scenario, set squad and rules, press LAUNCH (F5).
   The game is started and the map loaded over its own RCON. Nothing is typed.
3. Server: installs the dedicated server (SteamCMD, on button press), sets it up
   and starts it with your Play match. Shows join info and the players.
4. Problems: Settings > "Something went wrong?" saves a report.


What it does on your PC
-----------------------
- Reads game files, game log and the mod folder.
- Writes match rules to Game.ini and F10 to Input.ini, only while the game is
  closed, backed up first.
- Talks to the game over RCON on 127.0.0.1 with a random password. Keys are
  typed only as a fallback, only into the game window, after the console line
  is seen open.
- Server page only: writes the server's config files (backed up first) and
  starts/stops the server.
- Settings and logs: %APPDATA%\SandstormModLauncher
- No telemetry. Network: update check against the GitHub releases (Settings >
  About turns it off), and SteamCMD/server download when you press Install or
  Update.
- Game files and anti-cheat are not touched. Local play only.


SmartScreen / antivirus
-----------------------
The exe isn't code-signed: "More info" > "Run anyway". Some antivirus tools
dislike programs that can send keystrokes; this one only does it as a fallback.
Built by GitHub Actions from the public source. SHA-256 is on the release page:
  Get-FileHash .\SandstormModLauncher.exe -Algorithm SHA256


Updates
-------
Checked every 3 minutes while open; the new zip is verified against its SHA-256
and used from the next start.


Uninstall
---------
Settings > "Remove launcher rules from Game.ini", then delete this folder and
%APPDATA%\SandstormModLauncher.


Source and issues: https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher

License: open source for non-commercial use, PolyForm Noncommercial 1.0.0, see
LICENSE.txt. The Oswald font is under the SIL Open Font License,
see OFL-Oswald-font.txt.
Not affiliated with New World Interactive or Focus Entertainment.
