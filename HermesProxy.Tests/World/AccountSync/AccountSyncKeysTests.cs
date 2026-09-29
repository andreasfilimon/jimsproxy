using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncKeysTests
{
    private static readonly WowGuid128 Guid = new(42, 99);

    [Theory]
    [InlineData("Elwynn", "elwynn")]
    [InlineData(" elWYNN ", "elwynn")]
    [InlineData("BARRENS", "barrens")]
    public void Build_SupportedRealm_UsesCanonicalSlug(string realm, string slug)
    {
        Assert.Equal($"v1/classic-era-42597/bindings/account/{slug}/-/2", AccountSyncKeys.Build(realm, Guid, 2));
    }

    [Fact]
    public void Build_Type3_UsesDecimalGuidParts() =>
        Assert.Equal("v1/classic-era-42597/bindings/character/barrens/42-99/3", AccountSyncKeys.Build("Barrens", Guid, 3));

    [Theory]
    [InlineData("Other", 2u)]
    [InlineData("Elwynn", 1u)]
    [InlineData("Elwynn", 4u)]
    public void Build_UnsupportedRealmOrType_ReturnsNull(string realm, uint type) =>
        Assert.Null(AccountSyncKeys.Build(realm, Guid, type));
}
