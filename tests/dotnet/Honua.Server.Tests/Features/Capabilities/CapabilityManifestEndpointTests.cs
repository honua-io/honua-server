// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Services;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Capabilities;

/// <summary>
/// Integration coverage for the public server capability manifest endpoint (#1186).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Metadata)]
public sealed class CapabilityManifestEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _anonymousClient = null!;
    private HttpClient _adminClient = null!;

    public CapabilityManifestEndpointTests()
    {
        _fixture = CreateManifestFixture();
    }

    private static WebAppFixture CreateManifestFixture(
        string[]? entitlements = null,
        HonuaEdition edition = HonuaEdition.Pro,
        bool manifestFromRegistry = false,
        bool experimentalGlobalEnabled = true,
        bool tenantSchemaRoutingEnabled = false)
        => new WebAppFixture()
            .WithTestLicense(edition, entitlements: entitlements)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2EnvironmentSnapshotReader>();
                services.AddSingleton<IMetadataV2EnvironmentSnapshotReader>(
                    new StaticEnvironmentSnapshotReader("test"));
            })
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MultiTenancy:DefaultTenantId"] = "tenant-manifest",
                        ["Limits:MaxUploadSizeBytes"] = "123456",
                        ["Limits:Analytics:MaxDbscanEpsMeters"] = "12345.5",
                        ["Limits:Analytics:MaxKMeansK"] = "45",
                        ["Limits:Analytics:MaxBufferDistanceMeters"] = "23456.5",
                        ["Limits:Analytics:MinDensityCellSizeMeters"] = "12.5",
                        ["Limits:Analytics:MaxDensityCellSizeMeters"] = "34567.5",
                        ["Limits:Analytics:MaxDWithinDistanceMeters"] = "45678.5",
                        ["FeatureStreaming:MaxConcurrentSessions"] = "12",
                        ["Grpc:StreamBatchSize"] = "42",
                        ["Capabilities:ManifestFromRegistry"] = manifestFromRegistry ? "true" : "false",
                        ["Capabilities:Experimental:Enabled"] = experimentalGlobalEnabled ? "true" : "false",
                        ["MultiTenancy:SchemaRouting:Enabled"] = tenantSchemaRoutingEnabled ? "true" : "false",
                    });
                });
            });

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_LegacyProjection_MarksDisabledPreviewsUnavailable()
    {
        var fixture = CreateManifestFixture(experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            foreach (var id in new[] { "admin.multi-tenancy", "realtime.feature-streams", "serve.sensorthings" })
            {
                var capability = GetCapability(document.RootElement, id);
                capability.GetProperty("lifecycle").GetString().Should().Be("preview");
                capability.GetProperty("optInRequired").GetBoolean().Should().BeTrue();
                capability.GetProperty("available").GetBoolean().Should().BeFalse();
                capability.GetProperty("reasonCode").GetString().Should().Be("disabled-by-configuration");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_LifecycleOnlyPreviews_RemainAvailableWithoutOptIn(bool fromRegistry)
    {
        var fixture = CreateManifestFixture(manifestFromRegistry: fromRegistry, experimentalGlobalEnabled: false);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = await ReadDocumentAsync(response);
            foreach (var capability in new[] { "serve.geoservices-imageserver", "serve.wmts", "serve.ogc-api-coverages" }
                .Select(id => GetCapability(document.RootElement, id)))
            {
                capability.GetProperty("lifecycle").GetString().Should().Be("preview");
                capability.GetProperty("available").GetBoolean().Should().BeTrue();
                capability.GetProperty("optInRequired").GetBoolean().Should().BeFalse();
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_MultiTenancy_ReflectsRuntimeLicenseAndAuthentication()
    {
        foreach (var fixture in new[] { false, true }.Select(fromRegistry => CreateManifestFixture(
                     edition: HonuaEdition.Enterprise,
                     manifestFromRegistry: fromRegistry,
                     tenantSchemaRoutingEnabled: true)))
        {
            await fixture.InitializeAsync();

            try
            {
                using var anonymousClient = fixture.CreateClient();
                using var anonymousResponse = await anonymousClient.GetAsync("/api/v1/capabilities/manifest");
                using var anonymousDocument = await ReadDocumentAsync(anonymousResponse);
                var anonymousCapability = GetCapability(anonymousDocument.RootElement, "admin.multi-tenancy");
                anonymousCapability.GetProperty("available").GetBoolean().Should().BeFalse();
                anonymousCapability.GetProperty("reasonCode").GetString().Should().Be("insufficient-policy");
                anonymousCapability.GetProperty("entitlementKey").GetString().Should().Be(FeatureCatalog.MultiTenancyKey);
                anonymousCapability.GetProperty("minimumEdition").GetString().Should().Be("Enterprise");

                using var adminClient = fixture.CreateAdminClient();
                using var adminResponse = await adminClient.GetAsync("/api/v1/capabilities/manifest");
                using var adminDocument = await ReadDocumentAsync(adminResponse);
                GetCapability(adminDocument.RootElement, "admin.multi-tenancy")
                    .GetProperty("available").GetBoolean().Should().BeTrue();
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_MultiTenancyWithoutSchemaRouting_IsDisabledByConfiguration()
    {
        var fixture = CreateManifestFixture(
            edition: HonuaEdition.Enterprise,
            manifestFromRegistry: true,
            tenantSchemaRoutingEnabled: false);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");
            using var document = await ReadDocumentAsync(response);
            var capability = GetCapability(document.RootElement, "admin.multi-tenancy");
            capability.GetProperty("available").GetBoolean().Should().BeFalse();
            capability.GetProperty("reasonCode").GetString().Should().Be("disabled-by-configuration");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateWorkspaceScopedManifestFixture()
        => CreateManifestFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoleStore>();
                services.AddSingleton<IRoleStore, CapabilityManifestRoleStore>();
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, CapabilityManifestTestAuthHandler>(
                        CapabilityManifestTestAuthHandler.SchemeName,
                        static _ => { });
                services.PostConfigureAll<AuthenticationOptions>(static options =>
                {
                    options.DefaultAuthenticateScheme = CapabilityManifestTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = CapabilityManifestTestAuthHandler.SchemeName;
                    options.DefaultScheme = CapabilityManifestTestAuthHandler.SchemeName;
                });
            });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _anonymousClient = _fixture.CreateClient();
        _adminClient = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _anonymousClient.Dispose();
        _adminClient.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_AsAnonymous_ReturnsPublicTenantManifestAndNoStoreHeaders()
    {
        using var response = await _anonymousClient.GetAsync("/api/v1/capabilities/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.Pragma.Select(value => value.Name).Should().Contain("no-cache");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var document = await ReadDocumentAsync(response);
        var root = document.RootElement;
        root.GetProperty("schemaVersion").GetString().Should().Be("honua.capability_manifest.v1");

        var scope = root.GetProperty("scope");
        scope.GetProperty("tenantId").GetString().Should().Be("tenant-manifest");
        scope.GetProperty("tenantSource").GetString().Should().Be("Default");
        scope.GetProperty("authenticated").GetBoolean().Should().BeFalse();

        GetCapability(root, "query.features").GetProperty("available").GetBoolean().Should().BeTrue();
        GetCapability(root, "upload.file").GetProperty("available").GetBoolean().Should().BeFalse();
        GetCapability(root, "upload.file").GetProperty("reasonCode").GetString().Should().Be("insufficient-policy");
        GetCapability(root, "edit.features").GetProperty("entitlementKey").GetString()
            .Should().Be(FeatureCatalog.FeatureServerEditsKey);
        root.GetProperty("policies").GetProperty("callerCapabilities").EnumerateArray().Should().BeEmpty();
        GetLink(root, "feature-streaming-capabilities").GetProperty("href").GetString()
            .Should().Be("/api/v1/streaming/features/capabilities");
        GetTransport(root, "mcp").GetProperty("available").GetBoolean().Should().BeTrue();
        GetTransport(root, "qgis").GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_AdvertisesRolledUpContractVersionsForProtocolSurfaces()
    {
        // honua-release#32 / ADR-0058: the GeoServices/OGC/STAC surfaces must advertise a
        // real rolled-up wire-contract version (instead of nothing, which the platform
        // manifest was pinning at v0). The version is owned by the protocol assembly that
        // owns the surface, projected onto the manifest transport entry.
        using var response = await _anonymousClient.GetAsync("/api/v1/capabilities/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        var root = document.RootElement;

        GetTransport(root, "geoservices-rest").GetProperty("contractVersion").GetString()
            .Should().Be(GeoServicesContract.Version);
        GetTransport(root, "ogc-http").GetProperty("contractVersion").GetString()
            .Should().Be(OgcContract.Version);
        GetTransport(root, "stac").GetProperty("contractVersion").GetString()
            .Should().Be(StacContract.Version);

        // No surface may advertise an empty/placeholder ("v0"/nothing) contract version.
        foreach (var surface in new[] { "geoservices-rest", "ogc-http", "stac" })
        {
            var version = GetTransport(root, surface).GetProperty("contractVersion").GetString();
            version.Should().NotBeNullOrWhiteSpace();
            version.Should().NotBe("v0");
        }

        // Transports with no versioned wire contract of their own omit the field entirely
        // (DefaultIgnoreCondition.WhenWritingNull), so consumers can tell "unversioned"
        // from "advertises a real version".
        GetTransport(root, "grpc").TryGetProperty("contractVersion", out _).Should().BeFalse();

        // The rolled-up contract version is NOT an ArcGIS Server/Portal version: the
        // GeoServices wire models still carry no currentVersion/fullVersion
        // (NoArcGisServerVersionTests), and this field lives on the capability manifest,
        // not on any GeoServices service-info response.
        GetTransport(root, "geoservices-rest").TryGetProperty("currentVersion", out _).Should().BeFalse();
        GetTransport(root, "geoservices-rest").TryGetProperty("fullVersion", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_AsAdminWithEnvironmentAndWorkspace_ReturnsFilteredManifest()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/capabilities/manifest?environment=test&workspaceId=field-team");
        request.Headers.Accept.ParseAdd("application/vnd.honua.capability-manifest+json");

        using var response = await _adminClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should()
            .Be("application/vnd.honua.capability-manifest+json");

        using var document = await ReadDocumentAsync(response);
        var root = document.RootElement;
        var scope = root.GetProperty("scope");
        scope.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        scope.GetProperty("environment").GetString().Should().Be("test");
        scope.GetProperty("workspaceId").GetString().Should().Be("field-team");
        scope.GetProperty("workspaceAvailable").GetBoolean().Should().BeTrue();

        var environment = root.GetProperty("environment");
        environment.GetProperty("requested").GetBoolean().Should().BeTrue();
        environment.GetProperty("available").GetBoolean().Should().BeTrue();
        environment.GetProperty("environmentId").GetString().Should().Be("test");

        var policies = root.GetProperty("policies");
        policies.GetProperty("currentEdition").GetString().Should().Be("Pro");
        policies.GetProperty("callerCapabilities").EnumerateArray()
            .Select(item => item.GetString())
            .Should().Contain("admin.rbac.write");

        var offlineSync = GetCapability(root, "sync.offline");
        offlineSync.GetProperty("category").GetString().Should().Be("sync");
        offlineSync.GetProperty("entitlementKey").GetString().Should().Be(FeatureCatalog.FieldOpsOfflineSyncKey);
        offlineSync.GetProperty("minimumEdition").GetString().Should().Be("Pro");

        var specApply = GetCapability(root, "ai.spec-apply");
        specApply.GetProperty("available").GetBoolean().Should().BeTrue();
        specApply.GetProperty("entitlementKey").GetString().Should().Be(FeatureCatalog.AiSpecApplyKey);
        GetCapability(root, "ai.grounding").GetProperty("entitlementKey").GetString()
            .Should().Be(FeatureCatalog.AiGroundingKey);
        var animation = GetCapability(root, "temporal.animation-api");
        animation.GetProperty("available").GetBoolean().Should().BeTrue();
        animation.GetProperty("entitlementKey").GetString().Should().Be("temporal.animation-api");
        animation.GetProperty("minimumEdition").GetString().Should().Be("Pro");
        GetCapability(root, "publication.metadata-release").GetProperty("available").GetBoolean().Should().BeTrue();
        root.GetProperty("limits").GetProperty("upload").GetProperty("maxUploadSizeBytes").GetInt64()
            .Should().Be(123456);
        root.GetProperty("limits").GetProperty("streaming").GetProperty("maxConcurrentSessions").GetInt32()
            .Should().Be(12);
        root.GetProperty("limits").GetProperty("streaming").GetProperty("grpcStreamBatchSize").GetInt32()
            .Should().Be(42);

        var analysis = root.GetProperty("limits").GetProperty("analysis");
        analysis.GetProperty("maxDbscanEpsMeters").GetDouble().Should().Be(12345.5);
        analysis.GetProperty("maxKMeansK").GetInt32().Should().Be(45);
        analysis.GetProperty("maxBufferDistanceMeters").GetDouble().Should().Be(23456.5);
        analysis.GetProperty("minDensityCellSizeMeters").GetDouble().Should().Be(12.5);
        analysis.GetProperty("maxDensityCellSizeMeters").GetDouble().Should().Be(34567.5);
        analysis.GetProperty("maxDWithinDistanceMeters").GetDouble().Should().Be(45678.5);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithPartialSpatialAnalysisEntitlements_MarksAnalysisUnavailable()
    {
        var fixture = CreateManifestFixture(entitlements: ["analytics.clustering"]);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            var analysis = GetCapability(document.RootElement, "analysis.spatial");
            analysis.GetProperty("available").GetBoolean().Should().BeFalse();
            analysis.GetProperty("reasonCode").GetString().Should().Be("entitlement-inactive");
            analysis.TryGetProperty("entitlementKey", out _).Should().BeFalse();
            analysis.GetProperty("entitlementKeys").EnumerateArray()
                .Select(item => item.GetString())
                .Should().Equal(
                    "analytics.clustering",
                    "analytics.spatial-join",
                    "analytics.buffer-aggregate",
                    "analytics.density");

            var mapPackage = GetCapability(document.RootElement, "package.map");
            mapPackage.GetProperty("available").GetBoolean().Should().BeTrue();
            mapPackage.TryGetProperty("reasonCode", out _).Should().BeFalse();
            mapPackage.TryGetProperty("entitlementKey", out _).Should().BeFalse();

            var appPackage = GetCapability(document.RootElement, "package.app");
            appPackage.GetProperty("available").GetBoolean().Should().BeTrue();
            appPackage.TryGetProperty("reasonCode", out _).Should().BeFalse();
            appPackage.TryGetProperty("entitlementKey", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithCommunityLicense_MarksOfflineSyncAndAiOperationsUnavailable()
    {
        var fixture = CreateManifestFixture(edition: HonuaEdition.Community);
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(
                "/api/v1/capabilities/manifest?workspaceId=field-team");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            var root = document.RootElement;

            var offlineSync = GetCapability(root, "sync.offline");
            offlineSync.GetProperty("available").GetBoolean().Should().BeFalse();
            offlineSync.GetProperty("reasonCode").GetString().Should().Be("license-required");
            offlineSync.GetProperty("entitlementKey").GetString().Should().Be(FeatureCatalog.FieldOpsOfflineSyncKey);

            foreach (var capability in new[] { "ai.spec-apply", "ai.grounding" }
                .Select(capabilityId => GetCapability(root, capabilityId)))
            {
                capability.GetProperty("available").GetBoolean().Should().BeFalse();
                capability.GetProperty("reasonCode").GetString().Should().Be("license-required");
                capability.GetProperty("minimumEdition").GetString().Should().Be("Pro");
            }

            var animation = GetCapability(root, "temporal.animation-api");
            animation.GetProperty("available").GetBoolean().Should().BeFalse();
            animation.GetProperty("reasonCode").GetString().Should().Be("license-required");
            animation.GetProperty("entitlementKey").GetString().Should().Be("temporal.animation-api");
            animation.GetProperty("minimumEdition").GetString().Should().Be("Pro");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithWorkspaceClaimTypeDifferentCasing_MatchesRbacWorkspaceScope()
    {
        var fixture = CreateWorkspaceScopedManifestFixture();
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateClient(httpClient =>
            {
                httpClient.DefaultRequestHeaders.Add(CapabilityManifestTestAuthHandler.UserHeader, "workspace-user");
                httpClient.DefaultRequestHeaders.Add(CapabilityManifestTestAuthHandler.RolesHeader, "editor");
                httpClient.DefaultRequestHeaders.Add(CapabilityManifestTestAuthHandler.WorkspaceClaimTypeHeader, "GROUPS");
                httpClient.DefaultRequestHeaders.Add(CapabilityManifestTestAuthHandler.WorkspaceScopeHeader, "field-team");
            });
            using var response = await client.GetAsync(
                "/api/v1/capabilities/manifest?workspaceId=field-team");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = await ReadDocumentAsync(response);
            var scope = document.RootElement.GetProperty("scope");
            scope.GetProperty("authenticated").GetBoolean().Should().BeTrue();
            scope.GetProperty("workspaceId").GetString().Should().Be("field-team");
            scope.GetProperty("workspaceAvailable").GetBoolean().Should().BeTrue();
            scope.TryGetProperty("workspaceReasonCode", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithUnknownEnvironment_ReturnsUnavailableManifestState()
    {
        using var response = await _adminClient.GetAsync(
            "/api/v1/capabilities/manifest?environment=missing-env");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = await ReadDocumentAsync(response);
        var root = document.RootElement;
        var environment = root.GetProperty("environment");
        environment.GetProperty("requested").GetBoolean().Should().BeTrue();
        environment.GetProperty("available").GetBoolean().Should().BeFalse();
        environment.GetProperty("reasonCode").GetString().Should().Be("environment-unavailable");

        var publication = GetCapability(root, "publication.metadata-release");
        publication.GetProperty("available").GetBoolean().Should().BeFalse();
        publication.GetProperty("reasonCode").GetString().Should().Be("environment-unavailable");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_RollbackIgnoresTargetsFromOtherEnvironments()
    {
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("test-rollback");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(
            new DeployBackendCapabilities { SupportsRollback = true });
        var fixture = CreateManifestFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDeployBackend>();
                services.AddSingleton(backend);
                services.AddSingleton(Substitute.For<IWorkflowOperationStore>());
                services.Configure<ControlPlaneOptions>(configured => configured.DeployTargets.AddRange(
                [
                    new DeployTargetOptions
                    {
                        TargetId = "rollback-prod",
                        Backend = backend.BackendName,
                        TargetKind = backend.TargetKind,
                        Environment = "prod"
                    }
                ]));
            });
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(
                "/api/v1/capabilities/manifest?environment=test");
            using var document = await ReadDocumentAsync(response);

            GetCapability(document.RootElement, "deploy.rollback")
                .GetProperty("available").GetBoolean().Should().BeFalse(
                    "the durable store is present but the only rollback target belongs to prod");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_RollbackRequiresDurableOperationStore()
    {
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns("test-rollback");
        backend.TargetKind.Returns(DeployTargetKind.Kubernetes);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(
            new DeployBackendCapabilities { SupportsRollback = true });
        var fixture = CreateManifestFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDeployBackend>();
                services.AddSingleton(backend);
                services.Configure<ControlPlaneOptions>(configured => configured.DeployTargets.Add(
                    new DeployTargetOptions
                    {
                        TargetId = "rollback-test",
                        Backend = backend.BackendName,
                        TargetKind = backend.TargetKind,
                        Environment = "test"
                    }));
            });
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync(
                "/api/v1/capabilities/manifest?environment=test");
            using var document = await ReadDocumentAsync(response);

            GetCapability(document.RootElement, "deploy.rollback")
                .GetProperty("available").GetBoolean().Should().BeFalse(
                    "the matching backend supports rollback but no durable operation store is composed");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_AutonomyTracksPolicyStoreInsteadOfWorkflowStore()
    {
        var fixture = CreateManifestFixture()
            .ConfigureServices(services => services.AddSingleton<IOpsAutonomyPolicyStore>(
                new InMemoryOpsAutonomyPolicyStore()));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");
            using var document = await ReadDocumentAsync(response);

            GetCapability(document.RootElement, "ops.autonomy")
                .GetProperty("available").GetBoolean().Should().BeTrue(
                    "the autonomy policy store is the endpoint's actual runtime dependency");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithInvalidScopeIdentifier_ReturnsSafeBadRequest()
    {
        using var response = await _anonymousClient.GetAsync(
            "/api/v1/capabilities/manifest?workspaceId=bad/value");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("workspaceId contains unsupported characters.");
        body.Should().NotContain("CapabilityManifestService");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_RegistryDerived_MatchesHandCuratedComposition()
    {
        // #2335 (B3) golden before/after: the registry-derived composition
        // (Capabilities:ManifestFromRegistry=true) must serialize a byte-identical
        // Capabilities[] and Packages.Families[] to the legacy hand-curated path while
        // every descriptor stays Implemented, proving entitlement/availability
        // resolution is unchanged.
        const string query = "/api/v1/capabilities/manifest?environment=test&workspaceId=field-team";

        var legacyFixture = CreateManifestFixture();
        var derivedFixture = CreateManifestFixture(manifestFromRegistry: true);
        await legacyFixture.InitializeAsync();
        await derivedFixture.InitializeAsync();

        try
        {
            using var legacyClient = legacyFixture.CreateAdminClient();
            using var derivedClient = derivedFixture.CreateAdminClient();
            using var legacyResponse = await legacyClient.GetAsync(query);
            using var derivedResponse = await derivedClient.GetAsync(query);

            legacyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            derivedResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var legacyDocument = await ReadDocumentAsync(legacyResponse);
            using var derivedDocument = await ReadDocumentAsync(derivedResponse);

            var derivedCapabilities = derivedDocument.RootElement.GetProperty("capabilities").GetRawText();
            var legacyCapabilities = legacyDocument.RootElement.GetProperty("capabilities").GetRawText();
            derivedCapabilities.Should().Be(legacyCapabilities);

            var derivedFamilies = derivedDocument.RootElement
                .GetProperty("packages").GetProperty("families").GetRawText();
            var legacyFamilies = legacyDocument.RootElement
                .GetProperty("packages").GetProperty("families").GetRawText();
            derivedFamilies.Should().Be(legacyFamilies);
        }
        finally
        {
            await legacyFixture.DisposeAsync();
            await derivedFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithoutDurableJobStore_ReportsJobsRunnerDependencyUnavailable()
    {
        // honua-release#202: Redis is optional for a local install. A compute backend is always
        // registered, so `supported` alone over-claims — without the Redis-backed durable job
        // store nothing can be submitted. The manifest must say so with a machine-readable
        // reason code rather than advertising jobs.runner as available while every submission
        // is refused. The WebAppFixture runs without Redis, so no IExecutionJobStore exists.
        using var response = await _adminClient.GetAsync("/api/v1/capabilities/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);
        var root = document.RootElement;

        var jobsRunner = GetCapability(root, "jobs.runner");
        jobsRunner.GetProperty("supported").GetBoolean().Should().BeTrue(
            "the capability is implemented and a compute backend is registered");
        jobsRunner.GetProperty("available").GetBoolean().Should().BeFalse(
            "no durable job store is composed, so every submission is refused");
        jobsRunner.GetProperty("reasonCode").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
        jobsRunner.GetProperty("messageKey").GetString()
            .Should().Be($"capabilities.jobs.runner.{CapabilityUnavailableCodes.ErrorCode}");

        root.GetProperty("limits").GetProperty("job").GetProperty("durableJobRuntimeAvailable")
            .GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithoutDurableJobStore_ReportsDependencyReasonEvenForAnonymousCaller()
    {
        // A missing infrastructure dependency is a property of the deployment, not of who is
        // asking. An anonymous probe of a Redis-less install must not read `insufficient-policy`
        // (which would suggest the capability returns once you authenticate).
        using var response = await _anonymousClient.GetAsync("/api/v1/capabilities/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadDocumentAsync(response);

        var jobsRunner = GetCapability(document.RootElement, "jobs.runner");
        jobsRunner.GetProperty("available").GetBoolean().Should().BeFalse();
        jobsRunner.GetProperty("reasonCode").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithCompleteDurableJobSubstrate_ReportsJobsRunnerAvailable()
    {
        // Positive control: the degraded reading is caused by the absent substrate, not pinned on.
        // Re-adding BOTH halves (what configuring an entitled Redis does in production — the store
        // and the queue are gated on the same IConnectionMultiplexer) restores the available claim,
        // so the manifest tracks the deployment instead of a fixed answer.
        var fixture = CreateManifestFixture()
            .ConfigureServices(static services =>
            {
                services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
                services.AddSingleton<IJobQueue>(new InMemoryJobQueue());
            });
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = await ReadDocumentAsync(response);
            var root = document.RootElement;

            var jobsRunner = GetCapability(root, "jobs.runner");
            jobsRunner.GetProperty("available").GetBoolean().Should().BeTrue();
            jobsRunner.TryGetProperty("reasonCode", out var reasonCode).Should().BeFalse(
                "an available capability carries no reason code");
            _ = reasonCode;

            root.GetProperty("limits").GetProperty("job").GetProperty("durableJobRuntimeAvailable")
                .GetBoolean().Should().BeTrue();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithJobStoreButNoQueue_StillReportsJobsRunnerUnavailable()
    {
        // A job store without a runnable queue is the fabricated-availability trap: submissions
        // would be persisted, GeoprocessingJobDispatcher.MaybeEnqueueLocalAsync would silently skip
        // enqueueing, and nothing would ever drain. Store presence alone must therefore never
        // flip the manifest to available.
        var fixture = CreateManifestFixture()
            .ConfigureServices(static services =>
                services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore()));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = await ReadDocumentAsync(response);
            var root = document.RootElement;

            var jobsRunner = GetCapability(root, "jobs.runner");
            jobsRunner.GetProperty("available").GetBoolean().Should().BeFalse(
                "no queue is composed, so a submitted job could never drain");
            jobsRunner.GetProperty("reasonCode").GetString()
                .Should().Be(CapabilityUnavailableCodes.ErrorCode);
            root.GetProperty("limits").GetProperty("job").GetProperty("durableJobRuntimeAvailable")
                .GetBoolean().Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task GetManifest_WithRedisConfiguredButUnentitled_ReportsLicenseRequiredNotMissingDependency()
    {
        // honua-release#202 follow-up: Redis IS deployed, but the Pro `caching.redis` entitlement
        // is absent, so IConnectionMultiplexer — and the whole job substrate — was never
        // registered. Reporting `dependency-unavailable` here would send an operator to add a
        // Redis they are already running. The manifest must name the licence instead.
        var fixture = CreateManifestFixture()
            .ConfigureServices(static services =>
                services.Configure<DurableJobSubstrateOptions>(options =>
                {
                    options.RedisConfigured = true;
                    options.RedisEntitled = false;
                }));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.GetAsync("/api/v1/capabilities/manifest");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = await ReadDocumentAsync(response);

            var jobsRunner = GetCapability(document.RootElement, "jobs.runner");
            jobsRunner.GetProperty("available").GetBoolean().Should().BeFalse();
            jobsRunner.GetProperty("reasonCode").GetString().Should().Be("license-required");
            jobsRunner.GetProperty("reasonCode").GetString()
                .Should().NotBe(CapabilityUnavailableCodes.ErrorCode,
                    "an unentitled but present Redis is not a missing dependency");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static JsonElement GetCapability(JsonElement root, string capabilityId)
        => GetById(root, "capabilities", capabilityId);

    private static JsonElement GetTransport(JsonElement root, string transportId)
        => GetById(root.GetProperty("transports"), "items", transportId);

    private static JsonElement GetLink(JsonElement root, string rel)
        => root.GetProperty("links").EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("rel").GetString(), rel, StringComparison.Ordinal));

    private static JsonElement GetById(JsonElement root, string propertyName, string id)
        => root.GetProperty(propertyName).EnumerateArray().Single(item =>
            string.Equals(item.GetProperty("id").GetString(), id, StringComparison.Ordinal));

    private sealed class CapabilityManifestRoleStore : IRoleStore
    {
        private static readonly PermissionGrant[] EditorPermissions =
        [
            new() { Service = "*", Layer = "*", Operation = "write" }
        ];

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>(Array.Empty<RoleDefinition>());

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<RoleDefinition> CreateRoleAsync(
            RoleDefinition role,
            CancellationToken cancellationToken = default)
            => Task.FromResult(role);

        public Task<RoleDefinition?> UpdateRoleAsync(
            RoleDefinition role,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(role);

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(
            Guid roleId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>(Array.Empty<PermissionGrant>());

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
            Guid roleId,
            IReadOnlyList<PermissionGrant> permissions,
            CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var permissions = roles.Contains("editor", StringComparer.OrdinalIgnoreCase)
                ? EditorPermissions
                : Array.Empty<PermissionGrant>();
            var result = new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = permissions,
                ResolvedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(result);
        }
    }

    private sealed class CapabilityManifestTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "CapabilityManifestTest";
        public const string UserHeader = "X-Capability-Test-User";
        public const string RolesHeader = "X-Capability-Test-Roles";
        public const string WorkspaceClaimTypeHeader = "X-Capability-Test-Workspace-Claim-Type";
        public const string WorkspaceScopeHeader = "X-Capability-Test-Workspace-Scope";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userName = userValues.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userName))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userName),
                new(ClaimTypes.Name, userName)
            };
            AddRoles(claims);
            AddWorkspaceScope(claims);

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        private void AddRoles(List<Claim> claims)
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roleValues))
            {
                return;
            }

            var roles = roleValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("roles", role));
            }
        }

        private void AddWorkspaceScope(List<Claim> claims)
        {
            if (!Request.Headers.TryGetValue(WorkspaceScopeHeader, out var workspaceValues))
            {
                return;
            }

            var workspaceId = workspaceValues.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return;
            }

            var claimType = Request.Headers.TryGetValue(WorkspaceClaimTypeHeader, out var claimTypeValues)
                ? claimTypeValues.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(claimType))
            {
                claimType = new RbacOptions().WorkspaceScopeClaimType;
            }

            claims.Add(new Claim(claimType, workspaceId));
        }
    }

    private sealed class StaticEnvironmentSnapshotReader : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly MetadataV2GraphSnapshot _snapshot;

        public StaticEnvironmentSnapshotReader(string environment)
        {
            var graph = new TestMetadataV2GraphBuilder()
                .WithEnvironment(environment)
                .WithRevision(42)
                .Build();
            _snapshot = new MetadataV2GraphSnapshot(graph, "\"manifest-test\"", DateTimeOffset.UtcNow);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
            string environment,
            CancellationToken cancellationToken = default)
            => new(Matches(environment) ? _snapshot : null);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            string environment,
            long revision,
            CancellationToken cancellationToken = default)
            => new(Matches(environment) && revision == _snapshot.Revision ? _snapshot : null);

        public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
            IReadOnlyList<string> environments,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (!environments.Any(Matches))
            {
                yield break;
            }

            yield return new MetadataV2EnvironmentRevision
            {
                Environment = _snapshot.Graph.Environment,
                Revision = _snapshot.Revision,
                ETag = _snapshot.Etag,
                ActivatedAt = _snapshot.LoadedAt
            };
        }

        private bool Matches(string environment)
            => string.Equals(environment, _snapshot.Graph.Environment, StringComparison.OrdinalIgnoreCase);
    }
}
