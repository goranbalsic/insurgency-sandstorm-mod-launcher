# Contributing

Forks and pull requests are welcome. New features, fixes and support for more maps and mods all help.

## Getting started

1. Fork the repository and clone your fork.
2. Build it: `dotnet build -c Release` (needs the .NET SDK 8 or newer on Windows). The exe ends up in `bin\Release\net48\`.
3. Test with your own data folder so your real settings stay untouched: `SandstormModLauncher.exe --data C:\some\test-folder`.

## Before you open a pull request

- Run the tests that fit your change. Each one prints `ALL CHECKS PASSED` when it is happy:
  - `--data <copy of a data folder> --cli torture 3000 1`: the setup logic (maps, rules, presets, mutators, profiles)
  - `--data <copy of a data folder> --ui-torture out.txt 1000 1`: the window, driven at random
  - `--data <copy of a data folder> --cli rcon-torture 400 1`: the RCON code, against a fake game that misbehaves
- Keep the style of the code around your change and the plain wording of the texts on screen.
- Describe what your change does and how you tested it. A screenshot helps for anything on screen.

## Ideas and bugs

Have an idea but no time to build it, or found a bug? Open an issue and describe it. For bugs, the launcher can make a cleaned problem report (Settings > Something went wrong?).

## License

The project is open source for non-commercial use under the PolyForm Noncommercial License 1.0.0 (see LICENSE). By sending a pull request you agree that your change is shared under the same license.
