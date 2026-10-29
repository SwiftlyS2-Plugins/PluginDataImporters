<div align="center">
  <h1><strong>PluginDataImporters</strong></h1>
  <p>Move your players' data from the plugin you have onto the official SwiftlyS2 Core plugins.</p>
  <p>Each importer runs once, then you delete it.</p>
</div>

---

> [!NOTE]
> Your old tables are only ever read, never changed. If a result looks wrong, delete the new
> plugin's tables and run the importer again.

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

## 🛠️ Building

Importers compile against the contract of the plugin they target, and those contracts are not
published as packages. Clone the target's repository **beside this one**:

```
SwiftlyS2 Forks - Core Plugins/
├── PluginDataImporters/
└── Ranks/
```

```bash
dotnet publish Ranks_Importer/Ranks_Importer.csproj -c Release
```

If the target lives somewhere else, pass its path:

```bash
dotnet publish Ranks_Importer/Ranks_Importer.csproj -c Release -p:RanksRepo=/path/to/Ranks
```

An importer must never ship the target's `*.Contract.dll`. The reference is `Private="false"` for
that reason: a second copy gives the interface two type identities and the handshake silently stops
matching on a server where the target plugin is plainly running.
