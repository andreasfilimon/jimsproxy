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

    /// <summary>Returns the canonical key for a supported realm and a proxy-implemented type, or null.
    /// The family, scope and type segment come from the descriptor table, never a switch. The optional
    /// table lets tests run with a family turned on (N1.2); the default is the shipped table.</summary>
    public static string? Build(string realmName, WowGuid128 guid, uint type,
        IReadOnlyList<AccountSyncDescriptor>? descriptors = null)
    {
        if (!RealmSlugs.TryGetValue(realmName.Trim(), out string? slug))
            return null;
        var descriptor = AccountSyncDescriptors.ForType(type, descriptors);
        if (descriptor is null || !descriptor.ImplementedByProxy)
            return null;
        return descriptor.IsCharacter
            ? $"v1/{AccountSyncDescriptors.Flavor}/{descriptor.Family}/character/{slug}/{guid.GetLowValue()}-{guid.GetHighValue()}/{descriptor.TypeSegment}"
            : $"v1/{AccountSyncDescriptors.Flavor}/{descriptor.Family}/account/{slug}/-/{descriptor.TypeSegment}";
    }
}
