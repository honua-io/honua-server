// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Console.Publications;

/// <summary>
/// API integration tests for the authenticated content publication endpoints. Uses an
/// in-memory store override so the HTTP + service path is exercised without a migrated
/// Postgres schema (the Postgres store has dedicated integration coverage).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ContentPublicationEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ContentPublicationEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IContentPublicationStore>();
                services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    [Endpoint("GET /api/v1/console/publications/{publicationId}")]
    public async Task Publish_ThenGet_ReturnsDurableImmutableVersionAndActiveRoute()
    {
        var detail = await PublishAsync("Quarterly Map", ContentPublicationKind.Map, payload: "content-v1");

        detail.Route.RouteSlug.Should().Be("quarterly-map");
        detail.Route.ActiveRevision.Should().Be(1);
        detail.Versions.Should().ContainSingle();

        var get = await _client.GetAsync($"/api/v1/console/publications/{detail.Route.PublicationId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await DeserializeAsync(get, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        fetched!.Route.ActiveVersionId.Should().Be(detail.Route.ActiveVersionId);
        fetched.Versions[0].ContentHash.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_DuplicateSlug_ReturnsConflictWithoutLeakingInternals()
    {
        await PublishAsync("Conflict Map", ContentPublicationKind.Map);

        var body = SerializePublish(new PublishContentRequest { Kind = ContentPublicationKind.Dashboard, RouteSlug = "Conflict Map" });
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("already claimed");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications")]
    public async Task Publish_WithEmptyBody_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent("null"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/publications/{publicationId}")]
    public async Task Get_WithMalformedPublicationId_ReturnsBadRequestWithoutLeakingInternals()
    {
        var response = await _client.GetAsync("/api/v1/console/publications/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("publicationId must be a valid GUID");
        AssertNoSensitiveLeak(payload);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/publications/{publicationId}/versions/{versionSelector}")]
    public async Task GetVersion_ByRevisionAndVersionId_ReturnsImmutableVersion_AndRejectsBadSelector()
    {
        var detail = await PublishAsync("Versioned Map", ContentPublicationKind.Map);
        var publicationId = detail.Route.PublicationId;
        var versionId = detail.Route.ActiveVersionId;

        var byRevision = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/1");
        byRevision.StatusCode.Should().Be(HttpStatusCode.OK);

        var byId = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/{versionId}");
        byId.StatusCode.Should().Be(HttpStatusCode.OK);

        var bad = await _client.GetAsync($"/api/v1/console/publications/{publicationId}/versions/not-a-version");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/publications/{publicationId}/republish")]
    [Endpoint("POST /api/v1/console/publications/{publicationId}/rollback")]
    public async Task RepublishThenRollback_MovesRoutePointerAndKeepsHistory()
    {
        var detail = await PublishAsync("Lifecycle Map", ContentPublicationKind.Map, payload: "v1");
        var publicationId = detail.Route.PublicationId;
        var v1Id = detail.Route.ActiveVersionId;

        var republishBody = JsonSerializer.Serialize(
            new RepublishContentRequest { ContentPayload = "v2" },
            ContentPublicationJsonContext.Default.RepublishContentRequest);
        var republish = await _client.PostAsync($"/api/v1/console/publications/{publicationId}/republish", JsonContent(republishBody));
        republish.StatusCode.Should().Be(HttpStatusCode.OK);
        var republished = await DeserializeAsync(republish, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        republished!.Route.ActiveRevision.Should().Be(2);

        var rollbackBody = JsonSerializer.Serialize(
            new RollbackContentRequest { TargetRevision = 1 },
            ContentPublicationJsonContext.Default.RollbackContentRequest);
        var rollback = await _client.PostAsync($"/api/v1/console/publications/{publicationId}/rollback", JsonContent(rollbackBody));
        rollback.StatusCode.Should().Be(HttpStatusCode.OK);
        var rolledBack = await DeserializeAsync(rollback, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        rolledBack!.Route.ActiveRevision.Should().Be(1);
        rolledBack.Route.ActiveVersionId.Should().Be(v1Id);
        rolledBack.Route.RollbackTargetVersionId.Should().Be(v1Id);
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/publications/{publicationId}/policy")]
    public async Task UpdatePolicy_CreatesPublicLink_ReturnsTokenOnce()
    {
        var detail = await PublishAsync("Policy Map", ContentPublicationKind.Map);
        var publicationId = detail.Route.PublicationId;

        var body = JsonSerializer.Serialize(
            new UpdatePublicationPolicyRequest
            {
                Visibility = ContentPublicationVisibility.Public,
                CreatePublicLink = new ContentPublicLinkRequest { Label = "share", Token = "raw-secret-token" },
            },
            ContentPublicationJsonContext.Default.UpdatePublicationPolicyRequest);

        var response = await _client.PatchAsync($"/api/v1/console/publications/{publicationId}/policy", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationPolicyUpdateResponse);
        result!.CreatedPublicLinkToken.Should().Be("raw-secret-token");
        result.CreatedPublicLinkId.Should().NotBeNullOrEmpty();
        result.Route.Policy.Visibility.Should().Be(ContentPublicationVisibility.Public);
        // The stored link carries only a hash, never the raw token.
        var link = result.Route.Policy.PublicLink.Links.Should().ContainSingle().Subject;
        link.TokenHash.Should().NotBeNullOrEmpty();
        link.TokenHash.Should().NotContain("raw-secret-token");
    }

    private async Task<ContentPublicationDetail> PublishAsync(string slug, ContentPublicationKind kind, string? payload = null)
    {
        var body = SerializePublish(new PublishContentRequest { Kind = kind, RouteSlug = slug, Title = slug, ContentPayload = payload });
        var response = await _client.PostAsync("/api/v1/console/publications", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var detail = await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationDetail);
        detail.Should().NotBeNull();
        return detail!;
    }

    private static string SerializePublish(PublishContentRequest request)
        => JsonSerializer.Serialize(request, ContentPublicationJsonContext.Default.PublishContentRequest);

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    private static void AssertNoSensitiveLeak(string payload)
    {
        payload.Should().NotContain("Npgsql");
        payload.Should().NotContain("System.");
        payload.Should().NotContain("ConnectionString");
        payload.Should().NotContain("content_publication_routes");
        payload.Should().NotContain(" at Honua.");
    }
}
