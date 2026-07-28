// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Guards the startup output-cache entitlement gate
/// (<see cref="StartupConfigurationHelpers.IsOutputCacheEntitledAsync"/>, #2998). The gate
/// decides whether <c>Program</c> wires <c>app.UseOutputCache()</c>; mirrors
/// <see cref="RedisCacheEntitlementGateTests"/> for the boot-time <c>caching.redis</c> gate —
/// <c>Licensing:DevGrantEdition</c> is honored outside Production (#1787) and never in
/// Production.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class OutputCacheEntitlementGateTests
{
    [UnitTest]
    public async Task IsOutputCacheEntitled_DevGrantPro_InDevelopment_EntitlesOutputCache()
    {
        var configuration = BuildConfiguration(devGrantEdition: "Pro");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsOutputCacheEntitledAsync(configuration, environment);

        entitled.Should().BeTrue(
            "DevGrantEdition=Pro grants caching.output-cache at runtime, so the startup gate " +
            "must agree and wire the output-cache middleware");
    }

    [UnitTest]
    public async Task IsOutputCacheEntitled_NoLicenseNoDevGrant_DoesNotEntitleOutputCache()
    {
        var configuration = BuildConfiguration(devGrantEdition: null);
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsOutputCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "without a signed license or a dev grant the Community snapshot does not include caching.output-cache");
    }

    [UnitTest]
    public async Task IsOutputCacheEntitled_DevGrantPro_InProduction_DoesNotEntitleOutputCache()
    {
        // Fail-closed: the dev-only override must never relax a startup gate in Production.
        var configuration = BuildConfiguration(devGrantEdition: "Pro");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var entitled = await StartupConfigurationHelpers.IsOutputCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "DevGrantEdition is a test/dev-only override and must be ignored by the gate in Production");
    }

    [UnitTest]
    public async Task IsOutputCacheEntitled_DevGrantCommunity_DoesNotEntitleOutputCache()
    {
        // caching.output-cache is a Pro entitlement; a Community dev grant must not unlock it.
        var configuration = BuildConfiguration(devGrantEdition: "Community");
        var environment = new TestHostEnvironment { EnvironmentName = Environments.Development };

        var entitled = await StartupConfigurationHelpers.IsOutputCacheEntitledAsync(configuration, environment);

        entitled.Should().BeFalse(
            "caching.output-cache requires Pro; a Community dev grant does not reach that edition");
    }

    private static IConfiguration BuildConfiguration(string? devGrantEdition)
    {
        var settings = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(devGrantEdition))
        {
            settings["Licensing:DevGrantEdition"] = devGrantEdition;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Honua.Server.Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
