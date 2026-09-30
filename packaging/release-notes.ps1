param([Parameter(Mandatory)][string]$Repo, [string]$FromJson, [switch]$DryRun, [string]$Show)
# The text of each release: packaging/release-notes.md with only the sections up to that version, and the release's own
# build link and SHA-256 lines (read from its current text). -FromJson reads the releases from a saved API answer
# (GET /repos/<repo>/releases) and -DryRun only prints what would change.

$template = (Get-Content (Join-Path $PSScriptRoot 'release-notes.md') -Raw) -replace "`r`n", "`n"

function Get-Version([string]$text) {
    $parts = @($text.TrimStart('v').Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 3) { $parts += 0 }
    [version]::new($parts[0], $parts[1], $parts[2])
}

function Get-Notes([string]$tag, [string]$run, [string]$sums) {
    $version = Get-Version $tag
    $keep = $true
    $lines = foreach ($line in $template.Split("`n")) {
        if ($line -match '^\*\*Download\*\*') { $keep = $true }
        elseif ($line -match '^\*\*(\d+\.\d+(?:\.\d+)?)\*\*\s*$') { $keep = (Get-Version $Matches[1]) -le $version }
        if ($keep) { $line }
    }
    ($lines -join "`n").Replace('{VERSION}', $tag).Replace('{REPO}', $Repo).Replace('{RUN_URL}', $run).Replace('{SHA256}', $sums).Trim() + "`n"
}

$releases = if ($FromJson) { Get-Content $FromJson -Raw | ConvertFrom-Json | ForEach-Object { [pscustomobject]@{ tag = $_.tag_name; body = $_.body } } }
            else { gh release list --repo $Repo --limit 500 --json tagName | ConvertFrom-Json | ForEach-Object { [pscustomobject]@{ tag = $_.tagName; body = (gh release view $_.tagName --repo $Repo --json body -q .body) -join "`n" } } }

foreach ($r in $releases) {
    $body = ($r.body ?? '') -replace "`r`n", "`n"
    $run = [regex]::Match($body, 'https://github\.com/\S+?/actions/runs/\d+').Value
    $sums = [regex]::Match($body, '(?m)^[0-9a-f]{64}  \S+(\n[0-9a-f]{64}  \S+)*').Value
    if (-not $run -or -not $sums) { Write-Host "$($r.tag): no build link or checksums in its text, left as it is"; continue }
    $notes = Get-Notes $r.tag $run $sums
    if ($notes.Trim() -eq $body.Trim()) { Write-Host "$($r.tag): up to date"; continue }
    if ($DryRun) { Write-Host "$($r.tag): $($body.Length) -> $($notes.Length) characters"; if ($r.tag -eq $Show) { Write-Host $notes }; continue }
    $file = New-TemporaryFile
    Set-Content -Path $file -Value $notes -Encoding utf8 -NoNewline
    gh release edit $r.tag --repo $Repo --notes-file $file
    if ($LASTEXITCODE -ne 0) { throw "$($r.tag): the release text was not changed" }
    Remove-Item $file
    Write-Host "$($r.tag): $($body.Length) -> $($notes.Length) characters"
}
