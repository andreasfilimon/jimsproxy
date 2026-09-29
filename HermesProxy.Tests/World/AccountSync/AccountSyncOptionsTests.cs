using System;
using System.Collections.Generic;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncOptionsTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["ST_SYNC_BASE"] = "https://stonetavern.app/api/launcher/sync",
        ["ST_SYNC_TOKEN"] = "secret",
        ["ST_SYNC_DEVICE_ID"] = "12345678-1234-1234-1234-123456789abc",
    };

    [Theory]
    [InlineData("ST_SYNC_BASE")]
    [InlineData("ST_SYNC_TOKEN")]
    [InlineData("ST_SYNC_DEVICE_ID")]
    public void FromEnvironment_MissingRequiredValue_Disables(string missing)
    {
        var values = new Dictionary<string, string?>(Valid) { [missing] = null };
        Assert.False(AccountSyncOptions.FromEnvironment(name => values.GetValueOrDefault(name)).Enabled);
    }

    [Theory]
    [InlineData("ftp://localhost/sync")]
    [InlineData("http://example.com/sync")]
    [InlineData("https://user:pass@example.com/sync")]
    public void FromEnvironment_InvalidBase_Disables(string value)
    {
        var values = new Dictionary<string, string?>(Valid) { ["ST_SYNC_BASE"] = value };
        Assert.False(AccountSyncOptions.FromEnvironment(name => values.GetValueOrDefault(name)).Enabled);
    }

    [Theory]
    [InlineData("12345678-1234-1234-1234-123456789ABC", null)]
    [InlineData("not-a-uuid", null)]
    [InlineData("12345678-1234-1234-1234-123456789abc", "bad\nlabel")]
    public void FromEnvironment_InvalidDeviceMetadata_Disables(string deviceId, string? label)
    {
        var values = new Dictionary<string, string?>(Valid)
        {
            ["ST_SYNC_DEVICE_ID"] = deviceId,
            ["ST_SYNC_DEVICE_LABEL"] = label,
        };
        Assert.False(AccountSyncOptions.FromEnvironment(name => values.GetValueOrDefault(name)).Enabled);
    }

    [Theory]
    [InlineData("http://localhost/sync")]
    [InlineData("http://127.0.0.1/sync")]
    [InlineData("https://example.com/sync")]
    public void FromEnvironment_AllowedBase_Enables(string value)
    {
        var values = new Dictionary<string, string?>(Valid) { ["ST_SYNC_BASE"] = value };
        Assert.True(AccountSyncOptions.FromEnvironment(name => values.GetValueOrDefault(name)).Enabled);
    }

    [Fact]
    public void FromEnvironment_DefaultLabelContainsOsButNotMachineName()
    {
        var options = AccountSyncOptions.FromEnvironment(name => Valid.GetValueOrDefault(name));
        Assert.StartsWith("JimsProxy ", options.DeviceLabel);
        Assert.DoesNotContain(Environment.MachineName, options.DeviceLabel, StringComparison.OrdinalIgnoreCase);
    }
}
