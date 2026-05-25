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
/// API integration tests for the public published-route read endpoint: anonymous
/// visibility enforcement, public-link authorization, embed policy, and generated-app
/// reopen-by-revision after the active route has moved.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Query)]
public sealed class PublishedRouteEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _anonymousClient = null!;

    public PublishedRouteEndpointsTests()
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
        _adminClient = _fixture.CreateAdminClient();
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task PublicVisibility_AnonymousRead_ReturnsClientSafeView()
    {
        var detail = await PublishAsync("public-map", ContentPublicationKind.Map);
        await SetVisibilityPublicAsync(detail.Route.PublicationId);

        var response = await _anonymousClient.GetAsync("/api/v1/published/public-map");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await DeserializeAsync(response, ContentPublicationJsonContext.Default.PublishedArtifactView);
        view!.RouteSlug.Should().Be("public-map");
        view.Kind.Should().Be(ContentPublicationKind.Map);
        view.Visibility.Should().Be(ContentPublicationVisibility.Public);
        view.Revision.Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task PrivateVisibility_AnonymousRead_ReturnsUnauthorized()
    {
        await PublishAsync("private-map", ContentPublicationKind.Map);

        var response = await _anonymousClient.GetAsync("/api/v1/published/private-map");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task UnknownSlug_ReturnsNotFound()
    {
        var response = await _anonymousClient.GetAsync("/api/v1/published/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task PublicLink_AuthorizesAnonymousRead_AndDeniesWrongToken()
    {
        var detail = await PublishAsync("linked-map", ContentPublicationKind.Map);
        var linkResult = await UpdatePolicyAsync(detail.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            CreatePublicLink = new ContentPublicLinkRequest { Label = "share", Token = "link-token" },
        });
        var linkId = linkResult.CreatedPublicLinkId;

        var authorized = await _anonymousClient.GetAsync($"/api/v1/published/linked-map?link={linkId}&token=link-token");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);

        var wrongToken = await _anonymousClient.GetAsync($"/api/v1/published/linked-map?link={linkId}&token=wrong");
        wrongToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task Embed_DeniedWhenNotAllowedOrOriginMismatched_AndAllowedForAllowedOrigin()
    {
        var detail = await PublishAsync("embed-map", ContentPublicationKind.Map);
        await SetVisibilityPublicAsync(detail.Route.PublicationId);

        var denied = await _anonymousClient.GetAsync("/api/v1/published/embed-map?embed=true");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await UpdatePolicyAsync(detail.Route.PublicationId, new UpdatePublicationPolicyRequest
        {
            Embed = new ContentEmbedPolicy { AllowEmbedding = true, AllowedOrigins = ["https://app.example"], FrameAncestors = ["https://app.example"] },
        });

        using var wrongOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/published/embed-map?embed=true");
        wrongOrigin.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        var originDenied = await _anonymousClient.SendAsync(wrongOrigin);
        originDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var allowedOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/published/embed-map?embed=true");
        allowedOrigin.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        var allowed = await _anonymousClient.SendAsync(allowedOrigin);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/published/{*routeSlug}")]
    public async Task GeneratedApp_ReopenOlderRevision_AfterRouteMoved_ReturnsThatRevision()
    {
        var detail = await PublishAsync("demo-app", ContentPublicationKind.GeneratedApp, manifestId: "manifest-v1", bundleId: "bundle-v1");
        await SetVisibilityPublicAsync(detail.Route.PublicationId);

        // Republish to v2 with a new manifest; the active route now points at v2.
        var republishBody = JsonSerializer.Serialize(
            new RepublishContentRequest { AppManifestId = "manifest-v2", AppBundleArtifactId = "bundle-v2" },
            ContentPublicationJsonContext.Default.RepublishContentRequest);
        var republish = await _adminClient.PostAsync($"/api/v1/console/publications/{detail.Route.PublicationId}/republish", JsonContent(republishBody));
        republish.StatusCode.Should().Be(HttpStatusCode.OK);

        // Active read sees v2.
        var active = await DeserializeAsync(
            await _anonymousClient.GetAsync("/api/v1/published/demo-app"),
            ContentPublicationJsonContext.Default.PublishedArtifactView);
        active!.Revision.Should().Be(2);
        active.AppManifestId.Should().Be("manifest-v2");

        // Reopen-by-revision still resolves the immutable v1 manifest.
        var preview = await _anonymousClient.GetAsync("/api/v1/published/demo-app?version=1");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewView = await DeserializeAsync(preview, ContentPublicationJsonContext.Default.PublishedArtifactView);
        previewView!.Revision.Should().Be(1);
        previewView.AppManifestId.Should().Be("manifest-v1");
    }

    private async Task<ContentPublicationDetail> PublishAsync(
        string slug, ContentPublicationKind kind, string? manifestId = null, string? bundleId = null)
    {
        var body = JsonSerializer.Serialize(
            new PublishContentRequest { Kind = kind, RouteSlug = slug, Title = slug, AppManifestId = manifestId, AppBundleArtifactId = bundleId },
            ContentPublicationJsonContext.Default.PublishContentRequest);
        var response = await _adminClient.PostAsync("/api/v1/console/publications", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationDetail))!;
    }

    private async Task SetVisibilityPublicAsync(string publicationId)
        => await UpdatePolicyAsync(publicationId, new UpdatePublicationPolicyRequest { Visibility = ContentPublicationVisibility.Public });

    private async Task<ContentPublicationPolicyUpdateResponse> UpdatePolicyAsync(string publicationId, UpdatePublicationPolicyRequest request)
    {
        var body = JsonSerializer.Serialize(request, ContentPublicationJsonContext.Default.UpdatePublicationPolicyRequest);
        var response = await _adminClient.PatchAsync($"/api/v1/console/publications/{publicationId}/policy", JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await DeserializeAsync(response, ContentPublicationJsonContext.Default.ContentPublicationPolicyUpdateResponse))!;
    }

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(payload, typeInfo);
    }
}
