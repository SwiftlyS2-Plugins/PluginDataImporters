using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Ranks.Importer.Api;
using Ranks.Importer.Config;
using Ranks.Importer.Import;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Plugins;

namespace Ranks.Importer;

[PluginMetadata(
    Id = "Ranks_Importer",
    Version = "1.0.0",
    Name = "Ranks Importer",
    Author = "Shmitzas",
    Description = "One-shot import of K4-LevelRanks data into Ranks.")]
public sealed class RanksImporter : BasePlugin
{
    private const string MarkerTable = "ranks_import_log";

    private ILogger _logger = null!;
    private ImporterConfig _config = null!;
    private RanksBridge _bridge = null!;

    private bool _contractLoadFailed;
    private bool _ran;

    public RanksImporter(ISwiftlyCore core) : base(core)
    {
    }

    public override void Load(bool hotReload)
    {
        _logger = Core.LoggerFactory.CreateLogger("Ranks_Importer");

        Core.Configuration
            .InitializeJsonWithModel<ImporterConfig>("config.jsonc", "Importer", true)
            .Configure(builder => builder.AddJsonFile(
                Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: false));

        _config = Core.Configuration.Manager.GetSection("Importer").Get<ImporterConfig>() ?? new ImporterConfig();

        _bridge = new RanksBridge(Core, _logger);
        _bridge.CoreReady += RunOnce;
    }

    /// <remarks>
    /// Acquired here rather than in Load: the shared interface does not exist yet in Load.
    /// </remarks>
    public override void UseSharedInterface(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    public override void OnSharedInterfaceInjected(IInterfaceManager interfaceManager) => Acquire(interfaceManager);

    /// <remarks>
    /// The catch has to be here rather than inside the bridge. A missing Ranks.Contract.dll fails
    /// when the CLR JITs TryAcquire, which surfaces in this frame before the bridge's own catch can
    /// run — so a catch inside it would never see this.
    /// </remarks>
    private void Acquire(IInterfaceManager interfaceManager)
    {
        try
        {
            _bridge.TryAcquire(interfaceManager);
        }
        catch (Exception ex)
        {
            _contractLoadFailed = true;
            _logger.LogDebug(ex, "Ranks_Importer: the Ranks contract could not be loaded.");
        }
    }

    /// <remarks>
    /// Absence is reported from here, not from Acquire: the handshake re-runs on every plugin load
    /// and unload, so warning there repeats once per rebuild.
    /// </remarks>
    public override void OnAllPluginsLoaded()
    {
        if (_bridge.IsAvailable) return;

        if (_contractLoadFailed)
        {
            _logger.LogWarning(
                "Ranks_Importer: Ranks.Contract could not be loaded, so nothing was imported. " +
                "Check that Ranks ships resources/exports/Ranks.Contract.dll and that this plugin does not.");

            return;
        }

        _logger.LogWarning("Ranks_Importer: the Ranks plugin is not present, so there is nothing to import into.");
    }

    public override void Unload()
    {
        _bridge.CoreReady -= RunOnce;

        // Same JIT hazard as Acquire: Unwire names contract types, so a missing Ranks.Contract.dll
        // throws into this frame rather than into Unwire's own body.
        try
        {
            _bridge?.Unwire();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ranks_Importer: the Ranks contract could not be unwired.");
        }
    }

    /// <remarks>
    /// The handshake re-runs on every plugin load, so core can report itself ready more than once
    /// in a single server lifetime; the flag keeps the import to one attempt per process, and the
    /// marker row keeps it to one attempt ever.
    /// </remarks>
    private void RunOnce()
    {
        if (_ran) return;
        _ran = true;

        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Ranks_Importer: the import failed and nothing was written. " +
                "The old plugin's tables were only read, so its data is untouched.");
        }
    }

    private void Execute()
    {
        if (!ResolveConnectionNames(out var sourceName, out var destinationName)) return;

        if (_bridge.ResolveTable("ranks_players") is not { } playersTable ||
            _bridge.ResolveTable("ranks_settings") is not { } settingsTable ||
            _bridge.ResolveTable(MarkerTable) is not { } markerTable)
        {
            _logger.LogWarning("Ranks_Importer: Ranks did not report its table names.");
            return;
        }

        // Table names come from a config file and cannot be parameterised, so they are checked
        // rather than escaped. The quoting below is about PostgreSQL's case folding, not safety.
        foreach (var table in _config.Source.AllTables())
        {
            if (Import.SqlDialect.IsSafeIdentifier(table)) continue;

            _logger.LogWarning(
                "Ranks_Importer: '{Table}' is not a usable table name. Importer.Source entries may " +
                "contain only letters, digits and underscores.", table);

            return;
        }

        if (!TryOpen(destinationName, out var destination, out var destinationSql)) return;

        using (destination)
        {
            // One connection when both names agree, so the common single-database case behaves
            // exactly as it did before this setting existed.
            var separate = !string.Equals(sourceName, destinationName, StringComparison.OrdinalIgnoreCase);
            IDbConnection? owned = null;

            if (separate && !TryOpen(sourceName, out owned, out _)) return;

            using (owned)
            {
                var sourceConnection = owned ?? destination;
                var sourceSql = separate
                    ? Import.SqlDialect.For(Core.Database.GetConnectionInfo(sourceName).Driver)
                    : destinationSql;

                RunImport(sourceConnection, sourceSql, sourceName,
                    destination, destinationSql,
                    playersTable, settingsTable, markerTable);
            }
        }
    }

    /// <remarks>
    /// Both default to the connection Ranks reports, which is what keeps credentials out of this
    /// plugin's config entirely. A destination that is not Ranks' own is almost always a mistake,
    /// so it is named rather than silently obeyed.
    /// </remarks>
    private bool ResolveConnectionNames(out string sourceName, out string destinationName)
    {
        sourceName = destinationName = string.Empty;

        var ranksConnection = _bridge.ConnectionName();
        var configured = _config.Database.Destination.Trim();

        if (configured.Length > 0)
        {
            destinationName = configured;

            if (ranksConnection is not null && !string.Equals(configured, ranksConnection, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning(
                    "Ranks_Importer: Ranks reads from '{Ranks}' but Importer.Database.Destination says " +
                    "'{Configured}'. Unless those name the same database, the imported players will not " +
                    "show up in game. Leave it empty to use the one Ranks reports.",
                    ranksConnection, configured);
        }
        else if (ranksConnection is not null)
        {
            destinationName = ranksConnection;
        }
        else
        {
            _logger.LogWarning(
                "Ranks_Importer: Ranks did not report a database connection. " +
                "Set Importer.Database.Destination to the database.jsonc entry Ranks uses.");

            return false;
        }

        var source = _config.Database.Source.Trim();
        sourceName = source.Length > 0 ? source : destinationName;

        return true;
    }

    private bool TryOpen(string name, out IDbConnection connection, out Import.SqlDialect sql)
    {
        connection = null!;
        sql = null!;

        try
        {
            connection = Core.Database.GetConnection(name);
            connection.Open();
            sql = Import.SqlDialect.For(Core.Database.GetConnectionInfo(name).Driver);

            return true;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            connection = null!;

            _logger.LogError(ex,
                "Ranks_Importer: could not open the database connection '{Name}'. " +
                "It must be one of the entries in SwiftlyS2's database.jsonc.", name);

            return false;
        }
    }

    private void RunImport(
        IDbConnection connection, Import.SqlDialect sourceSql, string sourceName,
        IDbConnection destination, Import.SqlDialect sql,
        string playersTable, string settingsTable, string markerTable)
    {
        EnsureMarkerTable(destination, sql, markerTable);

        if (Db.Scalar(destination, $"SELECT COUNT(*) FROM {sql.Quote(markerTable)}") > 0)
        {
            _logger.LogInformation(
                "Ranks_Importer: this database has already been imported, so nothing was done. " +
                "Delete the plugin, or clear {Table} to run it again.", markerTable);

            return;
        }

        var source = Import.Importer.Resolve(connection, sourceSql, _config.Source, _logger);

        if (source is null || !source.IsPresent(connection))
        {
            _logger.LogWarning(
                "Ranks_Importer: found no old ranking tables on connection '{Connection}'. Looked for " +
                "'{LevelRanks}' (K4-LevelRanks) and '{K4System}' (K4-System). Set Importer.Source.Prefix " +
                "or .Suffix if the old install used one, .Kind to name the plugin outright, or " +
                "Importer.Database.Source if the old data is in another database.",
                sourceName, _config.Source.PlayersTable, $"{_config.Source.Prefix}k4ranks{_config.Source.Suffix}");

            return;
        }

        var existing = Db.Scalar(destination, $"SELECT COUNT(*) FROM {sql.Quote(playersTable)}");
        if (existing > 0 && !_config.Overwrite)
        {
            _logger.LogWarning(
                "Ranks_Importer: {Table} already holds {Count} player(s), so nothing was written. " +
                "Set Importer.Overwrite to true only if you mean to merge the old data into them.",
                playersTable, existing);

            return;
        }

        // Only offered when the module's own tables exist: WeaponStats migrates them itself, and
        // creating them here would mean owning a schema this plugin does not define.
        var weapons = _bridge.ResolveTable("ranks_weapon_stats") is { } w && Db.TableReadable(destination, sql.Quote(w)) ? w : null;
        var hits = _bridge.ResolveTable("ranks_hit_stats") is { } h && Db.TableReadable(destination, sql.Quote(h)) ? h : null;

        var targets = new TargetTables(playersTable, settingsTable, weapons, hits);
        var importer = new Import.Importer(sql, _logger);

        var report = importer.Run(connection, destination, source, targets, commit: _config.Confirm);

        if (!_config.Confirm)
        {
            _logger.LogWarning(
                "Ranks_Importer: DRY RUN — nothing was written. Found {Players} player(s) in {Source}" +
                "{Rejected}{Weapons}. Set Importer.Confirm to true and restart to import them into {Target}.",
                report.PlayersRead, report.SourceName,
                report.PlayersRejected > 0
                    ? $", skipping {report.PlayersRejected} row(s) whose SteamID could not be read"
                    : string.Empty,
                report.WeaponStatsSkipped ? ", and Ranks_WeaponStats is not installed so per-weapon history will be left behind" : string.Empty,
                playersTable);

            return;
        }

        RecordRun(destination, sql, markerTable, report);

        _logger.LogInformation(
            "Ranks_Importer: imported {Written} player(s) and {Settings} setting row(s) from {Source}. " +
            "Per-weapon rows: {Weapons}. Hitbox rows: {Hits}. Skipped: {Rejected}. " +
            "The old tables were only read and are unchanged — you can safely delete this plugin now.",
            report.PlayersWritten, report.SettingsWritten, report.SourceName,
            report.WeaponRowsWritten, report.HitRowsWritten, report.PlayersRejected);

        if (report.WeaponStatsSkipped)
            _logger.LogWarning(
                "Ranks_Importer: Ranks_WeaponStats is not installed, so per-weapon and per-hitbox history was NOT imported. " +
                "Install it, clear {Table}, and run this again if you want that history.", markerTable);
    }

    /// <remarks>
    /// Plain DDL rather than a migration handed to core: the three supported engines all accept
    /// this exact statement, and a ledger entry would outlive the plugin that explains it.
    /// </remarks>
    private static void EnsureMarkerTable(System.Data.IDbConnection connection, Import.SqlDialect sql, string table)
    {
        using var command = Db.Command(connection, null,
            $"CREATE TABLE IF NOT EXISTS {sql.Quote(table)} (" +
            $"{sql.Quote("source")} VARCHAR(128) NOT NULL, {sql.Quote("imported_at")} VARCHAR(32) NOT NULL, " +
            $"{sql.Quote("players")} INTEGER NOT NULL, {sql.Quote("weapon_rows")} INTEGER NOT NULL, " +
            $"{sql.Quote("hit_rows")} INTEGER NOT NULL)");

        command.ExecuteNonQuery();
    }

    private void RecordRun(System.Data.IDbConnection connection, Import.SqlDialect sql, string table, ImportReport report)
    {
        using var command = Db.Command(connection, null,
            $"INSERT INTO {sql.Quote(table)} (" +
            $"{sql.Quote("source")}, {sql.Quote("imported_at")}, {sql.Quote("players")}, " +
            $"{sql.Quote("weapon_rows")}, {sql.Quote("hit_rows")}) " +
            "VALUES (@source, @at, @players, @weapons, @hits)");

        Db.Bind(command, "@source", report.SourceName);
        Db.Bind(command, "@at", DateTime.UtcNow.ToString("O"));
        Db.Bind(command, "@players", report.PlayersWritten);
        Db.Bind(command, "@weapons", report.WeaponRowsWritten);
        Db.Bind(command, "@hits", report.HitRowsWritten);

        command.ExecuteNonQuery();
    }
}
