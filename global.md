# global.md — working notes for this repo

Facts an agent should not have to rediscover. Keep it short; update it when something here becomes wrong.

## What this is
WPF desktop app (C# 10, `net48`, no NuGet beyond reference assemblies) that sets up Insurgency: Sandstorm
matches, writes `Game.ini`, launches the game and talks to its local RCON. Also installs and runs a dedicated
server. Single window, dark theme, no installer, self-updating from GitHub releases.

## Build / test / run
```powershell
dotnet build -c Release                     # exe in bin\Release\net48\
.\bin\Release\net48\SandstormModLauncher.exe --selftest report.txt
.\bin\Release\net48\SandstormModLauncher.exe --ui-torture out.txt 800 3
.\bin\Release\net48\SandstormModLauncher.exe --cli torture 1500 1
.\bin\Release\net48\SandstormModLauncher.exe --cli rcon-torture 150 1
powershell -File tests\run-tests.ps1        # build + translation-check + all four suites
```
Tests are headless and need no game. `tests\fixture` is the fake data folder (`--data`, `--render-from-cache`).

## Layout
| Folder | What lives there |
| --- | --- |
| `Core/` | No UI: loc, JSON, ini, pak/uasset parsing, RCON, input injection, paths |
| `Game/` | Game/server detection, catalog scan, launch planning, launch execution, SteamCMD, mod.io |
| `Models/` | `AppSettings` (persisted) and plain data types |
| `Services/` | Store (settings/profiles), updater, CLI, self-test, the torture suites |
| `ViewModels/` | `MainViewModel` split by feature; all UI state and commands |
| `Views/` | XAML: `MainWindow`, `Pages/*`, `Controls/Stepper` |
| `Themes/Theme.xaml` | Every brush, style, font in the app |
| `Resources/` | `GameRules.json` (embedded rules DB) and `Languages/*.csv` |

## Rules that bite
- **Every user-visible text must be in `Resources/Languages/template.csv`.** After changing any `Text=`,
  `Content=`, `ToolTip=`, `Tag=`, `Header=`, `Title=`, `Label=`, `Hint=` literal, any `T("...")`/`F("...")`,
  or any dialog/toast literal, run:
  `SandstormModLauncher.exe --cli translation-template .` and rebuild. `translation-check` fails otherwise.
- **Language files are embedded by wildcard** (`Resources\Languages\*.csv`). Each starts with
  `@language` (name in that language) and `@translator`. A missing translation falls back to English.
  The four shipped translations (`de`, `fr`, `tr`, `zh`) are machine translations (Google) — quality varies;
  native speakers should improve them rather than regenerate blindly.
- **`StaticResource` everywhere**: nothing swaps theme at runtime. Only the UI scale (`LayoutTransform`) is dynamic.
- **Never rename** the resource keys `Accent`, `Text`, or the semantic brushes used by `Views/Converters.cs`.
- **Offline mode is launcher-only.** It blocks update checks, SteamCMD and mod.io calls. It does not change
  what the game does. Mods are mounted by the game's own mod.io sign-in at startup; the launcher cannot
  mount a pak for the client (`-Mods=` exists only for the dedicated server). Offline, the game may start
  vanilla; `LaunchService` now warns when expected mods were not mounted.
- **Server-only mode** (`AppSettings.ServerOnly`): no "game not found" dialog, target forced to the server,
  and the map/scenario/mutator catalog is read from the dedicated server's own paks
  (`GameInstall.ForCatalog`, used only for the catalog — never for launching).
- **`ServerInstall` is both a class and a `MainViewModel` property**; `ServerInstall.Detect(...)` resolves to
  the type (C# "Color Color" rule). Works, but do not add an instance member with the same name.
- **Writes to `Game.ini`/`Input.ini` only while the game is closed**, always after a backup. Do not break that.

## Style of the codebase
- Small, focused files; `MainViewModel` is `partial` and split per feature.
- Comments explain `why`, often with the date of an audit and what broke. Keep that voice.
- Tests are property/fuzz style (`Torture`, `RconTorture`, `UiTorture`, `ProbeTest`) and are part of the contract:
  change a behaviour, change the test that asserts the old one.
- Release: bump `<Version>`/`<FileVersion>` in the csproj, add a section to `packaging/release-notes.md`,
  push to `main` (or tag `vX.Y.Z`). Only `-p:OfficialBuild=true` builds self-update.

## Known risks / open items (audit 2026-10-25)
Fixed: infinite loop on a negative property size (`UPackage.ReadTagged`), unbounded counts in
`AssetRegistry`, passwords with spaces leaking into public reports, a crash window in `Updater` that could
leave no exe, unbounded RCON replies, unvalidated player ids in kick/ban, `Open()` starting arbitrary paths,
`netsh` argument expansion, silent settings-save failures.
Still open, by design or too large for a patch:
- **No code signature on the launcher exe.** Updates are protected by SHA-256 only; the sums file itself is
  HTTPS-trusted. A compromised repo or CA could ship code. SteamCMD *is* Authenticode-checked — mirror that
  if it ever matters.
- `report-endpoint.txt` accepts any `https://` URL from `main` and POSTs the (cleaned) report there.
  Pinning the host would tighten it.
- The test harness switches (`--probe-test`, `--selftest`, `--render`, `--cli rcon-torture`) ship in release
  builds. Same-user only, but they are attack surface.
- `Store.RenameProfile` overwrites another profile's file on a name clash and has no callers.

## Environment here
- Windows, .NET SDK 10 installed; project targets `net48`.
- GitHub CLI `gh` authenticated as `goranbalsic` (repo admin). Added to user PATH.
- OpenCode plugin `@dietrichgebert/ponytail` installed globally (`~/.config/opencode/opencode.json`),
  skills: `ponytail`, `ponytail-review`, `ponytail-audit`, `ponytail-debt`, `ponytail-gain`, `ponytail-help`.
  Keep the "smallest complete change" discipline; never cut validation, security or error handling.
- **OpenUI cannot be used here**: `thesysdev/openui` is a React/Vue/Svelte/Angular generative-UI framework.
  A WPF app can only borrow its visual language (spacing, soft radii, neutral palette), not its runtime.
