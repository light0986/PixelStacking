# GenLang.ps1 - builds the ResourceDictionary language files from Resources\Strings.tsv.
#
#   Resources\Strings.tsv        zh <TAB> en <TAB> ja <TAB> ko   (translation table, source of truth for now)
#   Resources\Lang\Keys.tsv      key <TAB> zh   (stable names; existing keys are never changed, new rows get new keys)
#   Resources\Lang\Strings.zh.xaml / .en.xaml / .ja.xaml / .ko.xaml   (generated ResourceDictionaries)
#
# Key rule:  <Prefix>_<Slug>
#   Prefix  = where the sentence is used: Menu (MenuItem header), Hdr (other header), Btn (button), Opt (radio/check),
#             Lbl (TextBlock text), Tip (tooltip), Title (window title) for XAML;
#             for code strings the name of the file that contains the literal (Export, Inflate, Pose, Main, Help ...);
#             Common = literal found in several files, Misc = built at run time (templates, concatenation).
#   Slug    = first words of the English translation in PascalCase (at most 5 words / ~30 letters).
#   A name that is already taken by another sentence gets a number: Tip_Undo, Tip_Undo2 ...
#   These are only the FIRST names for brand-new rows. Give a row a meaningful name afterwards with Tools\RenameKeys.ps1
#   (area prefixes used so far: Status_, Msg_, Hint_, Palette_, Layers_, Rig_, Export*_, ImportImage_, Inflate_ ...).
#   Names are never reassigned by this script; GenLang also writes Data\TextKey.cs (const strings for every key).
#
# Run:  powershell -ExecutionPolicy Bypass -File Tools\GenLang.ps1
param([string]$Root = '')
$ErrorActionPreference = 'Stop'
if ($Root -eq '') { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$utf8bom = New-Object System.Text.UTF8Encoding($true)
$han = '[' + [char]0x3400 + '-' + [char]0x9FFF + [char]0xFF00 + '-' + [char]0xFFEF + ']'

function Unesc([string]$s) {
    if ($s.IndexOf('\') -lt 0) { return $s }
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $s.Length; $i++) {
        if ($s[$i] -eq '\' -and $i + 1 -lt $s.Length) {
            $d = $s[$i + 1]
            if ($d -eq 'n') { [void]$sb.Append("`n"); $i++; continue }
            if ($d -eq '\') { [void]$sb.Append('\'); $i++; continue }
        }
        [void]$sb.Append($s[$i])
    }
    return $sb.ToString()
}
function EscTsv([string]$s) { return $s.Replace('\', '\\').Replace("`r`n", "`n").Replace("`n", '\n') }
function EscCs([string]$s) { return $s.Replace('\', '\\').Replace('"', '\"').Replace("`r`n", "`n").Replace("`n", '\n') }
function EscXml([string]$s) { return $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace("`r`n", "`n").Replace("`n", '&#10;') }

# ---------- 1. translation table ----------
$rows = New-Object System.Collections.ArrayList
$byZh = @{}
$tsv = [IO.File]::ReadAllText((Join-Path $Root 'Resources\Strings.tsv'), [Text.Encoding]::UTF8)
foreach ($raw in $tsv.Split("`n")) {
    $line = $raw.TrimEnd("`r").TrimStart([char]0xFEFF)
    if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
    $c = $line.Split("`t")
    if ($c.Count -lt 2) { continue }
    $en = Unesc $c[1]
    $ja = if ($c.Count -gt 2 -and $c[2].Length -gt 0) { Unesc $c[2] } else { $en }
    $ko = if ($c.Count -gt 3 -and $c[3].Length -gt 0) { Unesc $c[3] } else { $en }
    $row = [pscustomobject]@{ ZhRaw = $c[0]; Zh = (Unesc $c[0]); En = $en; Ja = $ja; Ko = $ko }
    if ($byZh.ContainsKey($c[0])) { $rows[$rows.IndexOf($byZh[$c[0]])] = $row } else { [void]$rows.Add($row) }
    $byZh[$c[0]] = $row
}
"strings.tsv rows: $($rows.Count)"

# ---------- 2. where each sentence is used ----------
$xamlKind = @{}   # zhRaw -> kind prefix (first use wins)
function Get-Kind([string]$el, [string]$attr) {
    if ($attr -eq 'ToolTip') { return 'Tip' }
    if ($attr -eq 'Title') { return 'Title' }
    if ($attr -eq 'Header') { if ($el -eq 'MenuItem') { return 'Menu' } else { return 'Hdr' } }
    if ($attr -eq 'Content') { if ($el -eq 'Button') { return 'Btn' } else { return 'Opt' } }
    if ($attr -eq 'Text') { return 'Lbl' }
    return 'Txt'
}
$elRe = New-Object System.Text.RegularExpressions.Regex('\G<([\w:.]+)')
foreach ($rel in 'MainWindow.xaml', 'Controls\AboutDialog.xaml', 'App.xaml') {
    $p = Join-Path $Root $rel
    if (-not (Test-Path $p)) { continue }
    $noc = [regex]::Replace([IO.File]::ReadAllText($p, [Text.Encoding]::UTF8), '<!--.*?-->', '', 'Singleline')
    foreach ($m in [regex]::Matches($noc, '(?<name>[\w:.]+)="(?<val>[^"]*)"')) {
        $attr = $m.Groups['name'].Value
        if ($attr -notin 'Header', 'Content', 'Text', 'ToolTip', 'Title') { continue }
        $val = [System.Net.WebUtility]::HtmlDecode($m.Groups['val'].Value)
        if ($val -notmatch $han -or $val.StartsWith('{')) { continue }
        $lt = $noc.LastIndexOf('<', $m.Index)
        $el = $elRe.Match($noc, $lt).Groups[1].Value
        if ($el.Contains(':')) { $el = $el.Substring($el.IndexOf(':') + 1) }
        $k = EscTsv $val
        if (-not $xamlKind.ContainsKey($k)) { $xamlKind[$k] = Get-Kind $el $attr }
    }
}
"sentences used in XAML: $($xamlKind.Count)"

$cs = New-Object System.Collections.ArrayList
Get-ChildItem -Path $Root -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin|Properties)\\' } | ForEach-Object {
    [void]$cs.Add([pscustomobject]@{ Name = $_.Name; Text = [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8) })
}
function Origin-Prefix([string]$fileName) {
    $n = $fileName.Split('.')[0]
    if ($n -eq 'MainWindow') { return 'Main' }
    if ($n -eq 'HelpContent') { return 'Help' }
    if ($n.EndsWith('Dialog') -and $n.Length -gt 6) { $n = $n.Substring(0, $n.Length - 6) }
    return $n
}
function Find-Origin([string]$zh) {
    $lit = EscCs $zh
    $quoted = '"' + $lit + '"'
    $hit = @($cs | Where-Object { $_.Text.IndexOf($quoted, [StringComparison]::Ordinal) -ge 0 })
    if ($hit.Count -eq 0 -and $lit.Length -ge 6) { $hit = @($cs | Where-Object { $_.Text.IndexOf($lit, [StringComparison]::Ordinal) -ge 0 }) }
    if ($hit.Count -eq 0) { return 'Misc' }
    $names = @($hit | ForEach-Object { Origin-Prefix $_.Name } | Select-Object -Unique)
    if ($names.Count -eq 1) { return $names[0] }
    return 'Common'
}

# ---------- 3. keys ----------
$keysPath = Join-Path $Root 'Resources\Lang\Keys.tsv'
$keyOf = @{}      # zhRaw -> key
$used = @{}       # key -> zhRaw
if (Test-Path $keysPath) {
    foreach ($raw in [IO.File]::ReadAllText($keysPath, [Text.Encoding]::UTF8).Split("`n")) {
        $line = $raw.TrimEnd("`r")
        if ($line.Length -eq 0) { continue }
        $c = $line.Split("`t")
        if ($c.Count -ge 2 -and $byZh.ContainsKey($c[1])) { $keyOf[$c[1]] = $c[0]; $used[$c[0]] = $c[1] }
    }
}
function Slug([string]$en) {
    $s = [regex]::Replace($en, '\(_.\)', '').Replace('_', '')   # drop the access-key marks: "Cu_t" -> "Cut"
    $s = [regex]::Replace($s, '\{\d+\}', ' N ')
    $words = New-Object System.Collections.ArrayList
    $len = 0
    foreach ($m in [regex]::Matches($s, '[A-Za-z][A-Za-z0-9]*|[0-9]+')) {
        if ($words.Count -ge 5 -or $len -ge 30) { break }
        $w = $m.Value
        if ($w -match '^[a-z]') { $w = $w.Substring(0, 1).ToUpper() + $w.Substring(1) }
        [void]$words.Add($w); $len += $w.Length
    }
    return ($words -join '')
}
$added = 0
$index = 0
foreach ($r in $rows) {
    $index++
    if ($keyOf.ContainsKey($r.ZhRaw)) { continue }
    $prefix = if ($xamlKind.ContainsKey($r.ZhRaw)) { $xamlKind[$r.ZhRaw] } else { Find-Origin $r.Zh }
    $slug = Slug $r.En
    if ($slug.Length -eq 0) { $slug = 'S' + $index }
    $key = $prefix + '_' + $slug
    $n = 2
    while ($used.ContainsKey($key) -and $used[$key] -ne $r.ZhRaw) { $key = $prefix + '_' + $slug + $n; $n++ }
    $keyOf[$r.ZhRaw] = $key; $used[$key] = $r.ZhRaw; $added++
}
"keys: $($keyOf.Count) ($added new)"

New-Item -ItemType Directory -Force -Path (Join-Path $Root 'Resources\Lang') | Out-Null
$sb = New-Object System.Text.StringBuilder
foreach ($r in $rows) { [void]$sb.Append($keyOf[$r.ZhRaw] + "`t" + $r.ZhRaw + "`r`n") }
[IO.File]::WriteAllText($keysPath, $sb.ToString(), $utf8bom)

# ---------- 4. dictionaries ----------
$head = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"' + "`r`n" +
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"' + "`r`n" +
        '                    xmlns:sys="clr-namespace:System;assembly=mscorlib"' + "`r`n" +
        '                    xml:space="preserve">' + "`r`n"
foreach ($lang in @(@('zh', 'Zh'), @('en', 'En'), @('ja', 'Ja'), @('ko', 'Ko'))) {
    $code = $lang[0]; $prop = $lang[1]
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append($head)
    [void]$sb.Append("    <!-- generated by Tools\GenLang.ps1 from Resources\Strings.tsv - do not edit by hand ($code) -->`r`n")
    foreach ($r in $rows) {
        [void]$sb.Append('    <sys:String x:Key="' + $keyOf[$r.ZhRaw] + '">' + (EscXml $r.$prop) + '</sys:String>' + "`r`n")
    }
    [void]$sb.Append('</ResourceDictionary>' + "`r`n")
    [IO.File]::WriteAllText((Join-Path $Root "Resources\Lang\Strings.$code.xaml"), $sb.ToString(), $utf8bom)
}

# ---------- 5. TextKey constants (Data\TextKey.cs) ----------
# Code uses Loc.Get(TextKey.Menu_File) instead of the literal "Menu_File": a typo becomes a compile error,
# IntelliSense lists every key with its English and Chinese text, and "find all references" works.
$ck = New-Object System.Text.StringBuilder
[void]$ck.Append("// <auto-generated>`r`n// Generated by Tools\GenLang.ps1 from Resources\Lang\Keys.tsv + Resources\Strings.tsv - do not edit by hand.`r`n// </auto-generated>`r`n")
[void]$ck.Append("namespace PixelStacking.Data`r`n{`r`n")
[void]$ck.Append("    /// <summary>Every text key (name = value). Use these instead of string literals: Loc.Get(TextKey.Menu_File).</summary>`r`n")
[void]$ck.Append("    public static class TextKey`r`n    {`r`n")
$byKey = @{}; foreach ($r in $rows) { $byKey[$keyOf[$r.ZhRaw]] = $r }
function DocText([string]$s) { $t = $s.Replace("`r", '').Replace("`n", ' / '); if ($t.Length -gt 90) { $t = $t.Substring(0, 90) + '...' }; return $t.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') }
foreach ($k in ($byKey.Keys | Sort-Object { $_ } -CaseSensitive)) {
    $r = $byKey[$k]
    [void]$ck.Append('        /// <summary>' + (DocText $r.En) + ' | ' + (DocText $r.Zh) + '</summary>' + "`r`n")
    [void]$ck.Append('        public const string ' + $k + ' = "' + $k + '";' + "`r`n")
}
[void]$ck.Append("    }`r`n}`r`n")
[IO.File]::WriteAllText((Join-Path $Root 'Data\TextKey.cs'), $ck.ToString(), $utf8bom)
"TextKey constants: $($byKey.Count)"

# ---------- 6. report ----------
"by prefix:"
$keyOf.Values | ForEach-Object { $_.Substring(0, $_.IndexOf('_')) } | Group-Object | Sort-Object Count -Descending | ForEach-Object { '  {0,-14} {1}' -f $_.Name, $_.Count }
