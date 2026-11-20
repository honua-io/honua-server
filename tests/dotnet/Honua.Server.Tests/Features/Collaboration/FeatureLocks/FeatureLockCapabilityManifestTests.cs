// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

// The capability manifest publishes the feature-lock dispositions, not only the docs page. A client that reads
// /api/v1/capabilities/manifest learns that leases are enforced, that no lease can be granted until an authorizer
// is supplied, that cross-node leases need the PostgreSQL store, and that the GeoServices applyEdits surface honours
// no client-supplied version token. Both composition paths are covered (the hand-curated roster and the
// registry-derived projection), because the two must stay wire-identical.
[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Metadata)]
public sealed class FeatureLockCapabilityManifestTests(FeatureLockCapabilityManifestTests.SharedStoreHosts hosts)
    : IClassFixture<FeatureLockCapabilityManifestTests.SharedStoreHosts>
{
    private const string LocksCapability = "collaboration.feature-locks";
    private const string CrossNodeCapability = "collaboration.feature-locks.cross-node";
    private const string VersionTokenCapability = "edit.geoservices-version-tokens";

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_OutOfTheBox_ReportsLeasesEnforcedButUngrantable(bool fromRegistry)
    {
        var fixture = await hosts.GetAsync(fromRegistry ? "registry" : "curated");
        using var client = fixture.CreateAdminClient();

        using var document = await GetManifestAsync(client);
        var locks = Capability(document, LocksCapability);

        locks.GetProperty("category").GetString().Should().Be("collaboration");
        locks.GetProperty("lifecycle").GetString().Should().Be(
            "implemented",
            "lease enforcement ships on every feature-write path — it is not preview or experimental");
        locks.GetProperty("supported").GetBoolean().Should().BeTrue();

        // The whole point of publishing this row: the shipped authorizer denies every claim,
        // so a client that reads `available: true` here would be told to attempt a claim that
        // can only ever be refused.
        locks.GetProperty("available").GetBoolean().Should().BeFalse(
            "the authorizer Honua ships grants no lease");
        locks.GetProperty("reasonCode").GetString().Should().Be("disabled-by-configuration");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_WhenAnAuthorizerIsSupplied_ReportsLeasesAvailable(bool fromRegistry)
    {
        var fixture = await hosts.GetAsync(fromRegistry ? "registry authorizer" : "curated authorizer");
        using var client = fixture.CreateAdminClient();

        using var document = await GetManifestAsync(client);
        var locks = Capability(document, LocksCapability);

        // Proves the row is a live reading of the deployment rather than a hard-coded false:
        // the only difference from the test above is the registered IFeatureLockAuthorizer.
        locks.GetProperty("supported").GetBoolean().Should().BeTrue();
        locks.GetProperty("available").GetBoolean().Should().BeTrue(
            "a deployment that supplied an authorizer can grant leases");
        // An available capability carries no reason; the serializer omits the null property.
        (!locks.TryGetProperty("reasonCode", out var reason) || reason.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("an available capability must not publish an unavailability reason");
    }

    [IntegrationTheory]
    [InlineData(false, VersionTokenCapability)]
    [InlineData(true, VersionTokenCapability)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_DeclaresTheRecordedGaps_AsUnsupported(bool fromRegistry, string capabilityId)
    {
        // Configured with a real authorizer on purpose: neither gap may become "supported"
        // just because the deployment turned collaborative editing on.
        var fixture = await hosts.GetAsync(fromRegistry ? "registry authorizer" : "curated authorizer");
        using var client = fixture.CreateAdminClient();

        using var document = await GetManifestAsync(client);
        var gap = Capability(document, capabilityId);

        gap.GetProperty("supported").GetBoolean().Should().BeFalse(
            $"{capabilityId} is a recorded 2026.1 implementation gap");
        gap.GetProperty("available").GetBoolean().Should().BeFalse();
        gap.GetProperty("lifecycle").GetString().Should().Be("planned");
        gap.GetProperty("reasonCode").GetString().Should().Be("unsupported");
    }

    // Each host shape names its composition path ("registry" or the curated roster), whether it has the shared
    // PostgreSQL store, and whether an authorizer that can grant leases is registered.
    [IntegrationTheory]
    [InlineData("curated shared-store authorizer", null)]
    [InlineData("registry shared-store authorizer", null)]
    [InlineData("curated shared-store", "disabled-by-configuration")]
    [InlineData("registry shared-store", "disabled-by-configuration")]
    [InlineData("curated authorizer", "dependency-unavailable")]
    [InlineData("registry authorizer", "dependency-unavailable")]
    [InlineData("curated", "dependency-unavailable")]
    [InlineData("registry", "dependency-unavailable")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_CrossNodeAvailability_FollowsSelectedStoreAndAuthorizer(string hostShape, string? expectedReason)
    {
        var shape = hostShape.Split(' ');
        if (shape.Contains("shared-store"))
        {
            await AssertCrossNodeAsync(await hosts.GetAsync(hostShape), expectedReason);
            return;
        }

        await using var fixture = CreateFixture(shape.Contains("authorizer"), shape.Contains("registry"))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFeatureLockService, InMemoryFeatureLockService>();
            });
        await fixture.InitializeAsync();
        await AssertCrossNodeAsync(fixture, expectedReason);
    }

    private static async Task AssertCrossNodeAsync(WebAppFixture fixture, string? expectedReason)
    {
        using var client = fixture.CreateAdminClient();
        using var document = await GetManifestAsync(client);
        var crossNode = Capability(document, CrossNodeCapability);

        crossNode.GetProperty("supported").GetBoolean().Should().BeTrue("shared leases are implemented");
        crossNode.GetProperty("lifecycle").GetString().Should().Be("implemented");
        crossNode.GetProperty("available").GetBoolean().Should().Be(expectedReason is null);
        (crossNode.TryGetProperty("reasonCode", out var reason) && reason.ValueKind != JsonValueKind.Null
            ? reason.GetString()
            : null).Should().Be(expectedReason);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_CompositionPaths_AgreeOnEveryFeatureLockRow()
    {
        // The hand-curated roster and the registry projection are two spellings of one wire
        // document. If they drift, the disposition a client reads depends on a config flag.
        var curatedFixture = await hosts.GetAsync("curated");
        using var curatedClient = curatedFixture.CreateAdminClient();
        using var curated = await GetManifestAsync(curatedClient);

        var derivedFixture = await hosts.GetAsync("registry");
        using var derivedClient = derivedFixture.CreateAdminClient();
        using var derived = await GetManifestAsync(derivedClient);

        foreach (var id in new[] { LocksCapability, CrossNodeCapability, VersionTokenCapability })
        {
            Capability(derived, id).GetRawText().Should().Be(
                Capability(curated, id).GetRawText(),
                $"both composition paths must publish an identical '{id}' row");
        }
    }

    private static async Task<JsonDocument> GetManifestAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Capability(JsonDocument manifest, string id)
    {
        var match = manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(capability => capability.GetProperty("id").GetString() == id)
            .ToArray();
        match.Should().ContainSingle(
            $"the manifest must publish exactly one '{id}' row for a client to read the disposition from");
        return match[0];
    }

    private static WebAppFixture CreateFixture(bool allowWrite, bool fromRegistry)
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        if (allowWrite)
        {
            fixture = fixture.ConfigureServices(services =>
            {
                services.RemoveAll<IFeatureLockAuthorizer>();
                services.AddSingleton<IFeatureLockAuthorizer, AllowFeatureLockAuthorizer>();
            });
        }

        return fixture.ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capabilities:ManifestFromRegistry"] = fromRegistry ? "true" : "false",
                });
            });
        });
    }

    private sealed class AllowFeatureLockAuthorizer : IFeatureLockAuthorizer
    {
        public ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
            string mapId,
            FeatureRef feature,
            ClaimsPrincipal principal,
            string operation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FeatureLockAuthorizationResult.AllowWrite());
    }

    // One host per shared-store shape (composition path, and whether an authorizer that can grant
    // leases is registered). The manifest is read-only, so every case of a shape reads the same
    // host; shapes with the in-memory store keep their own hosts.
    public sealed class SharedStoreHosts : IAsyncLifetime
    {
        private readonly Dictionary<string, WebAppFixture> _applications = new(StringComparer.Ordinal);

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task<WebAppFixture> GetAsync(string hostShape)
        {
            var shape = hostShape.Split(' ');
            var allowWrite = shape.Contains("authorizer");
            var fromRegistry = shape.Contains("registry");
            var key = $"{(fromRegistry ? "registry" : "curated")}/{(allowWrite ? "authorizer" : "denying")}";
            if (_applications.TryGetValue(key, out var started))
            {
                return started;
            }

            var application = CreateFixture(allowWrite, fromRegistry);
            try
            {
                await application.InitializeAsync();
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }

            _applications[key] = application;
            return application;
        }

        public async Task DisposeAsync()
        {
            foreach (var application in _applications.Values)
            {
                await application.DisposeAsync();
            }
        }
    }
}
