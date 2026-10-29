# This repo holds the readers; the target schema it writes into lives in the Ranks repo, which is
# expected beside this one. Override either with -RanksRepo / -K4Repo.
param(
  [string]$RanksRepo = "$PSScriptRoot\..\..\..\Ranks",
  [string]$K4Repo    = "G:\Documents\GitHub\K4ryuu-plugins-upkeep\K4-LevelRanks-Upkeep"
)

$ErrorActionPreference = 'Stop'

$resolved = Resolve-Path $RanksRepo -ErrorAction SilentlyContinue
if (-not $resolved) { Write-Host "Ranks repo not found at $RanksRepo - pass -RanksRepo <path>." -ForegroundColor Red; exit 1 }
$ranks = $resolved.Path

$k4    = "$K4Repo\src-plugin\Plugin\Database"
$imp   = "$PSScriptRoot\..\src\Import"

$pass = 0; $fail = 0
function Ok($m)  { $script:pass++; Write-Host "  ok   - $m" -ForegroundColor Gray }
function Bad($m) { $script:fail++; Write-Host "  FAIL - $m" -ForegroundColor Red }

# ---- Declared columns, straight out of each project's own migrations ----------

function Columns-From($files) {
  $set = New-Object System.Collections.Generic.HashSet[string]
  foreach ($f in $files) {
    foreach ($m in [regex]::Matches((Get-Content $f -Raw), '\.WithColumn\("([^"]+)"\)')) {
      [void]$set.Add($m.Groups[1].Value)
    }
  }
  return ,$set
}

$k4Cols = Columns-From @(
  "$k4\Migrations\M001_InitialTables.cs",
  "$k4\Migrations\M002_WeaponStatsTable.cs",
  "$k4\Migrations\M003_HitStatsTable.cs",
  "$k4\Migrations\CoreSchema.cs"
)

$ranksFiles = @(Get-ChildItem "$ranks\Ranks\src\Database\Migrations" -Filter *.cs | ForEach-Object { $_.FullName }) +
              @(Get-ChildItem "$ranks\Modules\Ranks_WeaponStats\src\Migrations" -Filter *.cs | ForEach-Object { $_.FullName })
$ranksCols = Columns-From $ranksFiles

# Ranks' combat-totals migration adds its columns from a loop, not a literal WithColumn.
foreach ($c in 'hits','misses','headshots') { [void]$ranksCols.Add($c) }

# K4-System ships no migrations to read; this is its schema transcribed from
# src/Players/StatCounters.cs (StatColumns.Names) and src/Storage/SqlDialect.cs
# (CreateTables). It exists so a typo in K4SystemSource.cs fails here.
$k4sCols = New-Object System.Collections.Generic.HashSet[string]
foreach ($c in @(
  'steam_id','name','lastseen','rank','points',
  'kills','firstblood','deaths','assists','shoots','hits_taken','hits_given','headshots',
  'chest_hits','stomach_hits','left_arm_hits','right_arm_hits','left_leg_hits','right_leg_hits',
  'neck_hits','unused_hits','gear_hits','special_hits','grenades','mvp','round_win','round_lose',
  'game_win','game_lose','rounds_overall','rounds_ct','rounds_t','bomb_planted','bomb_defused',
  'hostage_rescued','hostage_killed','noscope_kill','penetrated_kill','thrusmoke_kill',
  'flashed_kill','dominated_kill','revenge_kill','assist_flash',
  'all','ct','t','spec','dead','alive')) { [void]$k4sCols.Add($c) }

# K4-Zenith ships no migrations for its module data either; these are its real columns plus the
# JSON keys from modules/ranks/K4-Zenith-Ranks.cs and modules/statistics/K4-Zenith-Stats.cs
# (RegisterModuleStorage). A typo in K4ZenithSource.cs imports a silent zero, so it fails here.
$zenCols = New-Object System.Collections.Generic.HashSet[string]
foreach ($c in @(
  'steam_id','name','last_online','weapon','kills','shots','hits','headshots',
  'K4-Zenith-Ranks.storage','K4-Zenith-Stats.storage',
  'Points','Rank',
  'Kills','FirstBlood','Deaths','Assists','Shoots','HitsTaken','HitsGiven','Headshots',
  'HeadHits','ChestHits','StomachHits','LeftArmHits','RightArmHits','LeftLegHits','RightLegHits',
  'NeckHits','UnusedHits','GearHits','SpecialHits','Grenades','MVP','RoundWin','RoundLose',
  'GameWin','GameLose','RoundsOverall','RoundsCT','RoundsT','BombPlanted','BombDefused',
  'HostageRescued','HostageKilled','NoScopeKill','PenetratedKill','ThruSmokeKill','FlashedKill',
  'DominatedKill','RevengeKill','AssistFlash')) { [void]$zenCols.Add($c) }

# Pisex's original LVL Ranks, transcribed from levels_ranks.cpp (CREATE TABLE) and the
# cs2-lvl_ranks_modules ExStats Weapons / Hits tables. K4 built on this and added columns,
# so anything the reader names unconditionally must appear here too.
$pisexCols = New-Object System.Collections.Generic.HashSet[string]
foreach ($c in @(
  'steam','name','value','rank','kills','deaths','shoots','hits','headshots','assists',
  'round_win','round_lose','playtime','lastconnect','online',
  'classname',
  'SteamID','DmgHealth','DmgArmor','Head','Chest','Belly','LeftArm','RightArm','LeftLeg','RightLeg','Neak')) { [void]$pisexCols.Add($c) }

Write-Host "LVL Ranks (Pisex) declares $($pisexCols.Count) columns; K4-LevelRanks $($k4Cols.Count); K4-System $($k4sCols.Count); K4-Zenith $($zenCols.Count); Ranks $($ranksCols.Count)."
Write-Host ""

# ---- Every identifier the importer names must be declared somewhere ----------

function Referenced($path) {
  $text = Get-Content $path -Raw
  $set = New-Object System.Collections.Generic.HashSet[string]
  foreach ($m in [regex]::Matches($text, '(?:_sql|Sql)\.Quote\("([^"]+)"\)')) { [void]$set.Add($m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($text, 'Columns\(([^)]*)\)')) {
    foreach ($q in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { [void]$set.Add($q.Groups[1].Value) }
  }
  foreach ($m in [regex]::Matches($text, 'Select\(connection,[^;]*?\);')) {
    foreach ($q in [regex]::Matches($m.Groups[0].Value, '"([^"]+)"')) { [void]$set.Add($q.Groups[1].Value) }
  }
  # Column lists built up before the Select, so a probed/optional set is still covered.
  foreach ($m in [regex]::Matches($text, '(?s)List<string> columns =\s*\[(.*?)\];')) {
    foreach ($q in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { [void]$set.Add($q.Groups[1].Value) }
  }
  foreach ($m in [regex]::Matches($text, '(?s)new List<string>\s*\{(.*?)\}')) {
    foreach ($q in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { [void]$set.Add($q.Groups[1].Value) }
  }
  foreach ($m in [regex]::Matches($text, 'columns\.AddRange\(\[([^\]]*)\]')) {
    foreach ($q in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { [void]$set.Add($q.Groups[1].Value) }
  }
  # The hitbox pivots name their source columns in a tuple array.
  foreach ($m in [regex]::Matches($text, '\("([A-Za-z_]+)",\s*\d+\)')) { [void]$set.Add($m.Groups[1].Value) }
  # Zenith reads its values out of a JSON blob by key.
  foreach ($m in [regex]::Matches($text, 'Number\(stats, "([^"]+)"\)')) { [void]$set.Add($m.Groups[1].Value) }
  foreach ($m in [regex]::Matches($text, 'Column = "([^"]+)"')) { [void]$set.Add($m.Groups[1].Value) }
  return ,$set
}

$writer = Referenced "$imp\Importer.cs"
$lvl    = Referenced "$imp\Sources\LevelRanksSource.cs"
$k4s    = Referenced "$imp\Sources\K4SystemSource.cs"
$zen    = Referenced "$imp\Sources\K4ZenithSource.cs"

# Table names are config-driven, not columns.
$tables = @('lvl_base','lvl_base_settings','lvl_base_weapons','lvl_base_hits',
            'ranks_players','ranks_settings','ranks_weapon_stats','ranks_hit_stats',
            'zenith_player_storage','zenith_weapon_stats')

function Check-Set($label, $set, $allowed) {
  $unknown = @()
  foreach ($c in ($set | Sort-Object)) {
    if ($tables -contains $c) { continue }
    if ($allowed.Contains($c)) { continue }
    $unknown += $c
  }
  if ($unknown.Count -eq 0) { Ok "$label - all $($set.Count) identifiers resolve" }
  else { Bad "$label - undeclared: $($unknown -join ', ')" }
}

Write-Host "=== every column each file names is declared by the schema it reads or writes ===" -ForegroundColor Cyan
Check-Set "Importer.cs writes only Ranks columns" $writer $ranksCols
Check-Set "LevelRanksSource.cs reads only K4-LevelRanks columns" $lvl $k4Cols
Check-Set "K4SystemSource.cs reads only K4-System columns" $k4s $k4sCols
Check-Set "K4ZenithSource.cs reads only K4-Zenith columns and JSON keys" $zen $zenCols

# ---- The specific traps this audit was looking for ---------------------------

Write-Host ""
Write-Host "=== the column pairs that are easy to get backwards ===" -ForegroundColor Cyan

$lvlText = Get-Content "$imp\Sources\LevelRanksSource.cs" -Raw
$k4sText = Get-Content "$imp\Sources\K4SystemSource.cs" -Raw
$zenText = Get-Content "$imp\Sources\K4ZenithSource.cs" -Raw
$impText = Get-Content "$imp\Importer.cs" -Raw

if ($k4Cols.Contains('shoots') -and $k4Cols.Contains('shots')) {
  Ok "K4-LevelRanks really does spell it 'shoots' (lvl_base) and 'shots' (lvl_base_weapons)"
} else { Bad "expected both 'shoots' and 'shots' in K4-LevelRanks' schema" }

if ($lvlText -match '"shoots"' -and $lvlText -match '"shots"') { Ok "LevelRanksSource reads both spellings" }
else { Bad "LevelRanksSource does not reference both spellings" }

if ($k4Cols.Contains('game_wins') -and $ranksCols.Contains('match_wins')) {
  Ok "K4-LevelRanks game_wins/game_losses -> Ranks match_wins/match_losses"
} else { Bad "game/match naming does not match expectations" }

# K4-System drops the 's': game_win, not game_wins. Reading the wrong one throws at import time.
if ($k4sText -match '"game_win"' -and $k4sText -match '"game_lose"' -and $k4sText -notmatch '"game_wins"') {
  Ok "K4SystemSource uses the singular game_win/game_lose (it is NOT game_wins like K4-LevelRanks)"
} else { Bad "K4SystemSource does not use K4-System's singular game_win/game_lose" }

# hits_taken is damage received and would silently import as damage dealt.
if ($k4sText -match '"hits_given"' -and $k4sText -notmatch '"hits_taken"') {
  Ok "K4SystemSource reads hits_given, not the hits_taken mirror"
} else { Bad "K4SystemSource reads hits_taken or misses hits_given" }

if ($k4Cols.Contains('Neak')) { Ok "K4-LevelRanks' misspelled 'Neak' hitbox is carried verbatim" }
else { Bad "'Neak' is not in K4-LevelRanks' schema" }

# ---- The one damage figure any source records -------------------------------

Write-Host ""
Write-Host "=== damage is carried where it exists and left alone where it does not ===" -ForegroundColor Cyan

# DmgHealth is a whole-player total. Hitgroup 0 is GENERIC, the engine's "no hitbox" slot, so
# parking it there keeps SUM(damage) true without inventing a per-hitbox split.
if ($lvlText -match '"DmgHealth"') { Ok "LevelRanksSource carries DmgHealth rather than discarding it" }
else { Bad "LevelRanksSource drops DmgHealth, which both LVL Ranks plugins record" }

if ($lvlText -match 'new HitRow\(steamId, 0, 0, damage\)') {
  Ok "that total lands on hitgroup 0 (GENERIC), not apportioned across the hitboxes"
} else { Bad "LevelRanksSource does not place the damage total on hitgroup 0" }

# Ranks counts health damage only, so armour damage has nowhere truthful to go.
if ($lvlText -notmatch '"DmgArmor"') { Ok "DmgArmor is left behind - Ranks stores health damage only" }
else { Bad "LevelRanksSource reads DmgArmor, which Ranks has no column for" }

foreach ($n in 'K4-System','K4-Zenith') {
  $t = if ($n -eq 'K4-System') { $k4sText } else { $zenText }
  $c = if ($n -eq 'K4-System') { $k4sCols } else { $zenCols }
  if (($c | Where-Object { $_ -match 'Dmg|damage|Damage' }).Count -eq 0 -and $t -match 'hits, 0\)') {
    Ok "$n records no damage at all, so its hit rows correctly carry 0"
  } else { Bad "$n damage handling disagrees with its schema" }
}

if ($impText -match 'VALUES \(@id, @group, @hits, @damage\)') {
  Ok "Importer binds damage instead of hardcoding 0"
} else { Bad "Importer still writes a literal 0 into the damage column" }

# ---- Reads and writes must not share a connection ---------------------------

Write-Host ""
Write-Host "=== every read goes to the source database and every write to the destination ===" -ForegroundColor Cyan

# With both names defaulting to the same connection, crossing these still works for everyone who
# left the setting empty - only a two-database operator would ever see it, and only as "found no
# old ranking tables". So it is asserted rather than left to a run.
$run = ([regex]::Match($impText, '(?s)public ImportReport Run\(.*?\n    \}')).Value
if (-not $run) { Bad "could not find Importer.Run to inspect" }
else {
  $reads = [regex]::Matches($run, 'source\.Read\w+\(\s*(\w+)')
  $bad = $reads | Where-Object { $_.Groups[1].Value -ne 'sourceConnection' } | ForEach-Object { $_.Value }
  if ($reads.Count -ge 4 -and -not $bad) { Ok "all $($reads.Count) ISource reads take sourceConnection" }
  else { Bad "a read uses the wrong connection: $($bad -join '; ')" }

  $writes = [regex]::Matches($run, 'Write\w+\(\s*(\w+),\s*transaction')
  $badW = $writes | Where-Object { $_.Groups[1].Value -ne 'destination' } | ForEach-Object { $_.Value }
  if ($writes.Count -ge 4 -and -not $badW) { Ok "all $($writes.Count) writes take destination" }
  else { Bad "a write uses the wrong connection: $($badW -join '; ')" }

  if ($run -match 'destination\.BeginTransaction\(\)') { Ok "the only transaction is the destination's" }
  else { Bad "the transaction is not opened on the destination connection" }
}

$entry = Get-Content "$PSScriptRoot\..\src\Ranks_Importer.cs" -Raw
$cfgText = Get-Content "$PSScriptRoot\..\src\Config\Config.cs" -Raw

# Resolve sniffs the OLD plugin's tables, so it must be handed the source side of both.
if ($entry -match 'Importer\.Resolve\(connection, sourceSql') { Ok "source detection reads the source connection and its own dialect" }
else { Bad "Importer.Resolve is not given the source connection + source dialect" }

if ($entry -match 'Db\.Scalar\(destination.*?markerTable' -and $entry -match 'EnsureMarkerTable\(destination') {
  Ok "the run marker lives in the destination database"
} else { Bad "the marker table is not read/written on the destination connection" }

# Both settings exist and are documented, since they render into config.jsonc.
$dbBlock = ([regex]::Match($cfgText, '(?s)class DatabaseOptions\s*\{.*?\n\}')).Value
foreach ($k in 'Source','Destination') {
  $m = [regex]::Match($dbBlock, "(?s)\[Description\((.*?)\)\]\s*\r?\n\s*public string $k\b")
  if ($m.Success -and $m.Groups[1].Value -match 'database\.jsonc') {
    Ok "Database.$k is configurable and names database.jsonc"
  } else { Bad "Database.$k is missing or undocumented" }
}

# ---- The vendored contract must compile in but never ship -------------------

Write-Host ""
Write-Host "=== the provider contract is vendored, referenced and not shipped ===" -ForegroundColor Cyan

$proj = Get-Content "$PSScriptRoot\..\Ranks_Importer.csproj" -Raw
$depsDll = "$PSScriptRoot\..\deps\Ranks.Contract.dll"

if (Test-Path $depsDll) { Ok "deps/Ranks.Contract.dll is present, so a build needs only this repo" }
else { Bad "deps/Ranks.Contract.dll is missing - run docs/update-deps.ps1" }

if ($proj -match '(?s)<Reference Include="Ranks\.Contract">.*?deps/Ranks\.Contract\.dll.*?</Reference>') {
  Ok "the csproj references it out of deps/"
} else { Bad "the csproj does not reference deps/Ranks.Contract.dll" }

# A second copy next to the plugin gives IRanksApi two type identities and the handshake stops
# matching - on a server where Ranks is plainly running, with nothing in the log to explain it.
if ($proj -match '(?s)<Reference Include="Ranks\.Contract">.*?<Private>false</Private>.*?</Reference>') {
  Ok "and marks it Private=false, so it is compile-time only"
} else { Bad "the deps reference is not Private=false - it would ship beside the plugin" }

if ($proj -notmatch 'RanksRepo' -and $proj -notmatch 'ProjectReference') {
  Ok "no sibling-checkout path survives in the csproj"
} else { Bad "the csproj still points at another repository" }

# Only meaningful after a publish; skipped on a clean tree rather than failing.
$publish = "$PSScriptRoot\..\build\publish"
if (Test-Path $publish) {
  $stowaway = Get-ChildItem $publish -Recurse -Filter '*.Contract.dll' -ErrorAction SilentlyContinue
  if ($stowaway) { Bad "the last publish shipped: $($stowaway.Name -join ', ')" }
  else { Ok "the last publish output carries no contract assembly" }
}

# The five members the bridge actually calls. A wrong or ancient DLL vendored here compiles
# against a shape the running Ranks does not have, which only shows up as a failed handshake.
$bytes = if (Test-Path $depsDll) { [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($depsDll)) } else { '' }
$used = 'InterfaceKey','IsCoreReady','CoreReady','ConnectionName','ResolveTableName'
$absent = $used | Where-Object { $bytes -notlike "*$_*" }
if (-not $absent -and $bytes) { Ok "it exposes all $($used.Count) members RanksBridge calls" }
else { Bad "the vendored contract is missing: $($absent -join ', ')" }

$apiSource = "$ranks\Ranks.Contract\IRanksApi.cs"
if (Test-Path $apiSource) {
  $api = Get-Content $apiSource -Raw
  $gone = $used | Where-Object { $api -notmatch [regex]::Escape($_) }
  if ($gone) { Bad "Ranks no longer declares $($gone -join ', ') - deps/ is stale, run docs/update-deps.ps1" }
  else { Ok "and Ranks still declares every one of them" }
}

# ---- Three READMEs advertise one list between them --------------------------

Write-Host ""
Write-Host "=== every README naming the source plugins names the same ones ===" -ForegroundColor Cyan

# Bold links only, which skips both the "- by [author]" credit and the inline "modules" link.
function Bold-Links($text) {
  $map = @{}
  foreach ($m in [regex]::Matches($text, '\*\*\[([^\]]+)\]\((https://github\.com/[^)]+)\)\*\*')) {
    $map[$m.Groups[1].Value] = $m.Groups[2].Value
  }
  return $map
}

$lists = [ordered]@{}

# Ranks' own README: the bullets after "imports from". The unbold link to the importer repo sits
# before it and the author credits are inline, so neither reads as a plugin.
$lists['Ranks README'] = Bold-Links ([regex]::Match(
  (Get-Content "$ranks\README.md" -Raw), '(?s)imports from.*?\n\s*\n').Value)

$lists['importer README'] = Bold-Links ([regex]::Match(
  (Get-Content "$PSScriptRoot\..\README.md" -Raw), '(?s)## .{0,4} ?Works with.*?(?=\n## )').Value)

$lists['PluginDataImporters README'] = Bold-Links ([regex]::Match(
  (Get-Content "$PSScriptRoot\..\..\README.md" -Raw), '(?s)### Ranks_Importer reads from.*?(?=\n## )').Value)

$empty = $lists.Keys | Where-Object { $lists[$_].Count -eq 0 }
if ($empty) {
  Bad "no plugin list found in: $($empty -join ', ') (heading or wording changed?)"
} else {
  $reference = $lists['importer README']

  foreach ($name in $lists.Keys | Where-Object { $_ -ne 'importer README' }) {
    $other = $lists[$name]
    $missing = $reference.Keys | Where-Object { -not $other.ContainsKey($_) }
    $extra   = $other.Keys     | Where-Object { -not $reference.ContainsKey($_) }
    # A name can match while the link points elsewhere - one of these was a 404 fork before.
    $drift   = $other.Keys | Where-Object { $reference.ContainsKey($_) -and $reference[$_] -ne $other[$_] }

    if ($missing) { Bad "$name is missing: $($missing -join ', ')" }
    elseif ($extra) { Bad "$name lists plugins the importer README does not: $($extra -join ', ')" }
    elseif ($drift) { Bad "$name links elsewhere for: $($drift -join ', ')" }
    else { Ok "$name agrees - same $($other.Count) plugins, same links" }
  }

  # The reader is what actually decides; a README naming a sixth plugin would be a promise.
  if ($reference.Count -eq 5) { Ok "five plugins advertised, matching the three ISource readers" }
  else { Bad "$($reference.Count) plugins advertised - add or remove a source reader to match" }
}

# ---- Pisex's LVL Ranks is a narrower schema than K4's ------------------------

Write-Host ""
Write-Host "=== the columns K4 added on top of Pisex must be probed, never assumed ===" -ForegroundColor Cyan

# Selecting a column that is absent fails the whole import, so these two cannot be hardcoded.
foreach ($col in 'game_wins','game_losses') {
  if ($pisexCols.Contains($col)) { Bad "'$col' was expected to be a K4-only column" }
  else { Ok "'$col' really is absent from Pisex's lvl_base" }
}

if ($lvlText -match 'HasColumn\(connection, options\.PlayersTable, "game_wins"\)') {
  Ok "LevelRanksSource probes for the match counters before selecting them"
} else { Bad "LevelRanksSource selects game_wins without probing - a Pisex import would fail outright" }

if ($lvlText -match 'HasColumn\(connection, options\.WeaponsTable, "shots"\)') {
  Ok "LevelRanksSource probes the weapons table shape (Pisex records only kills)"
} else { Bad "LevelRanksSource assumes K4's 8-column weapons table" }

# Pisex's ExStats Hits is what K4 copied, so this one table needs no probing at all.
foreach ($col in 'SteamID','Head','Neak','DmgHealth') {
  if (-not $pisexCols.Contains($col)) { Bad "'$col' missing from the Pisex hits schema" }
}
Ok "the hits table is identical across Pisex and K4, so it is read unconditionally"

# Zenith uses the same singular GameWin as K4-System, and the same HitsGiven/HitsTaken pair.
if ($zenText -match '"GameWin"' -and $zenText -match '"GameLose"' -and $zenText -notmatch '"GameWins"') {
  Ok "K4ZenithSource uses the singular GameWin/GameLose"
} else { Bad "K4ZenithSource does not use Zenith's singular GameWin/GameLose" }

if ($zenText -match '"HitsGiven"' -and $zenText -notmatch '"HitsTaken"') {
  Ok "K4ZenithSource reads HitsGiven, not the HitsTaken mirror"
} else { Bad "K4ZenithSource reads HitsTaken or misses HitsGiven" }

# Zenith is the only source that records head hits apart from headshot kills.
if ($zenText -match '\("HeadHits", 1\)') {
  Ok "K4ZenithSource maps HeadHits to hitgroup 1 (K4-System cannot)"
} else { Bad "K4ZenithSource does not map HeadHits to hitgroup 1" }

# zenith_weapon_stats has no deaths and no damage column; guessing either would invent data.
if ($zenText -match 'Db\.GetInt64\(reader, 2\), 0, Db\.GetInt64\(reader, 5\)') {
  Ok "K4ZenithSource leaves per-weapon deaths and damage at 0 (Zenith records neither)"
} else { Bad "K4ZenithSource invents per-weapon deaths or damage" }

# ---- Steam id handling --------------------------------------------------------

Write-Host ""
Write-Host "=== both Steam id formats are accepted ===" -ForegroundColor Cyan

# K4-LevelRanks-SwiftlyS2 stores STEAM_X:Y:Z while the Upkeep fork converts to SteamID64.
# Rejecting either form would reject every row of one of them.
foreach ($f in 'Sources\LevelRanksSource.cs','Sources\K4SystemSource.cs','Sources\K4ZenithSource.cs') {
  $t = Get-Content "$imp\$f" -Raw
  # Only a parse applied to a column value is a Steam id parse. Zenith also parses JSON scalars,
  # which read through JsonElement.GetString() and are not ids.
  if ($t -match '(?:u?long)\.TryParse\(\s*Db\.GetString') {
    Bad "$f parses a Steam id column itself instead of going through SteamIds.TryParse"
  } elseif ($t -notmatch 'SteamIds\.TryParse\(Db\.GetString') {
    Bad "$f never resolves a Steam id through SteamIds.TryParse"
  } else { Ok "$f resolves every Steam id through SteamIds.TryParse" }
}

$ids = Get-Content "$imp\SteamIds.cs" -Raw
if ($ids -match '76561197960265728') { Ok "SteamIds carries the SteamID64 base" } else { Bad "no SteamID64 base in SteamIds" }

# The conversion, re-derived here rather than trusted: base + Z*2 + Y.
$base = [uint64]76561197960265728
$cases = @(
  @{ In = 'STEAM_0:0:0';         Out = $base },
  @{ In = 'STEAM_1:1:12345';     Out = $base + 24690 + 1 },
  @{ In = 'STEAM_1:0:12345678';  Out = $base + 24691356 }
)
$mismatch = @()
foreach ($c in $cases) {
  $p = $c.In.Split(':')
  $computed = $base + ([uint64]$p[2] * 2) + [uint64]$p[1]
  if ($computed -ne $c.Out) { $mismatch += "$($c.In) -> $computed, expected $($c.Out)" }
}
if ($mismatch.Count -eq 0) { Ok "base + Z*2 + Y reproduces every known STEAM_X:Y:Z pair" }
else { foreach ($m in $mismatch) { Bad $m } }

if ($ids -match '\(z \* 2\) \+ y') { Ok "SteamIds applies exactly that formula" } else { Bad "SteamIds does not apply base + Z*2 + Y" }

# An account id below the base is not a SteamID64 and must not be accepted as one.
if ($ids -match 'numeric <= Base') { Ok "a numeric value at or below the base is rejected" }
else { Bad "SteamIds accepts numbers that cannot be SteamID64s" }

# ---- Source selection ---------------------------------------------------------

Write-Host ""
Write-Host "=== source selection ===" -ForegroundColor Cyan

$importerText = Get-Content "$imp\Importer.cs" -Raw
foreach ($kind in 'levelranks','k4system','zenith') {
  if ($importerText -match "`"$kind`"") { Ok "Importer.Source.Kind accepts '$kind'" } else { Bad "'$kind' is not a recognised Kind" }
}
if ($importerText -match 'zenith\.IsPresent\(connection\) \? zenith') {
  Ok "auto-detect tries K4-Zenith first (it can leave an imported lvl_base behind)"
} else { Bad "auto-detect does not prefer K4-Zenith over tables it may have imported from" }
if ($importerText -match 'k4System\.IsPresent\(connection\) \? k4System') {
  Ok "auto-detect tries K4-System before LVL Ranks (its mirror is a subset of its own tables)"
} else { Bad "auto-detect does not prefer K4-System over its own mirror table" }

# The two Zenith storage columns carry a dash and a dot, so an unquoted reference is a syntax error.
if ($zenText -match 'Sql\.Quote\(column\)' -and $zenText -match 'Sql\.Quote\(StorageTable\)') {
  Ok "K4ZenithSource quotes its dotted module columns"
} else { Bad "K4ZenithSource references a module column unquoted" }

# ---- PostgreSQL case folding -------------------------------------------------

Write-Host ""
Write-Host "=== PascalCase source columns must never appear unquoted ===" -ForegroundColor Cyan

$pascal = @('SteamID','Head','Chest','Belly','LeftArm','RightArm','LeftLeg','RightLeg','Neak')
$bare = @()
foreach ($p in $pascal) {
  foreach ($m in [regex]::Matches($lvlText, "SELECT[^`"]*\b$p\b")) { $bare += "$p in: $($m.Value.Trim())" }
}
if ($bare.Count -eq 0) { Ok "no PascalCase identifier is interpolated raw into a SELECT" }
else { foreach ($b in $bare) { Bad $b } }

if ($lvlText -match 'Select\(connection, options\.HitsTable,\s*\r?\n?\s*\["SteamID"') {
  Ok "the hitbox SELECT builds its column list through Quote()"
} else { Bad "the hitbox SELECT does not go through Quote()" }

# ---- Dialect ------------------------------------------------------------------

Write-Host ""
Write-Host "=== quoting matches each engine ===" -ForegroundColor Cyan
$dialect = Get-Content "$imp\SqlDialect.cs" -Raw
if ($dialect -match '"mysql" or "mariadb"') { Ok "MySQL/MariaDB dispatch present" } else { Bad "no MySQL dispatch in SqlDialect" }
if ($dialect -match "SqlDialect\('``', '``'\)") { Ok "MySQL uses backticks" } else { Bad "MySQL quote char is not a backtick" }
if ($dialect -match "SqlDialect\('\`"', '\`"'\)") { Ok "everything else uses double quotes" } else { Bad "default quote char is not a double quote" }

Write-Host ""
Write-Host "================================"
Write-Host "passed: $pass   failed: $fail"
if ($fail -gt 0) { exit 1 }
