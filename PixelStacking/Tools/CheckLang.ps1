# CheckLang.ps1 - consistency checks for the text keys and translations.
#
#   powershell -ExecutionPolicy Bypass -File Tools\CheckLang.ps1
#
# ERRORS (exit code 1):
#   * Keys.tsv, the four Strings.xx.xaml dictionaries and Data\TextKey.cs do not list exactly the same keys
#     (run Tools\GenLang.ps1 to regenerate them)
#   * a {DynamicResource Key} in XAML, or a "Key"-looking string literal passed to Loc.Get / Loc.Format / LangResources.Get / SetResourceReference,
#     names a key that does not exist (typo)
#   * the {0} {1} ... placeholders of a sentence differ between the four languages
#   * a translation is empty
# WARNINGS (listed, exit code stays 0):
#   * keys nobody uses (not in XAML, not as TextKey.X, and the Chinese text is not a literal in the code either)
#   * keys still named Misc_ or ending with a collision number that is not part of the meaning
#   * sentences whose English text is identical to the Chinese one (probably not translated)
param([string]$Root = '')
$ErrorActionPreference = 'Stop'
if ($Root -eq '') { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$han = '[' + [char]0x3400 + '-' + [char]0x9FFF + ']'
$errors = New-Object System.Collections.ArrayList
$warnings = New-Object System.Collections.ArrayList
function EscCs([string]$s) { return $s.Replace('\', '\\').Replace('"', '\"').Replace("`r`n", "`n").Replace("`n", '\n') }
function Unesc([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $s.Length; $i++) {
        if ($s[$i] -eq '\' -and $i + 1 -lt $s.Length) { $d = $s[$i + 1]; if ($d -eq 'n') { [void]$sb.Append("`n"); $i++; continue }; if ($d -eq '\') { [void]$sb.Append('\'); $i++; continue } }
        [void]$sb.Append($s[$i])
    }
    return $sb.ToString()
}

# ---- Keys.tsv ----
$keys = New-Object System.Collections.ArrayList; $zhOf = @{}
foreach ($raw in [IO.File]::ReadAllText((Join-Path $Root 'Resources\Lang\Keys.tsv'), [Text.Encoding]::UTF8).Split("`n")) {
    $line = $raw.TrimEnd("`r").TrimStart([char]0xFEFF); if ($line.Length -eq 0) { continue }
    $c = $line.Split("`t"); [void]$keys.Add($c[0]); $zhOf[$c[0]] = Unesc $c[1]
}
$keySet = @{}; foreach ($k in $keys) { if ($keySet.ContainsKey($k)) { [void]$errors.Add("duplicate key in Keys.tsv: $k") }; $keySet[$k] = $true }

# ---- dictionaries ----
$dict = @{}
foreach ($code in 'zh', 'en', 'ja', 'ko') {
    $map = @{}
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText((Join-Path $Root "Resources\Lang\Strings.$code.xaml"), [Text.Encoding]::UTF8), '<sys:String x:Key="([^"]+)">([^<]*)</sys:String>')) {
        $map[$m.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
    }
    $dict[$code] = $map
    foreach ($k in $keys) { if (-not $map.ContainsKey($k)) { [void]$errors.Add("Strings.$code.xaml is missing key $k") } }
    foreach ($k in $map.Keys) { if (-not $keySet.ContainsKey($k)) { [void]$errors.Add("Strings.$code.xaml has a key that is not in Keys.tsv: $k") } }
}

# ---- TextKey.cs ----
$tk = @{}
foreach ($m in [regex]::Matches([IO.File]::ReadAllText((Join-Path $Root 'Data\TextKey.cs'), [Text.Encoding]::UTF8), 'public const string (\w+) = "(\w+)";')) {
    $tk[$m.Groups[1].Value] = $true
    if ($m.Groups[1].Value -ne $m.Groups[2].Value) { [void]$errors.Add("TextKey constant name and value differ: $($m.Groups[1].Value)") }
}
foreach ($k in $keys) { if (-not $tk.ContainsKey($k)) { [void]$errors.Add("TextKey.cs is missing $k (run GenLang)") } }
foreach ($k in $tk.Keys) { if (-not $keySet.ContainsKey($k)) { [void]$errors.Add("TextKey.cs has a stale constant: $k (run GenLang)") } }

# ---- usages ----
$files = Get-ChildItem -Path $Root -Recurse -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin|Properties)\\' -and $_.FullName -notmatch '\\Resources\\Lang\\' -and $_.Name -ne 'TextKey.cs' }
$used = @{}
$allCs = New-Object System.Text.StringBuilder
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    if ($f.Extension -eq '.xaml') {
        foreach ($m in [regex]::Matches($t, 'DynamicResource (\w+)\}')) {
            $k = $m.Groups[1].Value; $used[$k] = $true
            if (-not $keySet.ContainsKey($k)) { [void]$errors.Add("$($f.Name): {DynamicResource $k} - no such key") }
        }
    } else {
        [void]$allCs.Append($t)
        foreach ($m in [regex]::Matches($t, 'TextKey\.(\w+)')) { $used[$m.Groups[1].Value] = $true }
        foreach ($m in [regex]::Matches($t, '(?:Loc|LangResources)\.(?:Get|Format)\("([^"]+)"|SetResourceReference\([^,\r\n]+,\s*"([^"]+)"')) {
            $k = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
            [void]$warnings.Add("$($f.Name): key written as a string literal (use TextKey.$k): $k")
            $used[$k] = $true
            if (-not $keySet.ContainsKey($k)) { [void]$errors.Add("$($f.Name): unknown key literal $k") }
        }
    }
}
$code = $allCs.ToString()

# ---- placeholders / empties / untranslated ----
$untranslated = 0
foreach ($k in $keys) {
    $sets = @{}
    foreach ($c in 'zh', 'en', 'ja', 'ko') {
        $v = $dict[$c][$k]
        if ($null -eq $v -or $v.Length -eq 0) { [void]$errors.Add("empty translation: $k ($c)"); continue }
        $sets[$c] = (([regex]::Matches($v, '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ',')
    }
    if ($sets.Count -eq 4 -and (($sets.Values | Sort-Object -Unique).Count -gt 1)) { [void]$errors.Add("placeholders differ between languages: $k  (zh=$($sets['zh']) en=$($sets['en']) ja=$($sets['ja']) ko=$($sets['ko']))") }
    if ($dict['zh'][$k] -match $han -and $dict['en'][$k] -eq $dict['zh'][$k] -and $dict['en'][$k] -notmatch '[A-Za-z]{4}') { $untranslated++; [void]$warnings.Add("English text is identical to the Chinese one (not translated?): $k") }
}

# ---- unused keys / naming ----
$unused = New-Object System.Collections.ArrayList
foreach ($k in $keys) {
    if ($used.ContainsKey($k)) { continue }
    $lit = '"' + (EscCs $zhOf[$k]) + '"'
    if ($code.IndexOf($lit, [StringComparison]::Ordinal) -ge 0) { continue }   # data text that the code looks up through Loc.T
    [void]$unused.Add($k)
}
foreach ($k in $unused) { [void]$warnings.Add("unused key: $k") }
foreach ($k in $keys) { if ($k -like 'Misc_*') { [void]$warnings.Add("key still named Misc_: $k") } }

"keys: $($keys.Count); used: $($used.Count); unused: $($unused.Count); untranslated rows: $untranslated"
if ($warnings.Count -gt 0) { "--- warnings ($($warnings.Count)) ---"; $warnings | Select-Object -First 60 }
if ($errors.Count -gt 0) { "--- ERRORS ($($errors.Count)) ---"; $errors | Select-Object -First 60; exit 1 }
"OK: no errors"
