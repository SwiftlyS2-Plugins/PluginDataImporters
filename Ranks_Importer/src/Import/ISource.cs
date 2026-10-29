using System.Data;

namespace Ranks.Importer.Import;

internal readonly record struct PlayerRow(
    ulong SteamId, string Name, int Points, int Kills, int Deaths, int Assists,
    int RoundWins, int RoundLosses, int MatchWins, int MatchLosses,
    int BombsPlanted, int BombsDefused, int Headshots, int Hits, int Misses, DateTime LastSeen);

internal readonly record struct WeaponRow(
    ulong SteamId, string Weapon, long Kills, long Deaths, long Headshots, long Hits, long Shots, long Damage);

internal readonly record struct HitRow(ulong SteamId, short HitGroup, long Hits, long Damage);

/// <summary>One ranking plugin's database, read into the shapes Ranks stores.</summary>
/// <remarks>
/// A source only ever reads. Everything engine-specific lives in <see cref="SqlDialect"/>, and
/// everything Ranks-specific lives in <see cref="Importer"/>, so adding a fourth plugin means
/// adding one file here and nothing else.
/// </remarks>
internal interface ISource
{
    /// <summary>Shown to the operator, so it must name the plugin they recognise.</summary>
    string Name { get; }

    /// <summary>Whether this plugin's tables are present and readable on the connection.</summary>
    bool IsPresent(IDbConnection connection);

    List<PlayerRow> ReadPlayers(IDbConnection connection, out int rejected);

    /// <summary>The chat-message toggle, where the source has one.</summary>
    Dictionary<ulong, bool> ReadPreferences(IDbConnection connection);

    List<WeaponRow> ReadWeapons(IDbConnection connection);

    List<HitRow> ReadHitGroups(IDbConnection connection);
}

/// <summary>Shared helpers so a source reads as a column list and a projection.</summary>
internal abstract class SourceBase(SqlDialect sql)
{
    protected readonly SqlDialect Sql = sql;

    protected string Columns(params string[] names) => string.Join(", ", names.Select(Sql.Quote));

    protected IDbCommand Select(IDbConnection connection, string table, params string[] columns) =>
        Db.Command(connection, null, $"SELECT {Columns(columns)} FROM {Sql.Quote(table)}");

    protected bool HasColumn(IDbConnection connection, string table, string column) =>
        Db.ColumnReadable(connection, Sql.Quote(table), Sql.Quote(column));

    /// <remarks>
    /// K4 counts trigger pulls while Ranks counts misses, and one shotgun pull lands several hits,
    /// so the subtraction goes negative on real data and is clamped rather than stored. Accuracy
    /// after an import is an estimate, not the source's own figure.
    /// </remarks>
    protected static int MissesFrom(long shots, long hits) => (int)Math.Clamp(shots - hits, 0, int.MaxValue);

    protected static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    /// <remarks>A source that has never seen a player stores 0, which would land in 1970.</remarks>
    protected static DateTime FromUnixSeconds(long seconds) =>
        seconds <= 0 ? DateTime.UtcNow : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    /// <remarks>SQLite hands back the stored text rather than a DateTime.</remarks>
    protected static DateTime ReadDateTime(IDataRecord row, int i)
    {
        if (row.IsDBNull(i)) return DateTime.UtcNow;

        var value = row.GetValue(i);

        if (value is DateTime dt) return dt;

        return DateTime.TryParse(value.ToString(), out var parsed) ? parsed : DateTime.UtcNow;
    }
}
