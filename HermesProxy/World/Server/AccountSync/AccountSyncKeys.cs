using System.Collections.Frozen;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Canonical binding document keys and the allow-listed realm mapping.</summary>
public static class AccountSyncKeys
{
    private static readonly FrozenDictionary<string, string> RealmSlugs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Elwynn"] = "elwynn",
            ["Barrens"] = "barrens",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the canonical key for a supported realm/type, or null.</summary>
    public static string? Build(string realmName, WowGuid128 guid, uint type)
    {
        if (!RealmSlugs.TryGetValue(realmName.Trim(), out string? slug))
            return null;
        return type switch
        {
            2 => $"v1/classic-era-42597/bindings/account/{slug}/-/2",
            3 => $"v1/classic-era-42597/bindings/character/{slug}/{guid.GetLowValue()}-{guid.GetHighValue()}/3",
            _ => null,
        };
    }
}
