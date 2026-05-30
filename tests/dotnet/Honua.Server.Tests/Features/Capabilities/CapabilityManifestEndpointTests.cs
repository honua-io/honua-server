// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    private static WebAppFixture CreateManifestFixture(string[]? entitlements = null)
        => new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro, entitlements: entitlements)
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
                        ["Grpc:StreamBatchSize"] = "42"
                    });
                });
            });

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
        root.GetProperty("policies").GetProperty("callerCapabilities").EnumerateArray().Should().BeEmpty();
        GetLink(root, "feature-streaming-capabilities").GetProperty("href").GetString()
            .Should().Be("/api/v1/streaming/features/capabilities");
        GetTransport(root, "mcp").GetProperty("available").GetBoolean().Should().BeTrue();
        GetTransport(root, "qgis").GetProperty("available").GetBoolean().Should().BeTrue();
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

        GetCapability(root, "sync.offline").GetProperty("category").GetString().Should().Be("sync");
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
