using System.ComponentModel;

namespace Ranks.Importer.Config;

/// <summary>Everything the importer needs, in as few knobs as the job allows.</summary>
/// <remarks>
/// There are no credentials here — <see cref="DatabaseOptions"/> names connections that
/// SwiftlyS2's own database.jsonc already defines, and left empty it asks Ranks what it uses.
/// </remarks>
public sealed class ImporterConfig
{
    [Description("Nothing is written until this is true. Leave it false, start the server, read the report, then set it.")]
    public bool Confirm { get; set; } = false;

    [Description("Allow importing when Ranks already has players. Off by default so an import cannot overwrite a live ladder by accident.")]
    public bool Overwrite { get; set; } = false;

    [Description("Which database.jsonc connections to read from and write to.")]
    public DatabaseOptions Database { get; set; } = new();

    [Description("Where the old data lives.")]
    public SourceOptions Source { get; set; } = new();
}

/// <remarks>
/// Names of entries in SwiftlyS2's database.jsonc, not connection strings. The two may name the
/// same entry. Both are expected to be the same database engine.
/// </remarks>
public sealed class DatabaseOptions
{
    [Description("database.jsonc connection holding the OLD plugin's tables, e.g. \"ranks_old\". Empty means the same one Ranks uses.")]
    public string Source { get; set; } = string.Empty;

    [Description("database.jsonc connection Ranks itself writes to, e.g. \"ranks_new\". Empty means whatever Ranks reports, which is almost always what you want.")]
    public string Destination { get; set; } = string.Empty;
}

public sealed class SourceOptions
{
    [Description("Which plugin to import from: \"auto\", \"levelranks\" (Pisex's LVL Ranks, K4-LevelRanks-SwiftlyS2 or the K4-LevelRanks-Upkeep fork), \"k4system\" or \"zenith\". Auto picks whichever is present.")]
    public string Kind { get; set; } = "auto";

    [Description("Table prefix, if the old install used one. Example: \"awp_\" for awp_lvl_base or awp_k4ranks.")]
    public string Prefix { get; set; } = string.Empty;

    [Description("Table suffix, if it used one instead. Example: \"_awp\" for lvl_base_awp.")]
    public string Suffix { get; set; } = string.Empty;

    [Description("K4-LevelRanks only. The player table; change only if the old install renamed it.")]
    public string Players { get; set; } = "lvl_base";

    [Description("K4-LevelRanks only. Supplies the chat-message toggle.")]
    public string Settings { get; set; } = "lvl_base_settings";

    [Description("K4-LevelRanks only. Per-weapon statistics, imported when Ranks_WeaponStats is installed.")]
    public string Weapons { get; set; } = "lvl_base_weapons";

    [Description("K4-LevelRanks only. Per-hitbox statistics, imported when Ranks_WeaponStats is installed.")]
    public string Hits { get; set; } = "lvl_base_hits";

    // Both schemas apply affixes as Prefix + name + Suffix, so an install using either is reachable.
    public string PlayersTable => $"{Prefix}{Players}{Suffix}";
    public string SettingsTable => $"{Prefix}{Settings}{Suffix}";
    public string WeaponsTable => $"{Prefix}{Weapons}{Suffix}";
    public string HitsTable => $"{Prefix}{Hits}{Suffix}";

    /// <summary>Every composed name, for the caller to validate before any of them reaches SQL.</summary>
    public IEnumerable<string> AllTables()
    {
        yield return PlayersTable;
        yield return SettingsTable;
        yield return WeaponsTable;
        yield return HitsTable;
        yield return $"{Prefix}k4ranks{Suffix}";
        yield return $"{Prefix}k4stats{Suffix}";
        yield return $"{Prefix}zenith_player_storage{Suffix}";
        yield return $"{Prefix}zenith_weapon_stats{Suffix}";
    }
}
