// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Capabilities;
using Honua.Server.Features.Capabilities.Models;
using Honua.Server.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Ensures admin discovery reports the same effective grants as runtime capabilities.
/// These are pure DI/handler tests and do not start a host, database or Redis.
/// </summary>
public sealed class LicenseEffectiveEntitlementConsistencyTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("Enabled", "Enterprise")]
    [InlineData("Disabled", null)]
    public async Task GetEntitlements_EffectiveDevelopmentOrDisabledGrant_MatchesCapabilityPolicies(
        string mode, string? grant)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Licensing:Mode"] = mode,
                ["Licensing:DevGrantEdition"] = grant,
            }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHonuaLicensing(configuration, environment);
        await using var provider = services.BuildServiceProvider();
        var effective = provider.GetRequiredService<ILicenseEntitlementService>();
        var manager = provider.GetRequiredService<ILicenseManager>();

        // Reproduce the real enabled-development split: the persisted license
        // is Community, while the explicit effective grant is Enterprise.
        var persisted = await manager.GetEntitlementsAsync();
        Assert.Equal(mode == "Disabled", Assert.Single(persisted, row => row.Key == "caching.redis").IsActive);
        Assert.True(effective.CheckEntitlement("caching.redis").IsActive);
        Assert.True(effective.CheckEntitlement("routing.solve").IsActive);

        var handler = typeof(LicenseEndpoints).GetMethod("HandleGetEntitlements",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(handler);
        var result = Assert.IsType<Results<Ok<ApiResponse<IReadOnlyList<EntitlementResponse>>>, ProblemHttpResult>>(
            handler.Invoke(null, [effective, NullLogger<LicenseEndpoints.LicenseEndpointsLog>.Instance,
                new DefaultHttpContext()]));
        var response = Assert.IsType<Ok<ApiResponse<IReadOnlyList<EntitlementResponse>>>>(result.Result);
        Assert.NotNull(response.Value);
        Assert.True(response.Value.Success);
        Assert.NotNull(response.Value.Data);

        // Exercise the actual manifest policy builder, without unrelated
        // metadata/database services or a second imitation entitlement codec.
        var manifest = new CapabilityManifestService(null!, effective, null!, null!, null!,
            [], null!, null!, NullLogger<CapabilityManifestService>.Instance);
        var buildPolicies = typeof(CapabilityManifestService).GetMethod("BuildPolicies",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(buildPolicies);
        var policies = Assert.IsType<CapabilityManifestPolicies>(buildPolicies.Invoke(manifest,
            [effective.GetSnapshot(), Array.Empty<string>()]));
        foreach (var key in new[] { "caching.redis", "routing.solve" })
        {
            var admin = Assert.Single(response.Value.Data, row => row.Key == key);
            var capability = Assert.Single(policies.Entitlements, row => row.Key == key);
            Assert.True(admin.IsActive);
            Assert.Equal(capability.Active, admin.IsActive);
        }

        // Reading effective grants must not replace or mutate persisted status.
        Assert.Equal(mode == "Disabled", Assert.Single(await manager.GetEntitlementsAsync(),
            row => row.Key == "caching.redis").IsActive);
    }
}
