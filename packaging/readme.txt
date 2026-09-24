Sandstorm Mod Launcher
======================

Local play launcher for Insurgency: Sandstorm.
Play offline with bots, mods and mutators, and change any match rule
without typing console commands.


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
   The launcher starts the game, waits for the main menu and loads the match.
   Keep your hands off the keyboard for the few seconds it uses the game console.
4. Something wrong? Settings > "Something went wrong?" saves a report with
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
- Opens the game console and pastes the match command. It checks on screen
  that the console line is open before it types anything, and only types
  while the Insurgency: Sandstorm window is in front.
- Keeps its settings and logs in %APPDATA%\SandstormModLauncher
- No network access, no telemetry.
- Never changes game files and does not touch the anti-cheat. Local play only.


Windows SmartScreen / antivirus
-------------------------------
The exe is not code-signed, so Windows may show "Windows protected your PC".
Click "More info" > "Run anyway".
Some antivirus tools are wary of programs that send keystrokes. This one sends
them only to the game window, to use the console.

This zip was built by GitHub Actions from the public source code.
The release page lists the SHA-256 of every file. To check yours, run in PowerShell:
  Get-FileHash .\SandstormModLauncher.exe -Algorithm SHA256
You can also build it yourself from the source code.


Uninstall
---------
Use Settings > "Remove launcher rules from Game.ini" if you changed rules,
then delete this folder and %APPDATA%\SandstormModLauncher


Source code, updates and issues
-------------------------------
https://github.com/goranbalsic/insurgency-sandstorm-mod-launcher


License
-------
MIT, see LICENSE.txt
The Oswald font is under the SIL Open Font License, see OFL-Oswald-font.txt
Not affiliated with New World Interactive or Focus Entertainment.
