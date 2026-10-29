using System.Data;
using Microsoft.Extensions.Logging;
using Ranks.Importer.Config;

namespace Ranks.Importer.Import.Sources;

/// <summary>The LVL Ranks schema, shared by Pisex's original and the K4 plugins built on it.</summary>
/// <remarks>
/// Three plugins write these tables and they are not identical, so the column list is probed
/// rather than assumed — selecting a column that is absent fails the whole import:
///
/// <list type="bullet">
/// <item>Pisex has no <c>game_wins</c>/<c>game_losses</c> and no settings table; K4 added both.</item>
/// <item>Pisex's ExStats Weapons module records only <c>kills</c>; K4's table carries six counters.</item>
/// <item>The hits table is byte-identical across all three — K4 copied ExStats Hits.</item>
/// </list>
///
/// They also disagree on <c>steam</c>: Pisex and K4-LevelRanks-SwiftlyS2 keep the legacy
/// <c>STEAM_X:Y:Z</c> text, while the Upkeep fork converts it to SteamID64. <see cref="SteamIds"/>
/// takes either, so one reader serves all of them.
/// </remarks>
internal sealed class LevelRanksSource(SqlDialect sql, SourceOptions options, ILogger logger)
    : SourceBase(sql), ISource
{
    private string _name = "LVL Ranks";

    public string Name => _name;

    public bool IsPresent(IDbConnection connection) =>
        Db.TableReadable(connection, Sql.Quote(options.PlayersTable));

    public List<PlayerRow> ReadPlayers(IDbConnection connection, out int rejected)
    {
        var rows = new List<PlayerRow>();
        var bad = 0;

        // The match counters are the clearest marker of which plugin wrote this table.
        var hasMatches = HasColumn(connection, options.PlayersTable, "game_wins");
        _name = hasMatches ? "K4-LevelRanks" : "LVL Ranks (Pisex)";

        // "shoots" here, "shots" in the weapons table — the schema spells the same idea two ways.
        List<string> columns =
        [
            "steam", "name", "value", "kills", "deaths", "assists", "round_win", "round_lose",
            "headshots", "hits", "shoots", "lastconnect",
        ];

        if (hasMatches) columns.AddRange(["game_wins", "game_losses"]);

        using var command = Select(connection, options.PlayersTable, [.. columns]);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId))
            {
                bad++;
                continue;
            }

            var hits = Db.GetInt64(reader, 9);

            rows.Add(new PlayerRow(
                steamId,
                Db.GetString(reader, 1),
                Db.GetInt32(reader, 2),
                Db.GetInt32(reader, 3),
                Db.GetInt32(reader, 4),
                Db.GetInt32(reader, 5),
                Db.GetInt32(reader, 6),
                Db.GetInt32(reader, 7),
                hasMatches ? Db.GetInt32(reader, 12) : 0,
                hasMatches ? Db.GetInt32(reader, 13) : 0,
                // No LVL Ranks plugin records bomb objectives against a player.
                0,
                0,
                Db.GetInt32(reader, 8),
                Clamp(hits),
                MissesFrom(Db.GetInt64(reader, 10), hits),
                FromUnixSeconds(Db.GetInt64(reader, 11))));
        }

        rejected = bad;

        return rows;
    }

    public Dictionary<ulong, bool> ReadPreferences(IDbConnection connection)
    {
        var map = new Dictionary<ulong, bool>();

        if (!Db.TableReadable(connection, Sql.Quote(options.SettingsTable))) return map;

        try
        {
            using var command = Select(connection, options.SettingsTable, "steam", "messages");
            using var reader = command.ExecuteReader();

            while (reader.Read())
                if (SteamIds.TryParse(Db.GetString(reader, 0), out var steamId))
                    map[steamId] = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1));
        }
        catch (Exception ex)
        {
            // Preferences are not worth failing an import over; everyone falls back to the default.
            logger.LogWarning(ex, "Ranks_Importer: could not read {Table}, so chat-message preferences were left at the default.",
                options.SettingsTable);

            map.Clear();
        }

        return map;
    }

    /// <remarks>
    /// Pisex's ExStats Weapons module stores one counter per weapon and nothing else, so the five
    /// K4 adds are probed for and left at 0 when absent.
    /// </remarks>
    public List<WeaponRow> ReadWeapons(IDbConnection connection)
    {
        var rows = new List<WeaponRow>();

        if (!Db.TableReadable(connection, Sql.Quote(options.WeaponsTable))) return rows;

        var full = HasColumn(connection, options.WeaponsTable, "shots");

        List<string> columns = ["steam", "classname", "kills"];
        if (full) columns.AddRange(["deaths", "headshots", "hits", "shots", "damage"]);

        using var command = Select(connection, options.WeaponsTable, [.. columns]);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            var weapon = Db.GetString(reader, 1);
            if (string.IsNullOrWhiteSpace(weapon)) continue;

            rows.Add(full
                ? new WeaponRow(steamId, weapon, Db.GetInt64(reader, 2), Db.GetInt64(reader, 3),
                    Db.GetInt64(reader, 4), Db.GetInt64(reader, 5), Db.GetInt64(reader, 6), Db.GetInt64(reader, 7))
                : new WeaponRow(steamId, weapon, Db.GetInt64(reader, 2), 0, 0, 0, 0, 0));
        }

        return rows;
    }

    /// <remarks>
    /// A wide-to-tall pivot. Every column here is PascalCase, which is why they go through Quote().
    ///
    /// <c>DmgHealth</c> is a whole-player total, so it lands on hitgroup 0 (<c>GENERIC</c>, the
    /// engine's own "no hitbox" slot) rather than being apportioned across the eight — a split
    /// would be invented, and summing the table still yields the player's true damage.
    /// <c>DmgArmor</c> is dropped: Ranks counts health damage only.
    ///
    /// This table is identical in all three plugins — K4 adopted Pisex's ExStats Hits shape.
    /// </remarks>
    public List<HitRow> ReadHitGroups(IDbConnection connection)
    {
        var rows = new List<HitRow>();

        if (!Db.TableReadable(connection, Sql.Quote(options.HitsTable))) return rows;

        // CS2 hitgroup indices. "Neak" is the schema's own spelling of Neck.
        (string Column, short Group)[] groups =
        [
            ("Head", 1), ("Chest", 2), ("Belly", 3), ("LeftArm", 4),
            ("RightArm", 5), ("LeftLeg", 6), ("RightLeg", 7), ("Neak", 8),
        ];

        using var command = Select(connection, options.HitsTable,
            ["SteamID", "DmgHealth", .. groups.Select(g => g.Column)]);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            var damage = Db.GetInt64(reader, 1);
            if (damage > 0) rows.Add(new HitRow(steamId, 0, 0, damage));

            for (var i = 0; i < groups.Length; i++)
            {
                var hits = Db.GetInt64(reader, i + 2);
                if (hits > 0) rows.Add(new HitRow(steamId, groups[i].Group, hits, 0));
            }
        }

        return rows;
    }
}
