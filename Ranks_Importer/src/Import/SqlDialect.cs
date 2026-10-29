using System.Text.RegularExpressions;

namespace Ranks.Importer.Import;

/// <summary>Quotes identifiers the way the target engine expects.</summary>
/// <remarks>
/// Not cosmetic. K4-LevelRanks creates <c>lvl_base_hits</c> with PascalCase columns
/// (<c>SteamID</c>, <c>Head</c>, <c>Neak</c>) and FluentMigrator double-quotes identifiers on
/// PostgreSQL, so they exist there case-sensitively. An unquoted reference folds to lower case and
/// the column is simply not found — on PostgreSQL only, which is why it survives a MySQL test.
/// </remarks>
internal sealed partial class SqlDialect
{
    private readonly char _open;
    private readonly char _close;

    private SqlDialect(char open, char close)
    {
        _open = open;
        _close = close;
    }

    /// <remarks>Dispatch is on the driver string, matching core's own migration runner.</remarks>
    public static SqlDialect For(string? driver) => driver?.Trim().ToLowerInvariant() switch
    {
        "mysql" or "mariadb" => new SqlDialect('`', '`'),
        _ => new SqlDialect('"', '"'),
    };

    public string Quote(string identifier) => $"{_open}{identifier}{_close}";

    /// <summary>Identifiers come from a config file, and none of them can be parameterised.</summary>
    public static bool IsSafeIdentifier(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) && SafeIdentifier().IsMatch(identifier);

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SafeIdentifier();
}
