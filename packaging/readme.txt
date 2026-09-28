Sandstorm Mod Launcher
======================

Launcher for Insurgency: Sandstorm.
Play offline with bots, mods and mutators, and change any match rule
without typing console commands. Server admins can run the same setup on the
dedicated server and control it live. Other languages: Settings > Language.


Requirements
------------
- Windows 10 or 11
- Insurgency: Sandstorm
- .NET Framework 4.8 (part of Windows 10 1903 and newer)


How to use
----------
1. Extract this zip to any folder.
2. Run SandstormModLauncher.exe. No installer, no admin rights.
3. On PLAY pick a map and scenario, set your squad and the enemies, and change
   any other rule on the Rules tab. Press LAUNCH (or F5).
   The launcher starts the game, waits for the main menu and loads the match
   through the game's own remote console (RCON). Nothing is typed into the game.
4. Running a dedicated server? The Server page starts it with the Play match,
   its own settings and map cycle, and shows the players while it runs.
5. Something wrong? Settings > "Something went wrong?" saves a report with
   everything needed to find the cause.


What it does on your PC
-----------------------
- Reads the game files, the game log and the game's mod folder (read only).
- Writes only match rules (game mode settings) to Game.ini, one line per
  setting, while the game is closed. A backup is made first.
- Adds F10 as a console key to Input.ini while the game is closed (backup
  first). The ` key is missing on many keyboard layouts; F10 works on all.
- Keeps copies of your game key bindings before it sends any key to the game,
  and can put them back (Settings > Game key bindings). It never edits them.
- Talks to the game over RCON on 127.0.0.1 (this PC only) with a random
  password. Typing into the game console is only a fallback for a game it
  cannot reach that way; it checks on screen that the console line is open,
  and only types while the Insurgency: Sandstorm window is in front.
- Only when you use the Server page: writes the dedicated server's config files
  (Game.ini, Engine.ini, Admins.txt, MapCycle.txt, Mods.txt, backed up first)
  and starts or stops the server.
- Keeps its settings and logs in %APPDATA%\SandstormModLauncher
- No telemetry. The only network access is the update check against the
  project's GitHub releases (Settings > About turns it off).
- Never changes game files and does not touch the anti-cheat. Local play only.


Windows SmartScreen / antivirus
-------------------------------
The exe is not code-signed, so Windows may show "Windows protected your PC".
Click "More info" > "Run anyway".
Some antivirus tools are wary of programs that can send keystrokes. This one
sends them only to the game window, and only as a fallback.

This zip was built by GitHub Actions from the public source code.
The release page lists the SHA-256 of every file. To check yours, run in PowerShell:
  Get-FileHash .\SandstormModLauncher.exe -Algorithm SHA256
You can also build it yourself from the source code.


Updates
-------
The launcher updates itself. Every 3 minutes while it is open it checks the GitHub releases (a tiny request),
verifies the new zip against its SHA-256 and replaces its own exe. The new
version is used the next time you open it (or click "Update ready" at the
bottom left). Nothing is installed anywhere else.


Uninstall
---------
Use Settings > "Remove launcher rules from Game.ini" if you changed rules,
then delete this folder and %APPDATA%\SandstormModLauncher
(a SandstormModLauncher.exe.old file next to the exe after an update is
removed automatically at the next start).


Source code, updates and issues
-------------------------------
https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher


License
-------
Open source for non-commercial use: PolyForm Noncommercial License 1.0.0, see
LICENSE.txt. Use it, change it, fork it and share it, as long as nothing makes
money from it. Forks, pull requests and translations are welcome on GitHub
(link above).
The Oswald font is under the SIL Open Font License, see OFL-Oswald-font.txt
Not affiliated with New World Interactive or Focus Entertainment.
