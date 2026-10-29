using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ranks.Contract;
using SwiftlyS2.Shared;

namespace Ranks.Importer.Api;

/// <summary>The only file in this plugin that names a <c>Ranks.Contract</c> type.</summary>
/// <remarks>
/// This is the one mistake that takes the <b>whole server</b> down rather than just this plugin.
/// The CLR resolves every type a method <em>names</em> when it JITs that method, so on a box
/// without Ranks a field typed <see cref="IRanksApi"/> is enough to throw FileNotFoundException —
/// at the <em>caller's</em> frame, where this plugin's own try/catch cannot see it.
///
/// So every member that touches a contract type is <c>[MethodImpl(NoInlining)]</c>, and everywhere
/// else reads <see cref="IsAvailable"/>, a plain bool.
///
/// Audit rule: grep the <em>field name</em> across the whole plugin, not the type name.
/// </remarks>
internal sealed class RanksBridge
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;

    // Deliberately not IRanksApi: a field of that type would make every method of this class a
    // JIT hazard rather than only the ones marked NoInlining.
    private object? _api;

    public RanksBridge(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>Raised once core has migrated its own schema, which is when the target tables exist.</summary>
    public event Action? CoreReady;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool TryAcquire(IInterfaceManager interfaceManager)
    {
        try
        {
            // HasSharedInterface matches on the key string; TryGetSharedInterface resolves by Type.
            // The two disagreeing is the one symptom of a second Ranks.Contract.dll.
            var published = interfaceManager.HasSharedInterface(IRanksApi.InterfaceKey);

            if (!interfaceManager.TryGetSharedInterface<IRanksApi>(IRanksApi.InterfaceKey, out var api) || api is null)
            {
                if (published)
                    _logger.LogWarning(
                        "Ranks_Importer: Ranks published its API but this plugin could not resolve it. " +
                        "That means a second Ranks.Contract.dll is loaded — this plugin must not ship one.");

                _api = null;
                IsAvailable = false;

                return false;
            }

            _api = api;
            IsAvailable = true;

            Attach(api);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ranks_Importer: could not acquire the Ranks API.");
            _api = null;
            IsAvailable = false;

            return false;
        }
    }

    /// <remarks>Idempotent — the handshake re-runs on every plugin load and unload.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Attach(IRanksApi api)
    {
        if (api.IsCoreReady)
        {
            CoreReady?.Invoke();
            return;
        }

        api.CoreReady -= RaiseCoreReady;
        api.CoreReady += RaiseCoreReady;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RaiseCoreReady() => CoreReady?.Invoke();

    /// <summary>The connection Ranks itself is using, so the importer needs no credentials.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ConnectionName() => _api is IRanksApi api ? api.ConnectionName : null;

    /// <summary>A target table name with core's configured prefix and suffix already applied.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string? ResolveTable(string baseName) => _api is IRanksApi api ? api.ResolveTableName(baseName) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Unwire()
    {
        if (_api is IRanksApi api) api.CoreReady -= RaiseCoreReady;

        _api = null;
        IsAvailable = false;
    }
}
