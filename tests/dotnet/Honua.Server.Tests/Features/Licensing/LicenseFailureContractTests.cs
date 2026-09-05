// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

public sealed class LicenseFailureContractTests
{
    [Theory]
    [InlineData("Pro", "missing")]
    [InlineData("Enterprise", "missing")]
    [InlineData("Pro", "invalid")]
    [InlineData("Enterprise", "invalid")]
    [InlineData("Pro", "expired")]
    [InlineData("Enterprise", "expired")]
    [Trait("Tier", "Fast")]
    public async Task StartAsync_UnusablePaidLicense_RefusesStartupWithStateAndRemedy(string edition, string state)
    {
        var license = LicenseTestSupport.CreateSignedLicense(
            Enum.Parse<HonuaEdition>(edition),
            expiresAt: DateTimeOffset.UtcNow.AddDays(state == "expired" ? -1 : 60),
            tamperSignature: state == "invalid");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Edition"] = edition,
            ["Licensing:LicenseContent"] = state == "missing" ? null : Encoding.UTF8.GetString(license.LicenseData),
            [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = license.PublicKeySetting
        }).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileBackedLicenseService.LoadBootstrapSnapshotAsync(configuration, NullLoggerFactory.Instance));

        Assert.Equal($"Honua {edition} startup refused: license {state}. Install a valid {edition} license in the configured licensing source and restart. Community fallback is disabled.", error.Message);
    }

    [UnitTest]
    public async Task StartAsync_CommunityWithoutLicense_RemainsAvailable()
    {
        var service = new FileBackedLicenseService(Options.Create(new LicenseOptions()),
            new BouncyCastleEd25519Verifier(), NullLogger<FileBackedLicenseService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(HonuaEdition.Community, service.GetSnapshot().Edition);
        Assert.True(service.GetSnapshot().HasEntitlement("temporal.filtering"));
        await service.StopAsync(CancellationToken.None);
    }
}
