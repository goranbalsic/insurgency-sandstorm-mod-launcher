# Contributing

Forks and pull requests are welcome. New features, fixes and support for more maps and mods all help.

## Getting started

1. Fork the repository and clone your fork.
2. Build it: `dotnet build -c Release` (needs the .NET SDK 8 or newer on Windows). The exe ends up in `bin\Release\net48\`.
3. Test with your own data folder so your real settings stay untouched: `SandstormModLauncher.exe --data C:\some\test-folder`.

## Before you open a pull request

- Run the tests that fit your change. Each one prints `ALL CHECKS PASSED` when it is happy:
  - `--data <copy of a data folder> --cli torture 3000 1`: the setup logic (maps, rules, presets, mutators, saved setups)
  - `--data <copy of a data folder> --ui-torture out.txt 1000 1`: the window, driven at random
  - `--data <copy of a data folder> --cli rcon-torture 400 1`: the RCON code, against a fake game that misbehaves
  - `--cli translation-check <repo folder>`: the translation table is up to date (after changing texts, make it again with `--cli translation-template <repo folder>`)
- Keep the style of the code around your change and the plain wording of the texts on screen. New texts on screen go through `T("...")`, or `F("... {0} ...", value)` when a value is in them, so they can be translated; texts written in the XAML pages are translated on their own.
- Describe what your change does and how you tested it. A screenshot helps for anything on screen.

## Translations

No programming needed. In the launcher, Settings > Language > "Save a translation file..." saves a table with every text of the launcher. Fill in its Translation column with Excel, Google Sheets or LibreOffice:

- The first two rows are the name of your language, written in it (for example 简体中文), and your name or nickname if you want it shown in Settings.
- The Where column tells where a text appears. Keep `{0}`, `{1}` and so on: the launcher puts values there (a number, a map name). Keep line breaks inside a text.
- Leave a row empty if you are unsure; that text stays in English.

Save it as CSV UTF-8, put it in the languages folder (Settings > Language > "Open the languages folder") and press Reload to see it in the launcher. When you are happy with it, send it in as a pull request that adds it to `Resources/Languages` as `<language code>.csv` (for example `zh-CN.csv`, `es.csv`), or attach it to an issue. To update a translation later, pick your language and save the translation file again: your translations are filled in and new texts are empty.

## Ideas and bugs

Have an idea but no time to build it, or found a bug? Open an issue and describe it. For bugs, the launcher can make a cleaned problem report (Settings > Something went wrong?).

## License

The project is open source for non-commercial use under the PolyForm Noncommercial License 1.0.0 (see LICENSE). By sending a pull request you agree that your change is shared under the same license.
