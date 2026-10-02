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
    [InlineData("Elwynn", 6u)]
    [InlineData("Elwynn", 8u)]
    [InlineData("Elwynn", 9u)]
    [InlineData("Elwynn", 10u)]
    [InlineData("Elwynn", 11u)]
    [InlineData("Elwynn", 12u)]
    public void Build_UnsupportedRealmOrType_ReturnsNull(string realm, uint type) =>
        Assert.Null(AccountSyncKeys.Build(realm, Guid, type));

    [Theory]
    [InlineData(0u, "v1/classic-era-42597/config/account/elwynn/-/0")]
    [InlineData(1u, "v1/classic-era-42597/config/character/elwynn/42-99/1")]
    [InlineData(4u, "v1/classic-era-42597/macros/account/elwynn/-/4")]
    [InlineData(5u, "v1/classic-era-42597/macros/character/elwynn/42-99/5")]
    [InlineData(7u, "v1/classic-era-42597/chat/character/elwynn/42-99/7")]
    public void Build_ReleasedFamilies_ReturnTheDescriptorKey(uint type, string expected) =>
        Assert.Equal(expected, AccountSyncKeys.Build("Elwynn", Guid, type));
}
