using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ranks.Importer.Config;

namespace Ranks.Importer.Import.Sources;

/// <summary>K4-Zenith, where each module's data is one JSON column on a shared player row.</summary>
/// <remarks>
/// Zenith keeps almost nothing in ordinary columns. Every module writes a <c>{module}.storage</c>
/// JSON blob onto <c>zenith_player_storage</c>, and that column is created by ALTER TABLE the first
/// time the module loads — so a server that never ran K4-Zenith-Stats has no such column at all.
/// Both are probed rather than assumed, and either may be absent.
///
/// The JSON is parsed here rather than with JSON_EXTRACT: a JSON column reads back as text on every
/// engine, which keeps this source free of the dialect split that function would force.
/// </remarks>
internal sealed class K4ZenithSource(SqlDialect sql, SourceOptions options, ILogger logger)
    : SourceBase(sql), ISource
{
    // Zenith names these after the module assembly, so they carry a dash and a dot and must be quoted.
    private const string RanksColumn = "K4-Zenith-Ranks.storage";
    private const string StatsColumn = "K4-Zenith-Stats.storage";

    public string Name => "K4-Zenith";

    private string StorageTable => $"{options.Prefix}zenith_player_storage{options.Suffix}";
    private string WeaponsTable => $"{options.Prefix}zenith_weapon_stats{options.Suffix}";

    public bool IsPresent(IDbConnection connection) => Db.TableReadable(connection, Sql.Quote(StorageTable));

    public List<PlayerRow> ReadPlayers(IDbConnection connection, out int rejected)
    {
        var rows = new List<PlayerRow>();
        var bad = 0;

        var hasRanks = HasColumn(connection, RanksColumn);
        var hasStats = HasColumn(connection, StatsColumn);

        if (!hasRanks)
        {
            logger.LogWarning(
                "Ranks_Importer: {Table} has no '{Column}' column, so every player will import with 0 points. " +
                "That column only exists once K4-Zenith-Ranks has run.", StorageTable, RanksColumn);
        }

        if (!hasStats)
        {
            logger.LogWarning(
                "Ranks_Importer: {Table} has no '{Column}' column, so no statistics will be imported. " +
                "That column only exists once K4-Zenith-Stats has run.", StorageTable, StatsColumn);
        }

        var columns = new List<string> { "steam_id", "name", "last_online" };
        if (hasRanks) columns.Add(RanksColumn);
        if (hasStats) columns.Add(StatsColumn);

        using var command = Select(connection, StorageTable, [.. columns]);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId))
            {
                bad++;
                continue;
            }

            var points = hasRanks ? (int)Number(Parse(Db.GetString(reader, 3)), "Points") : 0;
            var stats = hasStats ? Parse(Db.GetString(reader, hasRanks ? 4 : 3)) : null;

            var hits = (int)Number(stats, "HitsGiven");

            rows.Add(new PlayerRow(
                steamId,
                Db.GetString(reader, 1),
                points,
                (int)Number(stats, "Kills"),
                (int)Number(stats, "Deaths"),
                (int)Number(stats, "Assists"),
                (int)Number(stats, "RoundWin"),
                (int)Number(stats, "RoundLose"),
                (int)Number(stats, "GameWin"),
                (int)Number(stats, "GameLose"),
                (int)Number(stats, "BombPlanted"),
                (int)Number(stats, "BombDefused"),
                (int)Number(stats, "Headshots"),
                hits,
                MissesFrom(Number(stats, "Shoots"), hits),
                ReadDateTime(reader, 2)));
        }

        rejected = bad;

        return rows;
    }

    /// <remarks>Zenith's chat toggles live under each module's own settings blob, not a shared one.</remarks>
    public Dictionary<ulong, bool> ReadPreferences(IDbConnection connection) => [];

    /// <remarks>
    /// The only part of Zenith kept in real columns. It records no deaths and no damage per weapon,
    /// so those two land at 0 rather than being guessed from the player totals.
    /// </remarks>
    public List<WeaponRow> ReadWeapons(IDbConnection connection)
    {
        var rows = new List<WeaponRow>();

        if (!Db.TableReadable(connection, Sql.Quote(WeaponsTable))) return rows;

        using var command = Select(connection, WeaponsTable,
            "steam_id", "weapon", "kills", "shots", "hits", "headshots");

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            var weapon = Db.GetString(reader, 1);
            if (string.IsNullOrWhiteSpace(weapon)) continue;

            rows.Add(new WeaponRow(steamId, weapon,
                Db.GetInt64(reader, 2), 0, Db.GetInt64(reader, 5),
                Db.GetInt64(reader, 4), Db.GetInt64(reader, 3), 0));
        }

        return rows;
    }

    /// <remarks>
    /// Read from the stats blob rather than <c>zenith_weapon_stats</c>: that table breaks hits down
    /// per weapon, while Ranks stores one row per hitbox for the whole player.
    ///
    /// Unlike K4-System this does include hitgroup 1 — Zenith keeps <c>HeadHits</c> separately from
    /// the <c>Headshots</c> kill counter.
    /// </remarks>
    public List<HitRow> ReadHitGroups(IDbConnection connection)
    {
        var rows = new List<HitRow>();

        if (!HasColumn(connection, StatsColumn)) return rows;

        // CS2 hitgroup indices; 9 is the engine's unused slot, which Zenith also tracks.
        (string Key, short Group)[] groups =
        [
            ("HeadHits", 1), ("ChestHits", 2), ("StomachHits", 3), ("LeftArmHits", 4),
            ("RightArmHits", 5), ("LeftLegHits", 6), ("RightLegHits", 7), ("NeckHits", 8),
            ("UnusedHits", 9), ("GearHits", 10), ("SpecialHits", 11),
        ];

        using var command = Select(connection, StorageTable, "steam_id", StatsColumn);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            var stats = Parse(Db.GetString(reader, 1));
            if (stats is null) continue;

            foreach (var (key, group) in groups)
            {
                var hits = Number(stats, key);
                if (hits > 0) rows.Add(new HitRow(steamId, group, hits, 0));
            }
        }

        return rows;
    }

    private bool HasColumn(IDbConnection connection, string column) =>
        Db.ColumnReadable(connection, Sql.Quote(StorageTable), Sql.Quote(column));

    private JsonElement? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            // Clone: the element is owned by the document, which this method disposes.
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ranks_Importer: skipped an unreadable K4-Zenith storage value.");

            return null;
        }
    }

    /// <remarks>
    /// Zenith serialises a <c>Dictionary&lt;string, object?&gt;</c>, so a value that has round-tripped
    /// through its own loader can come back as a quoted string rather than a number.
    /// </remarks>
    private static long Number(JsonElement? storage, string key)
    {
        if (storage is not { ValueKind: JsonValueKind.Object } element) return 0;
        if (!element.TryGetProperty(key, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n : (long)value.GetDouble(),
            JsonValueKind.String => long.TryParse(value.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }
}
