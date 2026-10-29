using System.Data;
using Ranks.Importer.Config;

namespace Ranks.Importer.Import.Sources;

/// <summary>K4-System's own schema: <c>k4ranks</c> for points, <c>k4stats</c> for everything else.</summary>
/// <remarks>
/// Unrelated to the LVL Ranks tables despite the shared author lineage. Steam ids are SteamID64
/// stored as text, and <c>lastseen</c> is a real DATETIME rather than a unix stamp.
///
/// K4-System can also mirror into a LVL Ranks table for old web panels. That mirror is a subset
/// of what these two tables hold, so it is deliberately ignored: importing from it would silently
/// drop the bomb and hitbox figures this reader carries across.
/// </remarks>
internal sealed class K4SystemSource(SqlDialect sql, SourceOptions options) : SourceBase(sql), ISource
{
    public string Name => "K4-System";

    private string RanksTable => $"{options.Prefix}k4ranks{options.Suffix}";
    private string StatsTable => $"{options.Prefix}k4stats{options.Suffix}";

    public bool IsPresent(IDbConnection connection) => Db.TableReadable(connection, Sql.Quote(RanksTable));

    /// <remarks>
    /// Two tables, joined in memory rather than in SQL: the stats row is optional (K4-System lets
    /// an operator turn the stat module off), and a LEFT JOIN spelled for three engines buys
    /// nothing a dictionary lookup does not.
    /// </remarks>
    public List<PlayerRow> ReadPlayers(IDbConnection connection, out int rejected)
    {
        var stats = ReadStats(connection);
        var rows = new List<PlayerRow>();
        var bad = 0;

        using var command = Select(connection, RanksTable, "steam_id", "name", "points", "lastseen");
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId))
            {
                bad++;
                continue;
            }

            var s = stats.TryGetValue(steamId, out var found) ? found : default;

            rows.Add(new PlayerRow(
                steamId,
                Db.GetString(reader, 1),
                Db.GetInt32(reader, 2),
                s.Kills,
                s.Deaths,
                s.Assists,
                s.RoundWins,
                s.RoundLosses,
                s.MatchWins,
                s.MatchLosses,
                s.BombsPlanted,
                s.BombsDefused,
                s.Headshots,
                s.Hits,
                MissesFrom(s.Shots, s.Hits),
                ReadDateTime(reader, 3)));
        }

        rejected = bad;

        return rows;
    }

    /// <remarks>K4-System stores no chat-message toggle, so everyone keeps core's default.</remarks>
    public Dictionary<ulong, bool> ReadPreferences(IDbConnection connection) => [];

    /// <remarks>K4-System records no per-weapon breakdown at all.</remarks>
    public List<WeaponRow> ReadWeapons(IDbConnection connection) => [];

    /// <remarks>
    /// Already tall-ish: one column per hitbox on the stats row. Head is absent on purpose —
    /// K4-System records headshot <em>kills</em>, not head hits, and those are a different figure
    /// that already lands in the player row.
    /// </remarks>
    public List<HitRow> ReadHitGroups(IDbConnection connection)
    {
        var rows = new List<HitRow>();

        if (!Db.TableReadable(connection, Sql.Quote(StatsTable))) return rows;

        // CS2 hitgroup indices; 9 is the engine's unused slot, which K4-System also tracks.
        (string Column, short Group)[] groups =
        [
            ("chest_hits", 2), ("stomach_hits", 3), ("left_arm_hits", 4), ("right_arm_hits", 5),
            ("left_leg_hits", 6), ("right_leg_hits", 7), ("neck_hits", 8), ("unused_hits", 9),
            ("gear_hits", 10), ("special_hits", 11),
        ];

        using var command = Select(connection, StatsTable, ["steam_id", .. groups.Select(g => g.Column)]);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            for (var i = 0; i < groups.Length; i++)
            {
                var hits = Db.GetInt64(reader, i + 1);
                if (hits > 0) rows.Add(new HitRow(steamId, groups[i].Group, hits, 0));
            }
        }

        return rows;
    }

    private Dictionary<ulong, Stats> ReadStats(IDbConnection connection)
    {
        var map = new Dictionary<ulong, Stats>();

        if (!Db.TableReadable(connection, Sql.Quote(StatsTable))) return map;

        // game_win / game_lose are singular here, unlike LVL Ranks' game_wins / game_losses.
        using var command = Select(connection, StatsTable,
            "steam_id", "kills", "deaths", "assists", "headshots", "hits_given", "shoots",
            "round_win", "round_lose", "game_win", "game_lose", "bomb_planted", "bomb_defused");

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!SteamIds.TryParse(Db.GetString(reader, 0), out var steamId)) continue;

            map[steamId] = new Stats(
                Db.GetInt32(reader, 1),
                Db.GetInt32(reader, 2),
                Db.GetInt32(reader, 3),
                Db.GetInt32(reader, 4),
                // hits_given is damage dealt; hits_taken is the mirror and is not imported.
                Db.GetInt32(reader, 5),
                Db.GetInt64(reader, 6),
                Db.GetInt32(reader, 7),
                Db.GetInt32(reader, 8),
                Db.GetInt32(reader, 9),
                Db.GetInt32(reader, 10),
                Db.GetInt32(reader, 11),
                Db.GetInt32(reader, 12));
        }

        return map;
    }

    private readonly record struct Stats(
        int Kills, int Deaths, int Assists, int Headshots, int Hits, long Shots,
        int RoundWins, int RoundLosses, int MatchWins, int MatchLosses, int BombsPlanted, int BombsDefused);
}
