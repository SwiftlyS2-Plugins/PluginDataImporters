<div align="center">
  <h1><strong>PluginDataImporters</strong></h1>
  <p>Move your players' data from the plugin you have onto the official SwiftlyS2 Core plugins.</p>
  <p>Each importer runs once, then you delete it.</p>
</div>

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

## 🛠️ Building

Nothing but this repo is needed. Each importer vendors the contract of the plugin it targets under
its own `deps/`, so there is no sibling checkout to arrange:

```bash
dotnet publish Ranks_Importer/Ranks_Importer.csproj -c Release
```

The zip lands in `Ranks_Importer/build/`.

A vendored contract can go stale. When the target plugin ships a changed one, refresh it from a
local clone and rebuild:

```powershell
pwsh Ranks_Importer/docs/update-deps.ps1 -RanksRepo ../Ranks -Build
```

**An importer must never ship the contract it compiled against.** The reference is `Private=false`
for that reason: a second copy gives the interface two type identities, and the shared-interface
handshake then silently stops matching on a server where the target plugin is plainly running. The
release workflow fails if a contract assembly reaches the publish output or the zip.

## ✅ Checks

| What | How |
|------|-----|
| Release workflow logic | `docker run --rm -v "${PWD}:/src:ro" debian:bookworm-slim bash -c "apt-get update -qq && apt-get install -y -qq git python3 python3-yaml zip unzip && cp -r /src /work && cd /work && bash docs/verify-release-workflow.sh"` |
| Ranks_Importer mapping and packaging | `pwsh Ranks_Importer/docs/verify-mapping.ps1` |

## 🚢 Releasing

Importers are versioned and released independently. Bump the importer's `CHANGELOG.md` and its
`PluginMetadata.Version` to match, then either push with `[release Ranks_Importer]` in the commit
message or run the workflow by hand and pick it from the list. The tag is `Ranks_Importer-v1.0.0`.
