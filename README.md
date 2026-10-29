<div align="center">
  <h1><strong>PluginDataImporters</strong></h1>
  <p>Move your players' data from the plugin you have onto the official SwiftlyS2 Core plugins.</p>
  <p>Each importer runs once, then you delete it.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/badge/build-passing-brightgreen" alt="Build Status">
  <img src="https://img.shields.io/github/downloads/shmitzas/PluginDataImporters/total" alt="Downloads">
  <img src="https://img.shields.io/github/stars/shmitzas/PluginDataImporters?style=flat&logo=github" alt="Stars">
  <img src="https://img.shields.io/github/license/shmitzas/PluginDataImporters" alt="License">
</p>

---

> [!NOTE]
> Your old tables are only read, never changed. If a result looks wrong, delete the new plugin's tables and run the importer again.

## 📦 Importers

| Importer | Imports into |
|----------|--------------|
| **[Ranks_Importer](Ranks_Importer)** | [Ranks](https://github.com/shmitzas/Ranks) |

VipCore, Admins, Shop and the rest are not covered yet.

### Ranks_Importer reads from

- **[LVL Ranks](https://github.com/Pisex/cs2-lvl_ranks)** - by [Pisex](https://github.com/Pisex) — the original, plus its [modules](https://github.com/Pisex/cs2-lvl_ranks_modules)
- **[K4-LevelRanks-SwiftlyS2](https://github.com/K4ryuu/K4-LevelRanks-SwiftlyS2)** - by [K4ryuu](https://github.com/K4ryuu) — now archived
- **[K4-LevelRanks-Upkeep](https://github.com/shmitzas/K4-LevelRanks-Upkeep)** - by [shmitzas](https://github.com/shmitzas) — the maintained fork, with minimal changes to original fork
- **[K4-System](https://github.com/karola3vax/K4-System)** - by [karola3vax](https://github.com/karola3vax) — a rewrite of K4ryuu's original
- **[K4-Zenith](https://github.com/K4ryuu/K4-Zenith)** - by [K4ryuu](https://github.com/K4ryuu) — its Ranks and Stats modules

You do not have to say which one you used. It looks in your database and tells you what it found.

## 🚀 How to use one

Every importer works the same way:

1. Install the target plugin first and make sure it works.
2. Drop the importer into `addons/swiftlys2/plugins/` and restart.
3. Read the console. Nothing is written yet — it only reports what it found.
4. Set `Confirm` to `true` in its `config.jsonc` and restart again.
5. Read the console, then delete the importer's folder.

Forgetting step 5 is harmless — an importer will not run twice.
