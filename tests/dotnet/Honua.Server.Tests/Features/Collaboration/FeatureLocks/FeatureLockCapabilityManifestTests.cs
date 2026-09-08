// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Proves the recorded honua-server#4402 dispositions are published by the machine-readable
/// capability manifest, not only by the docs page (acceptance criterion 1).
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/reference/collaboration/feature-locks.md</c> tells a client that everything the
/// disposition table answers "not implemented" is reported that way by the capability
/// manifest "so a client can discover it without reading this page". These tests are what
/// makes that sentence true and keeps it true: a client that reads
/// <c>/api/v1/capabilities/manifest</c> learns that leases are enforced, that no lease can
/// be granted until an authorizer is supplied, that leases do not span nodes, and that the
/// GeoServices <c>applyEdits</c> surface honours no client-supplied version token.
/// </para>
/// <para>
/// Both manifest composition paths are covered — the hand-curated roster and the #2335
/// registry-derived projection — because the two must stay wire-identical, and a gap that
/// is honest on only one of them is a gap a client can still be lied to about.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(Honua.TestKit.Constants.ProtocolNames.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Metadata)]
public sealed class FeatureLockCapabilityManifestTests
{
    private const string LocksCapability = "collaboration.feature-locks";
    private const string CrossNodeCapability = "collaboration.feature-locks.cross-node";
    private const string VersionTokenCapability = "edit.version-tokens";

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_OutOfTheBox_ReportsLeasesEnforcedButUngrantable(bool fromRegistry)
    {
        await using var fixture = CreateFixture(allowWrite: false, fromRegistry: fromRegistry);
        await fixture.InitializeAsync();
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
        await using var fixture = CreateFixture(allowWrite: true, fromRegistry: fromRegistry);
        await fixture.InitializeAsync();
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
    [InlineData(false, CrossNodeCapability)]
    [InlineData(true, CrossNodeCapability)]
    [InlineData(false, VersionTokenCapability)]
    [InlineData(true, VersionTokenCapability)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_DeclaresTheRecordedGaps_AsUnsupported(bool fromRegistry, string capabilityId)
    {
        // Configured with a real authorizer on purpose: neither gap may become "supported"
        // just because the deployment turned collaborative editing on.
        await using var fixture = CreateFixture(allowWrite: true, fromRegistry: fromRegistry);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var document = await GetManifestAsync(client);
        var gap = Capability(document, capabilityId);

        gap.GetProperty("supported").GetBoolean().Should().BeFalse(
            $"{capabilityId} is a recorded 2026.1 implementation gap");
        gap.GetProperty("available").GetBoolean().Should().BeFalse();
        gap.GetProperty("lifecycle").GetString().Should().Be("planned");
        gap.GetProperty("reasonCode").GetString().Should().Be("unsupported");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_CompositionPaths_AgreeOnEveryFeatureLockRow()
    {
        // The hand-curated roster and the registry projection are two spellings of one wire
        // document. If they drift, the disposition a client reads depends on a config flag.
        await using var curatedFixture = CreateFixture(allowWrite: false, fromRegistry: false);
        await curatedFixture.InitializeAsync();
        using var curatedClient = curatedFixture.CreateAdminClient();
        using var curated = await GetManifestAsync(curatedClient);

        await using var derivedFixture = CreateFixture(allowWrite: false, fromRegistry: true);
        await derivedFixture.InitializeAsync();
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
}
