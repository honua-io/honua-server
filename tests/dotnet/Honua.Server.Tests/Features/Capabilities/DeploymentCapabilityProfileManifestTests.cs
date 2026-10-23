// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Capabilities;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Capabilities;

/// <summary>
/// A configured deployment profile removes routes and must not still advertise those
/// capabilities as available (honua-server#4545).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Metadata)]
public sealed class DeploymentCapabilityProfileManifestTests
{
    private static readonly string[] EnabledCapabilities =
    [
        "discovery.capability-manifest",
        "temporal.filtering",
    ];

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task ProfileOmittingMapSessions_RemovesFeatureLocksButKeepsEnabledCapability(bool fromRegistry)
    {
        await using var fixture = CreateFixture(fromRegistry, HonuaEdition.Pro, EnabledCapabilities);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = manifest.RootElement;

        root.GetProperty("deploymentProfile").GetProperty("configured").GetBoolean().Should().BeTrue();
        root.GetProperty("deploymentProfile").GetProperty("enabledCapabilities").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(EnabledCapabilities);

        var locks = Capability(root, "collaboration.feature-locks");
        locks.GetProperty("supported").GetBoolean().Should().BeTrue();
        locks.GetProperty("available").GetBoolean().Should().BeFalse(
            "the feature-lock routes resolve to collaboration.map-sessions, which this profile omits");
        locks.GetProperty("reasonCode").GetString().Should().Be("deployment-profile-excluded");

        var filtering = Capability(root, "temporal.filtering");
        filtering.GetProperty("available").GetBoolean().Should().BeTrue(
            "the profile enables temporal.filtering and its other gates pass");
        (!filtering.TryGetProperty("reasonCode", out var filteringReason) || filteringReason.ValueKind == JsonValueKind.Null)
            .Should().BeTrue();

        var query = Capability(root, "query.features");
        query.GetProperty("available").GetBoolean().Should().BeTrue(
            "a capability with no mapped route is unchanged by the profile");

        var crossNode = Capability(root, "collaboration.feature-locks.cross-node");
        crossNode.GetProperty("supported").GetBoolean().Should().BeFalse();
        crossNode.GetProperty("available").GetBoolean().Should().BeFalse();
        crossNode.GetProperty("reasonCode").GetString().Should().Be("unsupported");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task ProfileOmittingCertificateRoutes_DoesNotExcludeConfiguredMtls(bool fromRegistry)
    {
        await using var fixture = CreateFixture(
            fromRegistry,
            HonuaEdition.Enterprise,
            ["admin.control-plane", "discovery.capability-manifest"],
            new Dictionary<string, string?>
            {
                ["Capabilities:Experimental:security.mtls:Enabled"] = "true",
                ["Authentication:ClientCertificates:Mode"] = "RequiredForAdmin",
            });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = manifest.RootElement;

        root.GetProperty("deploymentProfile").GetProperty("enabledCapabilities").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("admin.control-plane", "discovery.capability-manifest");

        var mtls = Capability(root, "security.mtls");
        mtls.GetProperty("available").GetBoolean().Should().BeTrue(
            "certificate enforcement still applies to retained admin routes when the profile omits certificate-management routes");
        (!mtls.TryGetProperty("reasonCode", out var mtlsReason) || mtlsReason.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("configured mTLS must not be deployment-profile-excluded");

        var locks = Capability(root, "collaboration.feature-locks");
        locks.GetProperty("available").GetBoolean().Should().BeFalse();
        locks.GetProperty("reasonCode").GetString().Should().Be("deployment-profile-excluded");
    }

    private static JsonElement Capability(JsonElement root, string id)
    {
        var match = root.GetProperty("capabilities").EnumerateArray()
            .Where(capability => capability.GetProperty("id").GetString() == id)
            .ToArray();
        match.Should().ContainSingle();
        return match[0];
    }

    private static WebAppFixture CreateFixture(
        bool fromRegistry,
        HonuaEdition edition,
        string[] capabilities,
        IReadOnlyDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            [DeploymentCapabilityProfile.EnabledCapabilitiesKey] = string.Join(',', capabilities),
            [DeploymentCapabilityProfile.SchemaVersionKey] = DeploymentCapabilityProfile.SupportedSchemaVersion,
            ["Capabilities:ManifestFromRegistry"] = fromRegistry ? "true" : "false",
        };
        if (extraSettings is not null)
        {
            foreach (var setting in extraSettings)
            {
                settings[setting.Key] = setting.Value;
            }
        }

        var fixture = new WebAppFixture()
            .WithTestLicense(edition)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IFeatureLockAuthorizer>();
                services.AddSingleton<IFeatureLockAuthorizer, AllowFeatureLockAuthorizer>();
            });

        return fixture.ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings));
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
