param(
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'sml-tests'),
    [int[]]$EngineSeeds = @(1, 2, 3),
    [int]$EngineSteps = 1500,
    [int[]]$RconSeeds = @(1, 2),
    [int]$RconSteps = 150,
    [int[]]$UiSeeds = @(3, 5, 7),
    [int]$UiSteps = 800,
    [string[]]$Skip = @(),        # any of: translation, engine, rcon, ui
    [switch]$NoBuild,
    [switch]$Ci                   # GitHub Actions: failures become ::error annotations
)
# Builds the launcher and runs every test suite headless, without the game (demo mode: tests/fixture), each run on its
# own copy of the data. Prints one line per run plus the distinct FAIL lines; exit code 1 when anything failed.
# Windows only (WPF); CI runs it in .github/workflows/tests.yml.

$ErrorActionPreference = 'Stop'
# A test exe that crashes hard (a stack overflow) must not put a Windows error box on the screen: these error-mode flags
# are inherited by every program started from here, so a crash just ends that process (and fails its suite).
Add-Type -Namespace SmlTests -Name ErrorMode -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);'
[void][SmlTests.ErrorMode]::SetErrorMode(0x8003)
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture = Join-Path $repo 'tests\fixture'
$bin = Join-Path $Out 'bin'
$exe = Join-Path $bin 'SandstormModLauncher.exe'
New-Item -ItemType Directory -Force $Out | Out-Null
$failed = $false

function Fail([string]$what, [string]$text) {
    $script:failed = $true
    if ($Ci) { Write-Host "::error title=$what::$($text -replace '\r?\n', ' | ')" } else { Write-Host "FAIL $what`: $text" }
}

if (-not $NoBuild) {
    $log = dotnet build (Join-Path $repo 'SandstormModLauncher.csproj') -c Release -o $bin 2>&1 | Out-String
    $errors = $log -split "`n" | Where-Object { $_ -match ' error |warning CS' } | ForEach-Object { ($_ -replace '\[.*\.csproj\]', '').Trim() } | Select-Object -Unique
    # A failed build leaves the old exe, which ignores the test flags and opens a normal window: never run it then.
    if ($log -notmatch 'Build succeeded' -or $log -match 'Build FAILED' -or -not (Test-Path $exe)) {
        $errors | ForEach-Object { Write-Host $_ }
        Fail 'build' 'the build failed'
        exit 1
    }
    $errors | ForEach-Object { Write-Host "build: $_" }
    Write-Host "build: ok"
}
if (-not (Test-Path $exe)) { Fail 'build' "no exe at $exe"; exit 1 }

# A data folder for one run: the fixture (settings, game data cache), optionally with a pseudo language that marks every
# translated text as »text« (the UI torture switches to it and checks that every text on screen is translated).
function New-Data([string]$name, [switch]$Pseudo) {
    $d = Join-Path $Out "data-$name"
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    Copy-Item -Recurse $fixture $d
    if ($Pseudo) {
        New-Item -ItemType Directory -Force (Join-Path $d 'languages') | Out-Null
        $rows = Import-Csv (Join-Path $repo 'Resources\Languages\template.csv') -Encoding UTF8
        $lines = @('English,Translation,Where', '"@language","Pseudo","x"')
        foreach ($r in $rows) {
            $e = $r.English.Replace('"', '""')
            $lines += '"' + $e + '","»' + $e + '«",""'
        }
        [IO.File]::WriteAllLines((Join-Path $d 'languages\zz.csv'), $lines, [Text.UTF8Encoding]::new($true))
    }
    $d
}

# Starts the exe on its own data folder; waits only for the exe itself (not for programs it starts).
function Start-Run([string]$name, [string[]]$arguments, [switch]$Pseudo) {
    $d = New-Data $name -Pseudo:$Pseudo
    $file = Join-Path $Out "$name.txt"
    if (Test-Path $file) { Remove-Item $file }
    $all = @('--data', $d, '--render-from-cache', (Join-Path $d 'cache')) + ($arguments | ForEach-Object { $_.Replace('{out}', $file) })
    [pscustomobject]@{ Name = $name; File = $file; Process = (Start-Process -FilePath $exe -ArgumentList $all -PassThru -WindowStyle Hidden) }
}

$runs = @()
if ($Skip -notcontains 'translation') {
    $r = & $exe --cli translation-check $repo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $r -notmatch 'the same') { Fail 'translation' ($r.Trim() + ' (run --cli translation-template <repo>, rebuild)') } else { Write-Host "translation: ok" }
}
if ($Skip -notcontains 'engine') { foreach ($s in $EngineSeeds) { $runs += Start-Run "engine-$s" @('--cli', '--out', '{out}', 'torture', "$EngineSteps", "$s") } }
if ($Skip -notcontains 'rcon') { foreach ($s in $RconSeeds) { $runs += Start-Run "rcon-$s" @('--cli', '--out', '{out}', 'rcon-torture', "$RconSteps", "$s") } }
if ($Skip -notcontains 'ui') { $i = 0; foreach ($s in $UiSeeds) { $runs += Start-Run "ui-$s" @('--ui-torture', '{out}', "$UiSteps", "$s") -Pseudo:($i++ % 2 -eq 0) } }

foreach ($r in $runs) {
    # A hang fails its suite instead of blocking the run forever.
    if (-not $r.Process.WaitForExit(40 * 60 * 1000)) { try { $r.Process.Kill() } catch { }; Fail $r.Name 'did not finish within 40 minutes (killed)'; continue }
    $lines = if (Test-Path $r.File) { Get-Content $r.File -Encoding UTF8 } else { @() }
    $fails = $lines | Where-Object { $_ -like 'FAIL*' -or $_ -like 'PROBLEM*' } | ForEach-Object { ($_ -split '\): ', 2)[-1] } | Select-Object -Unique -First 15
    $last = $lines | Select-Object -Last 1
    if ($r.Process.ExitCode -ne 0 -or $fails -or $last -notmatch 'ALL CHECKS PASSED') {
        Fail $r.Name ("exit $($r.Process.ExitCode); " + ((@($fails) + @($last)) -join "`n"))
    }
    else { Write-Host "$($r.Name): ok  ($(($lines | Select-Object -Last 2 | Select-Object -First 1)))" }
}
if ($failed) { Write-Host "SOME TESTS FAILED (outputs in $Out)"; exit 1 }
Write-Host "ALL SUITES PASSED"
