// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Regression proof for honua-server#4905: in the shipped multi-tenancy posture
/// (<c>MultiTenancy:Enabled=true</c>, <c>SchemaRouting:Enabled=false</c>, so every tenant shares
/// one set of <c>studio_*</c> tables) a tenant-scoped administrator of one tenant could
/// enumerate, read and propose publication for another tenant's Studio content, because the
/// <c>admin</c> role bypassed the ownership check and nothing else bound the content to a tenant.
/// </summary>
/// <remarks>
/// <para>
/// The tokens are deliberately issuer-bearing and carry a <c>tid</c> claim, exactly as the
/// honua-sdk-js#1426 governed-lifecycle replay's did: that is what makes
/// <c>TenantContextMiddleware</c> resolve a real per-request tenant. <c>admin</c> is not one of
/// the default <c>MultiTenancy:MultiTenantAdminRoles</c>, so both principals are tenant-scoped
/// administrators and must stay inside their own tenant.
/// </para>
/// <para>
/// Nothing here injects an authorization decision: both callers are genuine bearer principals
/// authenticated by the running host, the generic operator gate is admitted so the assertions
/// isolate the tenant boundary rather than an operator-grant denial, and every assertion is an
/// HTTP/MCP response from the real Studio lifecycle endpoints over the durable Postgres store.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioTenantIsolationTests : IAsyncLifetime
{
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "studio-tenant-isolation-signing-key-at-least-32-characters";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new();
    private readonly RedisFixture _redis = new();
    private HttpClient _tenantA = null!;
    private HttpClient _tenantB = null!;

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                // Authentication must be decided by the presented credential only: the dev-auth
                // bypass would hand both tenants the same identity and the same tenant.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");

                // Symmetric-key generic OIDC provider so issuer-bearing, tenant-tagged tokens are
                // minted and validated in-process without a live IdP.
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", Issuer);
                builder.UseSetting("Oidc:Generic:ClientId", Audience);
                builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
            })
            .ConfigureServices(services =>
            {
                // Test hosts skip application migrations, so the durable studio_* tables do not
                // exist here; the in-memory store is the same substitution the other Studio
                // endpoint suites make. The durable store's tenant column, its enumeration
                // filter and migration 120 are covered directly by
                // Honua.Db.Postgres.Tests' PostgresStudioPackageStoreTenantTests.
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IContentPublicationStore>();
                services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
                // Admit the generic operator gate so a denial can only come from the tenant
                // boundary under test, never from a missing StudioDraft grant.
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
            });
        await _fixture.InitializeAsync();
        _tenantA = CreateBearerClient(CreateToken("studio-alice", TenantA));
        _tenantB = CreateBearerClient(CreateToken("studio-bob", TenantB));
    }

    public async Task DisposeAsync()
    {
        _tenantA?.Dispose();
        _tenantB?.Dispose();
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items")]
    [Endpoint("GET /api/v1/studio/package-drafts")]
    public async Task StudioListings_AnotherTenantsContent_IsNotEnumerable()
    {
        var seeded = await SeedTenantAContentAsync();

        var ownItems = await ListContentItemIdsAsync(_tenantA);
        ownItems.Should().Contain(seeded.ItemId, "the owning tenant still enumerates its own content");
        var ownDrafts = await ListDraftIdsAsync(_tenantA);
        ownDrafts.Should().Contain(seeded.DraftId);

        var foreignItems = await ListContentItemIdsAsync(_tenantB);
        foreignItems.Should().NotContain(seeded.ItemId, "a tenant-scoped admin must not enumerate another tenant's content items");
        var foreignDrafts = await ListDraftIdsAsync(_tenantB);
        foreignDrafts.Should().NotContain(seeded.DraftId, "a tenant-scoped admin must not enumerate another tenant's drafts");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    public async Task StudioReads_AnotherTenantsDraftAndVersion_AreNotFound()
    {
        var seeded = await SeedTenantAContentAsync();

        // Control: the owning tenant reads both.
        (await _tenantA.GetAsync($"/api/v1/studio/package-drafts/{seeded.DraftId:D}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _tenantA.GetAsync($"/api/v1/studio/content-items/{seeded.ItemId:D}/versions/{seeded.VersionId:D}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertCrossTenantNotFoundAsync(
            _tenantB.GetAsync($"/api/v1/studio/package-drafts/{seeded.DraftId:D}"));
        await AssertCrossTenantNotFoundAsync(
            _tenantB.GetAsync($"/api/v1/studio/content-items/{seeded.ItemId:D}/versions/{seeded.VersionId:D}"));
        await AssertCrossTenantNotFoundAsync(
            _tenantB.GetAsync($"/api/v1/studio/content-items/{seeded.ItemId:D}/versions"));
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("DELETE /api/v1/studio/package-drafts/{draftId}")]
    public async Task StudioDraftMutations_AnotherTenantsDraft_AreRefusedAndLeaveTheDraftIntact()
    {
        var seeded = await SeedTenantAContentAsync();

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/studio/package-drafts/{seeded.DraftId:D}")
        {
            Content = JsonContent(
                new UpdateStudioPackageDraftRequest
                {
                    PackageKey = seeded.PackageKey,
                    WorkspaceId = "studio",
                    Generation = seeded.Generation,
                    Envelope = BuildEnvelope("1=0"),
                },
                StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest),
        };
        await AssertCrossTenantNotFoundAsync(_tenantB.SendAsync(update));
        await AssertCrossTenantNotFoundAsync(
            _tenantB.DeleteAsync($"/api/v1/studio/package-drafts/{seeded.DraftId:D}"));

        var stillThere = await _tenantA.GetAsync($"/api/v1/studio/package-drafts/{seeded.DraftId:D}");
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK, "a refused cross-tenant mutation must not have changed the draft");
        var draft = await ReadAsync<StudioPackageDraft>(stillThere, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.Generation.Should().Be(seeded.Generation, "the refused update must not have advanced the generation");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task StudioPublishRequest_AnotherTenantsVersion_IsRefusedAndDoesNotPublish()
    {
        var seeded = await SeedTenantAContentAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/studio/content-items/{seeded.ItemId:D}/versions/{seeded.VersionId:D}/publish-requests")
        {
            Content = JsonContent(
                new CreateStudioPublicationRequest
                {
                    Intent = new StudioPublicationIntent
                    {
                        Route = $"/studio/{seeded.PackageKey}",
                        Visibility = "organization",
                    },
                },
                StudioApiJsonContext.Default.CreateStudioPublicationRequest),
        };
        await AssertCrossTenantNotFoundAsync(_tenantB.SendAsync(request));

        var versionResponse = await _tenantA.GetAsync(
            $"/api/v1/studio/content-items/{seeded.ItemId:D}/versions/{seeded.VersionId:D}");
        versionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ListContentItemsAsync(_tenantA);
        var item = items.Should().ContainSingle(row => row.ItemId == seeded.ItemId).Subject;
        item.PublishedVersionId.Should().BeNull("a refused cross-tenant proposal must not advance the published pointer");
    }

    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task McpProposePublication_AnotherTenantsItem_IsRefused()
    {
        var seeded = await SeedTenantAContentAsync();

        var result = await CallToolAsync(
            _tenantB,
            "honua_studio_propose_publication",
            $$"""{"itemId":"{{seeded.ItemId:D}}","versionId":"{{seeded.VersionId:D}}","contentHash":"{{seeded.ContentHash}}","route":"/studio/{{seeded.PackageKey}}","visibility":"organization"}""");

        result.TryGetProperty("isError", out var isError).Should().BeTrue("the tool must report a refusal");
        isError.GetBoolean().Should().BeTrue(result.GetRawText());

        var items = await ListContentItemsAsync(_tenantA);
        items.Should().ContainSingle(row => row.ItemId == seeded.ItemId)
            .Which.PublishedVersionId.Should().BeNull(
                "a refused cross-tenant proposal must not advance the published pointer");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task StudioLifecycle_WithinOneTenant_IsUnchanged()
    {
        var seeded = await SeedTenantAContentAsync();

        // The same principal that authored the content completes the governed flow end to end:
        // the tenant boundary only ever refuses a foreign tenant.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/studio/content-items/{seeded.ItemId:D}/versions/{seeded.VersionId:D}/publish-requests")
        {
            Content = JsonContent(
                new CreateStudioPublicationRequest
                {
                    Intent = new StudioPublicationIntent
                    {
                        Route = $"/studio/{seeded.PackageKey}",
                        Visibility = "organization",
                    },
                },
                StudioApiJsonContext.Default.CreateStudioPublicationRequest),
        };
        var publishResponse = await _tenantA.SendAsync(request);
        publishResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await publishResponse.Content.ReadAsStringAsync());

        var items = await ListContentItemsAsync(_tenantA);
        items.Should().ContainSingle(row => row.ItemId == seeded.ItemId)
            .Which.PublishedVersionId.Should().Be(seeded.VersionId);
    }

    /// <summary>
    /// Creates a draft and saves it as an immutable version as tenant A, through the real REST
    /// lifecycle endpoints, so the content records tenant A exactly as production would.
    /// </summary>
    private async Task<SeededContent> SeedTenantAContentAsync()
    {
        var packageKey = $"tenant-isolation-{Guid.NewGuid():N}";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/studio/package-drafts")
        {
            Content = JsonContent(
                new CreateStudioPackageDraftRequest
                {
                    PackageKey = packageKey,
                    WorkspaceId = "studio",
                    Envelope = BuildEnvelope("1=1"),
                },
                StudioApiJsonContext.Default.CreateStudioPackageDraftRequest),
        };
        var createResponse = await _tenantA.SendAsync(create);
        createResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await createResponse.Content.ReadAsStringAsync());
        var draft = await ReadAsync<StudioPackageDraft>(
            createResponse,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        using var save = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions")
        {
            Content = new StringContent("{}", Encoding.UTF8, JsonMediaType),
        };
        var saveResponse = await _tenantA.SendAsync(save);
        saveResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            await saveResponse.Content.ReadAsStringAsync());
        var version = await ReadAsync<StudioContentVersion>(
            saveResponse,
            StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        // The save advances the draft generation, so re-read it for the concurrency token the
        // cross-tenant update attempt has to present.
        var refreshed = await _tenantA.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}");
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await ReadAsync<StudioPackageDraft>(
            refreshed,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        return new SeededContent(
            draft.DraftId,
            version.ItemId,
            version.VersionId,
            version.ContentHash,
            packageKey,
            current.Generation);
    }

    /// <summary>
    /// Asserts the honua-server#4905 refusal shape: <c>404 Not Found</c> (never <c>403</c>, which
    /// would confirm that another tenant's id exists) carrying the machine-readable
    /// <c>studio_authorization/cross_tenant_denied</c> code.
    /// </summary>
    private static async Task AssertCrossTenantNotFoundAsync(Task<HttpResponseMessage> pending)
    {
        using var response = await pending.ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("code", out var code).Should().BeTrue(payload);
        code.GetString().Should().Be(StudioAuthorizationService.CrossTenantDeniedCode, payload);
    }

    private static async Task<IReadOnlyList<StudioContentItemListRow>> ListContentItemsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/studio/content-items?limit=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var listing = await ReadAsync<StudioContentItemListResponse>(
            response,
            StudioApiJsonContext.Default.ApiResponseStudioContentItemListResponse);
        return listing.Items;
    }

    private static async Task<IReadOnlyList<Guid>> ListContentItemIdsAsync(HttpClient client)
        => (await ListContentItemsAsync(client)).Select(static row => row.ItemId).ToArray();

    private static async Task<IReadOnlyList<Guid>> ListDraftIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/studio/package-drafts?limit=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var listing = await ReadAsync<StudioPackageDraftListResponse>(
            response,
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraftListResponse);
        return listing.Items.Select(static draft => draft.DraftId).ToArray();
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string toolName, string argumentsJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                $$$"""{"jsonrpc":"2.0","id":"{{{toolName}}}","method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argumentsJson}}}}}""",
                Encoding.UTF8)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) },
            },
        };
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out var protocolError).Should().BeFalse(
            protocolError.ValueKind == JsonValueKind.Undefined ? payload : protocolError.GetRawText());
        return document.RootElement.GetProperty("result").Clone();
    }

    private HttpClient CreateBearerClient(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent JsonContent<T>(T body, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, JsonMediaType);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        var json = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize(json, typeInfo);
        envelope.Should().NotBeNull(json);
        envelope!.Success.Should().BeTrue(json);
        envelope.Data.Should().NotBeNull(json);
        return envelope.Data!;
    }

    private static StudioPackageEnvelope BuildEnvelope(string where)
    {
        using var body = JsonDocument.Parse($$"""{"where":"{{where}}"}""");
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Bindings =
            [
                new StudioPackageBinding
                {
                    Key = "source",
                    Kind = "content",
                    Ref = "content.parcels",
                    Crs = "EPSG:4326",
                    Srid = 4326,
                    RequiredPermissions = ["metadata.read"],
                },
            ],
            Body = body.RootElement.Clone(),
        };
    }

    private static string CreateToken(string subject, string tenantId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("tid", tenantId),
                new Claim("roles", "admin"),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record SeededContent(
        Guid DraftId,
        Guid ItemId,
        Guid VersionId,
        string ContentHash,
        string PackageKey,
        long Generation);

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("tenant-isolation fixture"));
    }
}
