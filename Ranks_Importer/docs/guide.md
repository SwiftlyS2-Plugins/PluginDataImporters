# Ranks_Importer

Brings your players over from another ranking plugin — points, stats and per-weapon history — in one step.

**Kind: one-shot tool** — not a module you keep. It runs once, records that it ran, and is then safe to delete. It awards no points and adds no commands.

It only ever *reads* the old plugin's tables. Nothing you do here can damage the data you are migrating from, so a bad result costs you nothing but a retry.

---

## Supported plugins

| Plugin | Notes |
|--------|-------|
| [LVL Ranks](https://github.com/Pisex/cs2-lvl_ranks) | Pisex's original, plus its [modules](https://github.com/Pisex/cs2-lvl_ranks_modules). A narrower schema than K4's — see below |
| [K4-LevelRanks-SwiftlyS2](https://github.com/K4ryuu/K4-LevelRanks-SwiftlyS2) | K4ryuu's take on it, archived May 2026. Stores Steam ids as `STEAM_1:…` |
| [K4-LevelRanks-Upkeep](https://github.com/shmitzas/K4-LevelRanks-Upkeep) | The maintained fork. Converted its Steam ids to SteamID64 |
| [K4-System](https://github.com/karola3vax/K4-System) | A separate schema — reads `k4ranks` and `k4stats` |
| [K4-Zenith](https://github.com/K4ryuu/K4-Zenith) | Reads the **Ranks** and **Stats** modules out of `zenith_player_storage`, plus `zenith_weapon_stats` |

All three LVL Ranks plugins write `lvl_base`, so the importer cannot tell them apart by table name. It probes the columns instead, and the report names what it found. Both Steam id forms are accepted.

You do not have to say which one you used. The importer looks in the database and reports what it found.

### LVL Ranks modules

Pisex's modules each add a table named after the core one, so `lvl_base` gives `lvl_base_weapons` and `lvl_base_hits`. Two of them hold something Ranks can store:

| Module | What happens |
|--------|--------------|
| ExStats Hits | Every hitbox. Identical to K4's table — K4 adopted this shape |
| ExStats Weapons | Per-weapon **kills only**. Pisex records nothing else, so shots, hits, deaths, headshots and damage arrive at 0 |
| ExStats Maps | Skipped. Ranks keeps no per-map statistics |
| ExStats GeoIP | Skipped. Ranks stores no addresses |
| Unusual Kills | Skipped. Ranks has no equivalent — closest is [Ranks_CustomCombos](https://github.com/shmitzas/Ranks/wiki/Module-CustomCombos), which starts fresh |
| VIP Multi, GiveExp | No stored data |

Pisex also has no `game_wins` / `game_losses`, so match records start at 0; K4 added those columns.

### K4-Zenith modules

Zenith keeps each module's data as a JSON column on one shared player row, created the first time that module loads. Only two of them hold anything Ranks can store:

| Module | What happens |
|--------|--------------|
| K4-Zenith-Ranks | Points. Missing column means the module never ran, and everyone imports at 0 — logged as a warning |
| K4-Zenith-Stats | Every statistic, including per-hitbox hits and `zenith_weapon_stats` |
| K4-Zenith-TimeStats | Skipped. Ranks does not track playtime |
| K4-Zenith-Bans | Skipped. Ranks stores no punishments |
| K4-Zenith-Toplists | Nothing to import — it derives everything from the two above |

Zenith has no table prefix of its own, so leave `Source.Prefix` and `Source.Suffix` empty for it.

---

## Before you start

> [!WARNING]
> **Players keep their points. Their rank name will probably change.**

Ranks stores no rank. It derives one from points using the ladder in your own `ranks.jsonc`, and your old plugin used different thresholds. Points transfer exactly; what they are *called* is up to your ladder.

If everyone lands too high or too low, tune [Rank Ladder](https://github.com/shmitzas/Ranks/wiki/Rank-Ladder) — re-running the import will not change it.

---

## How to use it

1. Install core and confirm it works. The importer asks core which database to use, so there is nothing to configure — no credentials, no connection string.
2. Put `Ranks_Importer` in the plugins directory and restart.
3. Read the console. Nothing is written yet; it reports what it found.
4. Set `Confirm` to `true` in `config.jsonc` and restart again.
5. Read the console, then delete the plugin folder.

Skipping step 5 is harmless — it records the run in `ranks_import_log` and does nothing on later restarts.

The import is all-or-nothing. A failure partway writes nothing rather than leaving half your players imported.

---

## Configuration

`addons/swiftlys2/plugins/Ranks_Importer/config.jsonc`

```jsonc
{
  "Importer": {
    "Confirm": false,
    "Overwrite": false,
    "Database": {
      "Source": "",
      "Destination": ""
    },
    "Source": {
      "Kind": "auto",
      "Prefix": "",
      "Suffix": "",
      "Players": "lvl_base",
      "Settings": "lvl_base_settings",
      "Weapons": "lvl_base_weapons",
      "Hits": "lvl_base_hits"
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Confirm` | `false` | Nothing is written while this is false. The report comes first |
| `Overwrite` | `false` | Allow the import when core already holds players. Off so it cannot overwrite a live ladder by accident |
| `Database.Source` | `""` | `database.jsonc` entry holding the **old** plugin's tables, e.g. `ranks_old`. Empty means the same database Ranks uses |
| `Database.Destination` | `""` | `database.jsonc` entry Ranks itself writes to, e.g. `ranks_new`. Empty means whatever Ranks reports, which is almost always what you want |
| `Source.Kind` | `auto` | `auto`, `levelranks`, `k4system` or `zenith`. Set it only when a server ran two of them and auto picked the wrong one. `levelranks` covers all three LVL Ranks plugins |
| `Source.Prefix` | `""` | Prefix on the old tables — `awp_` for `awp_lvl_base` or `awp_k4ranks` |
| `Source.Suffix` | `""` | Suffix instead — `_awp` for `lvl_base_awp` |
| `Source.Players` | `lvl_base` | K4-LevelRanks only. The remaining four apply to that schema and are ignored for K4-System |
| `Source.Settings` | `lvl_base_settings` | K4-LevelRanks only |
| `Source.Weapons` | `lvl_base_weapons` | K4-LevelRanks only |
| `Source.Hits` | `lvl_base_hits` | K4-LevelRanks only |

`Prefix` and `Suffix` apply to K4-System's fixed table names too.

**The old plugin's data may live in another database** — point `Database.Source` at it. Use the
same database engine on both sides: this is a migration tool, not an engine converter, and nothing
here checks that you did. Only the destination is written, inside a transaction; the old database
is read and nothing more. If `Database.Destination` names a connection other than the one Ranks
reports, the import still runs, but the console says so — if the two are not the same database,
the players you import will not show up in game.

**Include the underscore.** K4-LevelRanks adds the separator for you — its `TablePrefix: "awp"` produces `awp_lvl_base`. The importer uses what you give it verbatim, so copy the value across as `"awp_"`. Getting this wrong reports *"found no old ranking tables"* rather than failing loudly.

---

## What transfers

| From every supported plugin | |
|---|---|
| Points, kills, deaths, assists, headshots | exact |
| Round wins and losses, match wins and losses | exact |
| Player name and last-seen time | exact |

| Plugin-specific | |
|---|---|
| Per-weapon and per-hitbox history (K4-LevelRanks) | Needs [Ranks_WeaponStats](https://github.com/shmitzas/Ranks/wiki/Module-WeaponStats) installed **before** `Confirm` is set |
| Per-hitbox history (K4-System) | Needs Ranks_WeaponStats installed |
| Per-weapon and per-hitbox history (K4-Zenith) | Needs Ranks_WeaponStats installed. Per-weapon deaths and damage arrive at 0 — Zenith records neither |
| Head hits (K4-Zenith) | Transfers. Zenith is the only source that counts them apart from headshot kills |
| Chat point-message setting (K4-LevelRanks) | exact |
| Bomb plants and defuses (K4-System, K4-Zenith) | exact |

---

## What does not transfer

| Thing | Reason |
|-------|--------|
| Rank name | Derived from points by your ladder — see above |
| Playtime | Core does not track it |
| Missions, rewards, streaks (K4-System) | Core has no equivalent |
| Bans, toplists, playtime modules (K4-Zenith) | Core has no equivalent |
| Per-weapon history (K4-System) | K4-System never recorded it |
| Chat point-message setting (K4-System, K4-Zenith) | Neither has such a setting; core's default applies |
| Bomb plants and defuses (LVL Ranks) | No LVL Ranks plugin records them; both start at 0 |
| Match wins and losses (LVL Ranks, Pisex only) | Pisex has no such columns; K4 added them |
| Per-hitbox damage (LVL Ranks) | `DmgHealth` is one whole-player total, so it is filed under hitgroup 0 (**Generic**) — `SUM(damage)` is correct, but the per-hitbox split is not recoverable |
| Armour damage (LVL Ranks) | `DmgArmor` has no column — Ranks counts health damage only |
| All damage (K4-System, K4-Zenith) | Neither plugin records damage anywhere; it starts at 0 |
| Playtime | Ranks has no playtime column |
| Per-map and GeoIP statistics | Ranks stores neither |
| Head hits (K4-System) | It counted headshot *kills*, not head hits. The kills transfer |

**Accuracy is an estimate.** Both source schemas count trigger pulls while core counts missed bullets, and one shotgun pull fires several. Kills, deaths and hit counts are exact; only the derived accuracy percentage is approximate.

**Per-hitbox damage arrives as 0.** No source stores it, so it is left empty rather than invented. Hit *counts* are exact.

---

## Column mapping

For checking a result that looks wrong. `docs/verify-mapping.ps1` validates every column named here against each plugin's real schema, so a rename fails a check instead of an import.

### K4-LevelRanks (both versions)

| Old column | Core column |
|------------|-------------|
| `lvl_base.steam` | `steam_id` — accepts `STEAM_1:0:12345` or `76561198000012345` |
| `lvl_base.name` | `name` |
| `lvl_base.value` | `points` |
| `lvl_base.kills`, `deaths`, `assists`, `headshots` | the same names |
| `lvl_base.round_win` / `round_lose` | `round_wins` / `round_losses` |
| `lvl_base.game_wins` / `game_losses` | `match_wins` / `match_losses` |
| `lvl_base.hits` | `hits` |
| `lvl_base.shoots` minus `hits` | `misses` |
| `lvl_base.lastconnect` | `first_seen` and `last_seen` |
| `lvl_base_settings.messages` | `show_point_messages` |
| `lvl_base_weapons` | `ranks_weapon_stats` |
| `lvl_base_hits` | `ranks_hit_stats`, one row per body part |

### K4-System

| Old column | Core column |
|------------|-------------|
| `k4ranks.steam_id` | `steam_id` |
| `k4ranks.name` | `name` |
| `k4ranks.points` | `points` |
| `k4ranks.lastseen` | `first_seen` and `last_seen` |
| `k4stats.kills`, `deaths`, `assists`, `headshots` | the same names |
| `k4stats.hits_given` | `hits` |
| `k4stats.shoots` minus `hits_given` | `misses` |
| `k4stats.round_win` / `round_lose` | `round_wins` / `round_losses` |
| `k4stats.game_win` / `game_lose` | `match_wins` / `match_losses` |
| `k4stats.bomb_planted` / `bomb_defused` | `bombs_planted` / `bombs_defused` |
| `k4stats.chest_hits` … `special_hits` | `ranks_hit_stats`, one row per body part |

---

## Troubleshooting

| Symptom | Cause |
|---------|-------|
| *"found no old ranking tables"* | The old tables use a prefix — set `Source.Prefix`. Or core is pointed at a different database than the old plugin used |
| *"already holds N player(s)"* | Core is already in use. Set `Overwrite` only to merge deliberately |
| *"already been imported"* | It ran before. Empty `ranks_import_log` to run it again |
| *"Ranks_WeaponStats is not installed"* | Per-weapon and hitbox history was skipped. Install it, empty `ranks_import_log`, restart |
| It read the wrong plugin | The server ran two. Set `Source.Kind` |
| Every rank changed | Expected. Tune [Rank Ladder](https://github.com/shmitzas/Ranks/wiki/Rank-Ladder), not the import |
| Anything else | Drop core's tables, restart so core rebuilds them, run again. The old data is untouched |

See also [Troubleshooting](https://github.com/shmitzas/Ranks/wiki/Troubleshooting) and [Database](https://github.com/shmitzas/Ranks/wiki/Database).
