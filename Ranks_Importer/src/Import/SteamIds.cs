namespace Ranks.Importer.Import;

/// <summary>Accepts either SteamID64 or the legacy <c>STEAM_X:Y:Z</c> text.</summary>
/// <remarks>
/// Both appear in the wild: K4-LevelRanks-Upkeep converts its column to SteamID64, the SwiftlyS2
/// rewrite keeps the legacy form, and K4-System stores SteamID64 as text. Guessing wrong rejects
/// every row of a database, so both are parsed rather than one assumed.
/// </remarks>
internal static class SteamIds
{
    private const ulong Base = 76561197960265728UL;

    public static bool TryParse(string? value, out ulong steamId)
    {
        steamId = 0;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();

        if (ulong.TryParse(text, out var numeric))
        {
            // A SteamID64 is always above the base; a smaller number is an account id or junk.
            if (numeric <= Base) return false;

            steamId = numeric;
            return true;
        }

        if (!text.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = text.Split(':');
        if (parts.Length != 3) return false;

        if (!uint.TryParse(parts[1], out var y) || y > 1) return false;
        if (!ulong.TryParse(parts[2], out var z)) return false;

        steamId = Base + (z * 2) + y;

        return true;
    }
}
