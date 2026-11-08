using System.Data;
using Microsoft.Extensions.Logging;
using Ranks.Importer.Config;
using Ranks.Importer.Import.Sources;

namespace Ranks.Importer.Import;

internal sealed record ImportReport(
    string SourceName,
    int PlayersRead,
    int PlayersWritten,
    int PlayersRejected,
    int SettingsWritten,
    int WeaponRowsWritten,
    int HitRowsWritten,
    bool WeaponStatsSkipped,
    bool PlaytimeSkipped);

/// <param name="Playtime">Whether the players table has the <c>playtime</c> column, which Ranks before 1.3.0 lacks.</param>
internal sealed record TargetTables(string Players, string Settings, string? WeaponStats, string? HitStats, bool Playtime);

/// <param name="PlayersWithPlaytime">Source players holding any playtime — the only ones a playtime-only run touches.</param>
/// <param name="PlayersUpdated">Of those, how many Ranks already held and therefore received it.</param>
internal sealed record PlaytimeReport(string SourceName, int PlayersWithPlaytime, int PlayersUpdated, int PlayersRejected);

/// <summary>Writes whatever a <see cref="ISource"/> read into Ranks' own tables.</summary>
/// <remarks>
/// Source tables are only ever read. The old data is left exactly as it was, which is what makes
/// the whole operation reversible: an operator who dislikes the result drops Ranks' tables and
/// runs it again.
///
/// Every identifier goes through <see cref="SqlDialect.Quote"/> — one source's hitbox table is
/// PascalCase and is therefore case-sensitive on PostgreSQL.
/// </remarks>
internal sealed class Importer
{
    private readonly SqlDialect _sql;
    private readonly ILogger _logger;

    public Importer(SqlDialect sql, ILogger logger)
    {
        _sql = sql;
        _logger = logger;
    }

    /// <summary>Picks the plugin to import from, by config or by what is actually there.</summary>
    public static ISource? Resolve(IDbConnection connection, SqlDialect sql, SourceOptions options, ILogger logger)
    {
        var levelRanks = new LevelRanksSource(sql, options, logger);
        var k4System = new K4SystemSource(sql, options);
        var zenith = new K4ZenithSource(sql, options, logger);

        return options.Kind.Trim().ToLowerInvariant() switch
        {
            "levelranks" or "lvl_base" or "k4-levelranks" or "pisex" or "lvlranks" => levelRanks,
            "k4system" or "k4-system" => k4System,
            "zenith" or "k4zenith" or "k4-zenith" => zenith,
            // Order matters only when a server ran more than one. Zenith and K4-System both come
            // first because each can leave the older plugin's tables behind: Zenith imports from
            // lvl_base, and K4-System mirrors into it. Those leftovers are subsets of what the
            // newer plugin itself holds.
            _ => zenith.IsPresent(connection) ? zenith
                : k4System.IsPresent(connection) ? k4System
                : levelRanks.IsPresent(connection) ? levelRanks
                : null,
        };
    }

    /// <remarks>
    /// Two connections because the old plugin may live in another database. Reads finish before the
    /// transaction opens, so the only transaction is the destination's — nothing spans both.
    /// </remarks>
    public ImportReport Run(IDbConnection sourceConnection, IDbConnection destination, ISource source, TargetTables targets, bool commit)
    {
        var players = source.ReadPlayers(sourceConnection, out var rejected);
        var preferences = source.ReadPreferences(sourceConnection);
        var weaponStatsSkipped = targets.WeaponStats is null || targets.HitStats is null;

        var weapons = weaponStatsSkipped ? [] : source.ReadWeapons(sourceConnection);
        var hits = weaponStatsSkipped ? [] : source.ReadHitGroups(sourceConnection);

        if (!commit)
        {
            return new ImportReport(source.Name, players.Count, 0, rejected, preferences.Count,
                weapons.Count, hits.Count, weaponStatsSkipped, !targets.Playtime);
        }

        using var transaction = destination.BeginTransaction();

        var written = WritePlayers(destination, transaction, targets.Players, players, targets.Playtime);
        var settings = WriteSettings(destination, transaction, targets.Settings, players, preferences);

        var weaponRows = weaponStatsSkipped ? 0 : WriteWeapons(destination, transaction, targets.WeaponStats!, weapons);
        var hitRows = weaponStatsSkipped ? 0 : WriteHitGroups(destination, transaction, targets.HitStats!, hits);

        transaction.Commit();

        return new ImportReport(source.Name, players.Count, written, rejected, settings,
            weaponRows, hitRows, weaponStatsSkipped, !targets.Playtime);
    }

    /// <summary>Adds each source player's playtime to the row Ranks already holds for them, and writes nothing else.</summary>
    /// <remarks>
    /// Added, not replaced: Ranks has been counting since its own update, so the old plugin's total is
    /// the time before it. A player Ranks does not hold is skipped — a row holding nothing but playtime
    /// would rank someone who never played under Ranks.
    /// </remarks>
    public PlaytimeReport RunPlaytime(IDbConnection sourceConnection, IDbConnection destination, ISource source, string table, bool commit)
    {
        var players = source.ReadPlayers(sourceConnection, out var rejected).Where(p => p.Playtime > 0).ToList();

        if (!commit) return new PlaytimeReport(source.Name, players.Count, 0, rejected);

        var t = _sql.Quote(table);
        var column = _sql.Quote("playtime");
        var updated = 0;

        using var transaction = destination.BeginTransaction();

        foreach (var p in players)
        {
            using var update = Db.Command(destination, transaction,
                $"UPDATE {t} SET {column} = {column} + @playtime WHERE {_sql.Quote("steam_id")} = @id");

            Db.Bind(update, "@playtime", p.Playtime);
            Db.Bind(update, "@id", (long)p.SteamId);

            if (update.ExecuteNonQuery() > 0) updated++;
        }

        transaction.Commit();

        return new PlaytimeReport(source.Name, players.Count, updated, rejected);
    }

    private string Columns(params string[] names) => string.Join(", ", names.Select(_sql.Quote));

    /// <remarks>
    /// UPDATE-then-INSERT rather than an upsert: the three engines spell that three different ways,
    /// and a one-shot import has no throughput problem worth a dialect switch for.
    ///
    /// <c>playtime</c> is named only when the destination has it, so an older Ranks still gets
    /// everything else instead of a rolled-back import.
    /// </remarks>
    private int WritePlayers(IDbConnection connection, IDbTransaction transaction, string table, List<PlayerRow> players, bool playtime)
    {
        var t = _sql.Quote(table);
        var written = 0;

        var setPlaytime = playtime ? $", {_sql.Quote("playtime")} = @playtime" : string.Empty;
        var insertPlaytime = playtime ? $", {_sql.Quote("playtime")}" : string.Empty;
        var valuePlaytime = playtime ? ", @playtime" : string.Empty;

        foreach (var p in players)
        {
            using (var update = Db.Command(connection, transaction, $"UPDATE {t} SET " +
                $"{_sql.Quote("name")} = @name, {_sql.Quote("points")} = @points, {_sql.Quote("kills")} = @kills, " +
                $"{_sql.Quote("deaths")} = @deaths, {_sql.Quote("assists")} = @assists, " +
                $"{_sql.Quote("round_wins")} = @rw, {_sql.Quote("round_losses")} = @rl, " +
                $"{_sql.Quote("match_wins")} = @mw, {_sql.Quote("match_losses")} = @ml, " +
                $"{_sql.Quote("bombs_planted")} = @bp, {_sql.Quote("bombs_defused")} = @bd, " +
                $"{_sql.Quote("headshots")} = @hs, {_sql.Quote("hits")} = @hits, {_sql.Quote("misses")} = @misses, " +
                $"{_sql.Quote("last_seen")} = @seen{setPlaytime} WHERE {_sql.Quote("steam_id")} = @id"))
            {
                Bind(update, p, playtime);

                if (update.ExecuteNonQuery() > 0)
                {
                    written++;
                    continue;
                }
            }

            using var insert = Db.Command(connection, transaction, $"INSERT INTO {t} (" +
                Columns("steam_id", "name", "points", "kills", "deaths", "assists", "round_wins",
                    "round_losses", "match_wins", "match_losses", "bombs_planted", "bombs_defused",
                    "headshots", "hits", "misses", "first_seen", "last_seen") + insertPlaytime +
                ") VALUES (@id, @name, @points, @kills, @deaths, @assists, @rw, @rl, " +
                $"@mw, @ml, @bp, @bd, @hs, @hits, @misses, @seen, @seen{valuePlaytime})");

            Bind(insert, p, playtime);
            insert.ExecuteNonQuery();

            written++;
        }

        return written;
    }

    /// <remarks>Binds <c>@playtime</c> only when the statement names it: a parameter the SQL never mentions is not portable.</remarks>
    private static void Bind(IDbCommand command, PlayerRow p, bool playtime)
    {
        Db.Bind(command, "@id", (long)p.SteamId);
        Db.Bind(command, "@name", p.Name);
        Db.Bind(command, "@points", p.Points);
        Db.Bind(command, "@kills", p.Kills);
        Db.Bind(command, "@deaths", p.Deaths);
        Db.Bind(command, "@assists", p.Assists);
        Db.Bind(command, "@rw", p.RoundWins);
        Db.Bind(command, "@rl", p.RoundLosses);
        Db.Bind(command, "@mw", p.MatchWins);
        Db.Bind(command, "@ml", p.MatchLosses);
        Db.Bind(command, "@bp", p.BombsPlanted);
        Db.Bind(command, "@bd", p.BombsDefused);
        Db.Bind(command, "@hs", p.Headshots);
        Db.Bind(command, "@hits", p.Hits);
        Db.Bind(command, "@misses", p.Misses);
        Db.Bind(command, "@seen", p.LastSeen);

        if (playtime) Db.Bind(command, "@playtime", p.Playtime);
    }

    /// <remarks>
    /// Driven by the player list, not the source's settings table: core needs a row per player,
    /// and a player the source stored no preference for keeps core's own default.
    /// </remarks>
    private int WriteSettings(
        IDbConnection connection, IDbTransaction transaction, string table,
        List<PlayerRow> players, Dictionary<ulong, bool> preferences)
    {
        var t = _sql.Quote(table);
        var written = 0;

        foreach (var p in players)
        {
            var show = !preferences.TryGetValue(p.SteamId, out var messages) || messages;

            using (var update = Db.Command(connection, transaction,
                $"UPDATE {t} SET {_sql.Quote("show_point_messages")} = @on WHERE {_sql.Quote("steam_id")} = @id"))
            {
                Db.Bind(update, "@on", show);
                Db.Bind(update, "@id", (long)p.SteamId);

                if (update.ExecuteNonQuery() > 0)
                {
                    written++;
                    continue;
                }
            }

            using var insert = Db.Command(connection, transaction,
                $"INSERT INTO {t} ({Columns("steam_id", "show_point_messages")}) VALUES (@id, @on)");

            Db.Bind(insert, "@id", (long)p.SteamId);
            Db.Bind(insert, "@on", show);

            insert.ExecuteNonQuery();
            written++;
        }

        return written;
    }

    private int WriteWeapons(IDbConnection connection, IDbTransaction transaction, string table, List<WeaponRow> rows)
    {
        var t = _sql.Quote(table);
        var written = 0;

        foreach (var r in rows)
        {
            using (var update = Db.Command(connection, transaction, $"UPDATE {t} SET " +
                $"{_sql.Quote("shots")} = @shots, {_sql.Quote("hits")} = @hits, {_sql.Quote("damage")} = @damage, " +
                $"{_sql.Quote("kills")} = @kills, {_sql.Quote("deaths")} = @deaths, {_sql.Quote("headshots")} = @hs " +
                $"WHERE {_sql.Quote("steam_id")} = @id AND {_sql.Quote("weapon")} = @weapon"))
            {
                BindWeapon(update, r);

                if (update.ExecuteNonQuery() > 0)
                {
                    written++;
                    continue;
                }
            }

            using var insert = Db.Command(connection, transaction, $"INSERT INTO {t} (" +
                Columns("steam_id", "weapon", "shots", "hits", "damage", "kills", "deaths", "headshots") +
                ") VALUES (@id, @weapon, @shots, @hits, @damage, @kills, @deaths, @hs)");

            BindWeapon(insert, r);
            insert.ExecuteNonQuery();

            written++;
        }

        return written;
    }

    private static void BindWeapon(IDbCommand command, WeaponRow r)
    {
        Db.Bind(command, "@id", (long)r.SteamId);
        Db.Bind(command, "@weapon", r.Weapon);
        Db.Bind(command, "@shots", r.Shots);
        Db.Bind(command, "@hits", r.Hits);
        Db.Bind(command, "@damage", r.Damage);
        Db.Bind(command, "@kills", r.Kills);
        Db.Bind(command, "@deaths", r.Deaths);
        Db.Bind(command, "@hs", r.Headshots);
    }

    /// <remarks>
    /// Damage arrives on hitgroup 0 or not at all — no source records it per hitbox. The row is
    /// keyed <c>(steam_id, hitgroup)</c>, so hits and damage for one group never collide.
    /// </remarks>
    private int WriteHitGroups(IDbConnection connection, IDbTransaction transaction, string table, List<HitRow> rows)
    {
        var t = _sql.Quote(table);
        var written = 0;

        foreach (var r in rows)
        {
            using (var update = Db.Command(connection, transaction,
                $"UPDATE {t} SET {_sql.Quote("hits")} = @hits, {_sql.Quote("damage")} = @damage " +
                $"WHERE {_sql.Quote("steam_id")} = @id AND {_sql.Quote("hitgroup")} = @group"))
            {
                Db.Bind(update, "@hits", r.Hits);
                Db.Bind(update, "@damage", r.Damage);
                Db.Bind(update, "@id", (long)r.SteamId);
                Db.Bind(update, "@group", r.HitGroup);

                if (update.ExecuteNonQuery() > 0)
                {
                    written++;
                    continue;
                }
            }

            using var insert = Db.Command(connection, transaction,
                $"INSERT INTO {t} ({Columns("steam_id", "hitgroup", "hits", "damage")}) VALUES (@id, @group, @hits, @damage)");

            Db.Bind(insert, "@id", (long)r.SteamId);
            Db.Bind(insert, "@group", r.HitGroup);
            Db.Bind(insert, "@hits", r.Hits);
            Db.Bind(insert, "@damage", r.Damage);

            insert.ExecuteNonQuery();
            written++;
        }

        return written;
    }
}
