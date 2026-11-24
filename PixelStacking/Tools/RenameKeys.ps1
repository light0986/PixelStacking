# RenameKeys.ps1 - renames text keys everywhere in one go.
#
#   powershell -ExecutionPolicy Bypass -File Tools\RenameKeys.ps1 -Map rename_map.tsv [-WhatIf]
#
# rename_map.tsv:  oldKey <TAB> newKey   (one per line; swaps and chains are fine - the rename is done in two phases)
# What it touches:
#   Resources\Lang\Keys.tsv              first column
#   *.cs  (not obj/bin)                  string literals "oldKey"        (Loc.Get / Format / SetResourceReference ...)
#   *.xaml (not Resources\Lang)          {DynamicResource oldKey}
# Afterwards run Tools\GenLang.ps1 (regenerates the dictionaries and Tools\TextKey constants).
param([Parameter(Mandatory = $true)][string]$Map, [string]$Root = '', [switch]$WhatIf)
$ErrorActionPreference = 'Stop'
if ($Root -eq '') { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$utf8bom = New-Object System.Text.UTF8Encoding($true)

$pairs = New-Object System.Collections.ArrayList
foreach ($raw in [IO.File]::ReadAllText($Map, [Text.Encoding]::UTF8).Split("`n")) {
    $line = $raw.TrimEnd("`r"); if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    $c = $line.Split("`t"); if ($c.Count -ne 2) { throw "bad line: $line" }
    [void]$pairs.Add(@{ Old = $c[0].Trim(); New = $c[1].Trim() })
}

# --- validate ---
$keysPath = Join-Path $Root 'Resources\Lang\Keys.tsv'
$keyLines = New-Object System.Collections.ArrayList
$existing = @{}
foreach ($raw in [IO.File]::ReadAllText($keysPath, [Text.Encoding]::UTF8).Split("`n")) {
    $line = $raw.TrimEnd("`r").TrimStart([char]0xFEFF); if ($line.Length -eq 0) { continue }
    [void]$keyLines.Add($line); $existing[$line.Split("`t")[0]] = $true
}
$olds = @{}; $news = @{}
foreach ($p in $pairs) {
    if (-not $existing.ContainsKey($p.Old)) { throw "unknown key: $($p.Old)" }
    if ($olds.ContainsKey($p.Old)) { throw "old key listed twice: $($p.Old)" }
    if ($news.ContainsKey($p.New)) { throw "new name used twice: $($p.New)" }
    if ($p.New -notmatch '^[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9]+$') { throw "bad new name (Prefix_Name, letters/digits only): $($p.New)" }
    $olds[$p.Old] = $true; $news[$p.New] = $true
}
foreach ($p in $pairs) { if ($existing.ContainsKey($p.New) -and -not $olds.ContainsKey($p.New)) { throw "new name already exists: $($p.New)" } }
"map ok: $($pairs.Count) renames"
if ($WhatIf) { return }

# --- two-phase replacement: old -> token -> new ---
$tokens = @{}; $i = 0
foreach ($p in $pairs) { $i++; $tokens[$p.Old] = ('@@K' + $i + '@@'); $p.Token = $tokens[$p.Old] }
function Rename-InText([string]$text, [string]$mode) {
    foreach ($p in $pairs) {
        if ($mode -eq 'cs') { $text = $text.Replace('"' + $p.Old + '"', '"' + $p.Token + '"') }
        else { $text = $text.Replace('DynamicResource ' + $p.Old + '}', 'DynamicResource ' + $p.Token + '}') }
    }
    foreach ($p in $pairs) { $text = $text.Replace($p.Token, $p.New) }
    return $text
}
$changed = 0
Get-ChildItem -Path $Root -Recurse -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin|Properties)\\' -and $_.FullName -notmatch '\\Resources\\Lang\\' } | ForEach-Object {
    $mode = if ($_.Extension -eq '.xaml') { 'xaml' } else { 'cs' }
    $enc = New-Object System.Text.UTF8Encoding($true)
    $old = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    $new = Rename-InText $old $mode
    if ($new -ne $old) { [IO.File]::WriteAllText($_.FullName, $new, $enc); $changed++ }
}
"source files changed: $changed"

# --- Keys.tsv ---
$sb = New-Object System.Text.StringBuilder
foreach ($line in $keyLines) {
    $c = $line.Split("`t")
    $k = $c[0]
    foreach ($p in $pairs) { if ($p.Old -eq $k) { $k = $p.New; break } }
    [void]$sb.Append($k + "`t" + $c[1] + "`r`n")
}
[IO.File]::WriteAllText($keysPath, $sb.ToString(), $utf8bom)
"Keys.tsv updated"
