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
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// honua-server#5733: on a Development host with no Redis, Studio drafts compose against the
/// volatile operation store, but the governed proposal control plane is not composed. A
/// publication request that needs separate-principal approval must refuse with the same typed
/// capability-unavailable receipt (<c>missingDependency=redis</c>,
/// <c>capability=operations.proposals</c>) as the rest of the Redis-off surface, on both the
/// Studio REST route and <c>honua_studio_propose_publication</c>, and must leave the draft and
/// the published pointer untouched. Before the fix the REST route returned an untyped 500.
/// Licensing is disabled, the 2026.1 posture in which publication waits for approval.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioPublishRequestRedisOffTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "studio-redis-off-publication-signing-key-32-chars";
    private const string Tenant = "tenant-a";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture
            .ConfigureWebHost(builder =>
            {
                // Deliberately no ConnectionStrings:redis.
                builder.UseEnvironment("Development");
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
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
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
            });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/validate")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/preview-plan")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task PublishRequest_RedisOffDevelopmentHost_RefusesWithTypedRedisDependencyAndLeavesDraftUnchanged()
    {
        using var client = _fixture.CreateAdminClient();

        var createResponse = await PostAsync(
            client,
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"redis-off-{Guid.NewGuid():N}",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope(),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var draft = await ReadAsync(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        (await PostEmptyJsonAsync(client, $"/api/v1/studio/package-drafts/{draft.DraftId:D}/validate"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostEmptyJsonAsync(client, $"/api/v1/studio/package-drafts/{draft.DraftId:D}/preview-plan"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saveResponse = await PostAsync(
            client,
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "redis-off save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created, await saveResponse.Content.ReadAsStringAsync());
        var version = await ReadAsync(saveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var before = await GetDraftAsync(client, draft.DraftId);

        var publishResponse = await PostAsync(
            client,
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest { WarningAcknowledgement = "reviewed" },
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);

        var body = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        publishResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using (var problem = JsonDocument.Parse(body))
        {
            var root = problem.RootElement;
            root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
            root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
            root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
            root.GetProperty("capability").GetString()
                .Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
            root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
        }

        var after = await GetDraftAsync(client, draft.DraftId);
        after.Should().BeEquivalentTo(
            before,
            options => options.Excluding(draft => draft.Envelope.Body),
            "a refused publication request must not mutate the draft");
        (after.Envelope.Body?.GetRawText()).Should().Be(before.Envelope.Body?.GetRawText());
        (await GetPublishedVersionIdAsync(version.ItemId)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task ProposePublication_RedisOffDevelopmentHost_ReturnsTypedUnavailableToolError()
    {
        using var caller = _fixture.CreateClient();
        caller.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("studio-member-without-admin"));
        var packageKey = $"redis-off-mcp-{Guid.NewGuid():N}";

        var created = await CallToolAsync(caller, "honua_studio_create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"dashboard","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();
        var updated = await CallToolAsync(caller, "honua_studio_update_draft",
            $$$"""{"body":{"layers":[{"id":"roads"}],"view":{"center":[-158,22],"zoom":7}},"draftId":"{{{draftId:D}}}","generation":1,"packageKey":"{{{packageKey}}}","schemaVersion":"1.0"}""");
        var generation = updated.GetProperty("generation").GetInt64();
        await CallToolAsync(caller, "honua_studio_validate_draft", $$"""{"draftId":"{{draftId:D}}"}""");
        var saved = await CallToolAsync(caller, "honua_studio_save_version",
            $$"""{"draftId":"{{draftId:D}}","generation":{{generation}}}""");
        var version = saved.GetProperty("version");
        var itemId = version.GetProperty("itemId").GetGuid();

        var result = await CallToolRawAsync(caller, "honua_studio_propose_publication",
            $$"""{"itemId":"{{itemId:D}}","versionId":"{{version.GetProperty("versionId").GetGuid():D}}","contentHash":"{{version.GetProperty("contentHash").GetString()}}","route":"/maps/redis-off-{{Guid.NewGuid():N}}","visibility":"public"}""");

        result.GetProperty("isError").GetBoolean().Should().BeTrue(result.GetRawText());
        var error = result.GetProperty("structuredContent");
        error.GetProperty("code").GetString().Should().Be("unavailable");
        error.GetProperty("retryable").GetBoolean().Should().BeFalse();
        error.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
        error.GetProperty("capability").GetString()
            .Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);

        (await GetPublishedVersionIdAsync(itemId)).Should().BeNull();
    }

    private async Task<Guid?> GetPublishedVersionIdAsync(Guid itemId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        return (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId;
    }

    private static async Task<StudioPackageDraft> GetDraftAsync(HttpClient client, Guid draftId)
    {
        var response = await client.GetAsync($"/api/v1/studio/package-drafts/{draftId:D}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync(response, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, JsonTypeInfo<T> typeInfo)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, JsonMediaType);
        return await client.PostAsync(path, content);
    }

    private static async Task<HttpResponseMessage> PostEmptyJsonAsync(HttpClient client, string path)
    {
        using var content = new StringContent("{}", Encoding.UTF8, JsonMediaType);
        return await client.PostAsync(path, content);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        var envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), typeInfo);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        return envelope.Data!;
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var result = await CallToolRawAsync(client, toolName, argumentsJson);
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse($"tool '{toolName}' failed: {result.GetRawText()}");
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<JsonElement> CallToolRawAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var body = $$$"""{"jsonrpc":"2.0","id":"{{{toolName}}}","method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argumentsJson}}}}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        root.TryGetProperty("error", out var protocolError).Should().BeFalse(
            $"tool '{toolName}' should return a tool result: {payload}");
        return root.GetProperty("result").Clone();
    }

    private static string CreateToken(string subject)
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
                new Claim("tid", Tenant),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static StudioPackageEnvelope BuildEnvelope()
    {
        using var body = JsonDocument.Parse("""{"where":"1=1"}""");
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
            Dependencies =
            [
                new StudioPackageDependency { Kind = "content-item", Ref = "content.parcels", VersionId = "v1" },
            ],
            Provenance =
            [
                new StudioProvenanceRef { Kind = "prompt", Ref = "prompt-1", Rel = "generated-by" },
            ],
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/redis-off", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("redis-off publication fixture"));
    }
}
