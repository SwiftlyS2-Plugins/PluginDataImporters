<div align="center">
  <h1><strong>Ranks_Importer</strong></h1>
  <p>Brings your players over from another ranking plugin — points, stats and per-weapon history.</p>
  <p>Run it once, then delete it.</p>
</div>

---

> [!NOTE]
> Your old tables are only ever read, never changed. If the result looks wrong, delete Ranks' tables
> and run it again.

## 📦 Works with

- **[LVL Ranks](https://github.com/Pisex/cs2-lvl_ranks)** - by [Pisex](https://github.com/Pisex) — the original, plus its [modules](https://github.com/Pisex/cs2-lvl_ranks_modules)
- **[K4-LevelRanks-SwiftlyS2](https://github.com/K4ryuu/K4-LevelRanks-SwiftlyS2)** - by [K4ryuu](https://github.com/K4ryuu) — now archived
- **[K4-LevelRanks-Upkeep](https://github.com/shmitzas/K4-LevelRanks-Upkeep)** - by [shmitzas](https://github.com/shmitzas) — the maintained fork, with minimal changes to original fork
- **[K4-System](https://github.com/karola3vax/K4-System)** - by [karola3vax](https://github.com/karola3vax) — a rewrite of K4ryuu's original
- **[K4-Zenith](https://github.com/K4ryuu/K4-Zenith)** - by [K4ryuu](https://github.com/K4ryuu) — its Ranks and Stats modules

You do not have to say which one you used. It looks in your database and tells you what it found.

The [full guide](docs/guide.md) covers the column mapping, what does not transfer, and troubleshooting.

## 🚀 How to use it

1. Install Ranks and make sure it works. There is nothing to configure here — no passwords, no
   database details.
2. Put `Ranks_Importer` in `addons/swiftlys2/plugins/` and restart.
3. Read the console. Nothing is written yet; it only reports what it found.
4. Set `Confirm` to `true` in `config.jsonc` and restart again.
5. Read the console, then delete the plugin folder.

Forgetting step 5 is harmless — it will not import twice.

## ⚙️ Settings

`addons/swiftlys2/plugins/Ranks_Importer/config.jsonc`

| Setting | Default | Description |
|---------|---------|-------------|
| `Confirm` | `false` | The only one most people touch. Nothing is written until it is `true` |
| `Overwrite` | `false` | Allow the import when Ranks already holds players |
| `Database.Source` | *(empty)* | `database.jsonc` entry holding the **old** plugin's tables, e.g. `"ranks_old"`. Empty means the same database Ranks uses |
| `Database.Destination` | *(empty)* | `database.jsonc` entry Ranks writes to, e.g. `"ranks_new"`. Empty means whatever Ranks reports — almost always right |
| `Source.Kind` | `auto` | `auto`, `levelranks`, `k4system` or `zenith`. Set it only if your server ran two of them |

Leave both empty if the old plugin's tables sit beside Ranks' own, which is the usual case. Use
the same database engine on both sides.

Old tables with a prefix or a renamed table? Those settings are in the
[full guide](docs/guide.md).

## ✅ What transfers

Points, kills, deaths, assists, headshots, round and match results, player names and last-seen
times — all exact.

Per-weapon and hit-location history comes too, as long as **Ranks_WeaponStats** is installed
*before* you set `Confirm`. Bomb plants and defuses transfer from K4-System and K4-Zenith only;
the LVL Ranks plugins never recorded them.

> [!NOTE]
> Anything the source never recorded arrives as 0 rather than being guessed at, and the log
> says so. The LVL Ranks plugins keep one damage total per player instead of one per hitbox,
> so it is filed under **Generic** — your total is right, the breakdown is only as detailed as
> the old plugin was.

Playtime, map and country statistics, missions and rewards do not transfer — Ranks has nowhere
to put them.

## 💡 The one thing that surprises people

> [!WARNING]
> **Players keep their points. Their rank name will probably change.**

Ranks works out each rank from points using *your* `ranks.jsonc`, and your old plugin used
different numbers. Same points, different name. If everyone lands too high or too low, tune the
ladder — re-running the import will not change it.

## 🛟 Troubleshooting

| Console says | What to do |
|--------------|------------|
| *"found no old ranking tables"* | Your old tables probably have a prefix, or Ranks is pointed at a different database |
| *"already holds N player(s)"* | Ranks is already in use. Set `Overwrite` to `true` to merge on purpose |
| *"already been imported"* | Empty the `ranks_import_log` table to run it again |
| *"Ranks_WeaponStats is not installed"* | Install it, empty `ranks_import_log`, restart |
| It read the wrong plugin | Your server ran two. Set `Source.Kind` |
| *"no 'K4-Zenith-Ranks.storage' column"* | That Zenith module never ran, so there are no points to import |
| Every rank changed | Expected — see above |

The import is all-or-nothing: if anything fails partway, nothing is written.

---

**[Full documentation →](https://github.com/shmitzas/Ranks/wiki/Module-Importer)**
