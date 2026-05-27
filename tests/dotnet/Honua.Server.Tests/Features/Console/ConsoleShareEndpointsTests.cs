// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Integration tests for the Console Share access public-link and embed API (#1215).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class ConsoleShareEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "console-share-admin-key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: true) },
    };

    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _anonymousClient = null!;

    public ConsoleShareEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ReplaceService<TimeProvider>(_clock)
            .ReplaceService<IConsoleShareStore>(new InMemoryConsoleShareStore(_clock));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/share")]
    public async Task GetShare_ForPersonalItem_ReturnsPrivateProjectionWithCallerPermissions()
    {
        var item = await CreateItemAsync("share-private", ConsoleVisibility.Personal, title: "Private share item");

        var response = await _adminClient.GetAsync($"/api/v1/console/content/{item.Id}/share");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var projection = await ReadDataAsync<ConsoleShareProjection>(response);
        Assert.Equal(item.Id, projection.ItemId);
        Assert.Equal(ConsoleShareAccessTier.Private, projection.AccessTier);
        Assert.False(projection.PublicLinkEnabled);
        Assert.Empty(projection.PublicLinkTokens);
        Assert.False(projection.EmbedEnabled);
        Assert.False(projection.AnonymousEligible);
        Assert.Contains(ConsoleContentAction.Share, projection.CallerPermissions);
        Assert.Contains(ConsoleContentAction.Embed, projection.CallerPermissions);
        Assert.Contains(ConsoleContentAction.Administer, projection.CallerPermissions);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    [Endpoint("GET /api/v1/console/share/content/{id}")]
    public async Task UpdateAccess_PublicIndexed_UpdatesVisibilityAndAllowsAnonymousPublicRead()
    {
        var item = await CreateItemAsync("share-public", ConsoleVisibility.Organization, title: "Public share item");

        var updateResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/access",
            new UpdateShareAccessRequest { AccessTier = ConsoleShareAccessTier.PublicIndexed },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var projection = await ReadDataAsync<ConsoleShareProjection>(updateResponse);
        Assert.Equal(ConsoleShareAccessTier.PublicIndexed, projection.AccessTier);
        Assert.True(projection.OpenDataEligible);
        Assert.True(projection.AnonymousEligible);

        var contentResponse = await _adminClient.GetAsync($"/api/v1/console/content/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        var content = await ReadDataAsync<ConsoleContentItem>(contentResponse);
        Assert.Equal(ConsoleVisibility.Public, content.Visibility);

        var publicResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/content/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicProjection = await ReadDataAsync<ConsolePublicLinkResolutionResponse>(publicResponse);
        Assert.Equal(item.Id, publicProjection.ItemId);
        Assert.Equal(ConsoleShareAccessTier.PublicIndexed, publicProjection.AccessTier);
        Assert.Equal("Public share item", publicProjection.Title);

        var privateItem = await CreateItemAsync("share-private-denial", ConsoleVisibility.Personal, title: "Do Not Leak Me");
        var privateResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/content/{privateItem.Id}");
        await AssertNonLeakingNotFoundAsync(privateResponse, privateItem.Id, privateItem.Title);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/share/dependencies")]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    [Endpoint("PUT /api/v1/console/content/{id}/share/embed")]
    public async Task PublicShareAndEmbed_WithPrivateDependency_ReturnClosureConflictUnlessBypassed()
    {
        var dependency = await CreateItemAsync("private-source", ConsoleVisibility.Personal);
        var root = await CreateItemAsync(
            "public-root",
            ConsoleVisibility.Organization,
            provenance: [new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = dependency.Id, Rel = "derived-from" }]);

        var previewResponse = await _adminClient.GetAsync($"/api/v1/console/content/{root.Id}/share/dependencies?targetTier=public-link");
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await ReadDataAsync<ConsoleShareDependencyClosureResponse>(previewResponse);
        Assert.False(preview.IsCompatible);
        Assert.Contains(preview.Conflicts, conflict => conflict.ItemId == dependency.Id);

        var blockedAccessResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{root.Id}/share/access",
            new UpdateShareAccessRequest { AccessTier = ConsoleShareAccessTier.PublicLink },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, blockedAccessResponse.StatusCode);
        var blockedAccess = await ReadFailureDataAsync<ConsoleShareDependencyClosureResponse>(blockedAccessResponse);
        Assert.Contains(blockedAccess.Conflicts, conflict => conflict.ItemId == dependency.Id);

        var blockedEmbedResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{root.Id}/share/embed",
            new SetEmbedRequest { Enabled = true, Audience = ConsoleEmbedAudience.Map },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, blockedEmbedResponse.StatusCode);
        var blockedEmbed = await ReadFailureDataAsync<ConsoleShareDependencyClosureResponse>(blockedEmbedResponse);
        Assert.Contains(blockedEmbed.Conflicts, conflict => conflict.ItemId == dependency.Id);

        var bypassResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{root.Id}/share/access",
            new UpdateShareAccessRequest
            {
                AccessTier = ConsoleShareAccessTier.PublicLink,
                AllowDependencyConflicts = true,
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, bypassResponse.StatusCode);
        var projection = await ReadDataAsync<ConsoleShareProjection>(bypassResponse);
        Assert.Equal(ConsoleShareAccessTier.PublicLink, projection.AccessTier);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content/{id}/share/link")]
    [Endpoint("GET /api/v1/console/content/{id}/share/link")]
    [Endpoint("GET /api/v1/console/share/link/{token}")]
    [Endpoint("DELETE /api/v1/console/content/{id}/share/link/{tokenId}")]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    public async Task PublicLinkLifecycle_ResolvesAndDeniesExpiredInvalidRevokedAndPrivateWithoutTitleLeak()
    {
        var item = await CreateItemAsync("share-link", ConsoleVisibility.Personal, title: "Public Link Leak Sentinel");
        await SetAccessTierAsync(item.Id, ConsoleShareAccessTier.PublicLink);

        var expiringToken = await MintPublicLinkAsync(item.Id, _clock.GetUtcNow().AddMinutes(5));

        var listResponse = await _adminClient.GetAsync($"/api/v1/console/content/{item.Id}/share/link");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await ReadDataAsync<ConsolePublicLinkListResponse>(listResponse);
        var listedToken = Assert.Single(list.Tokens);
        Assert.Equal(expiringToken.TokenId, listedToken.TokenId);
        Assert.Null(listedToken.Token);
        Assert.False(listedToken.IsExpired);

        var validResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/link/{expiringToken.Token}");
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        var resolved = await ReadDataAsync<ConsolePublicLinkResolutionResponse>(validResponse);
        Assert.Equal(item.Id, resolved.ItemId);
        Assert.Equal(ConsoleShareAccessTier.PublicLink, resolved.AccessTier);
        Assert.Equal("Public Link Leak Sentinel", resolved.Title);

        var expiredToken = await _fixture.GetService<IConsoleShareStore>()
            .MintPublicLinkAsync(item.Id, ConsoleShareAccessTier.PublicLink, DateTimeOffset.UtcNow.AddMinutes(-1), "test", CancellationToken.None);
        var expiredResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/link/{expiredToken.Token}");
        await AssertNonLeakingNotFoundAsync(expiredResponse, item.Id, item.Title);

        var revokedToken = await MintPublicLinkAsync(item.Id, expiresAt: null);
        var revokeResponse = await _adminClient.DeleteAsync($"/api/v1/console/content/{item.Id}/share/link/{revokedToken.TokenId}");
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var revokedResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/link/{revokedToken.Token}");
        await AssertNonLeakingNotFoundAsync(revokedResponse, item.Id, item.Title);

        var privateToken = await MintPublicLinkAsync(item.Id, expiresAt: null);
        await SetAccessTierAsync(item.Id, ConsoleShareAccessTier.Private);
        var downgradedResponse = await _adminClient.GetAsync($"/api/v1/console/content/{item.Id}/share");
        Assert.Equal(HttpStatusCode.OK, downgradedResponse.StatusCode);
        var downgradedProjection = await ReadDataAsync<ConsoleShareProjection>(downgradedResponse);
        Assert.Equal(ConsoleShareAccessTier.Private, downgradedProjection.AccessTier);
        Assert.False(downgradedProjection.PublicLinkEnabled);
        Assert.False(downgradedProjection.AnonymousEligible);
        Assert.Contains(downgradedProjection.PublicLinkTokens, token => token.TokenId == privateToken.TokenId);

        var privateResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/link/{privateToken.Token}");
        await AssertNonLeakingNotFoundAsync(privateResponse, item.Id, item.Title);

        var invalidResponse = await _anonymousClient.GetAsync("/api/v1/console/share/link/not-a-real-token");
        await AssertNonLeakingNotFoundAsync(invalidResponse, item.Id, item.Title);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content/{id}/share/link")]
    public async Task MintPublicLink_RejectsExpiryPastInjectedClock()
    {
        var item = await CreateItemAsync("share-link-clock", ConsoleVisibility.Personal, title: "Clocked public link");
        await SetAccessTierAsync(item.Id, ConsoleShareAccessTier.PublicLink);

        _clock.Advance(TimeSpan.FromDays(2));
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/link",
            new MintPublicLinkRequest { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("expiresAt", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}/share/embed")]
    [Endpoint("POST /api/v1/console/content/{id}/share/embed")]
    [Endpoint("POST /api/v1/console/share/embed/{token}/redeem")]
    public async Task EmbedTokens_AreMintedRedeemedAndDeniedAfterExpiryOrDisable()
    {
        var item = await CreateItemAsync("share-embed", ConsoleVisibility.Organization, title: "Embed Leak Sentinel");

        var enableResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/embed",
            new SetEmbedRequest { Enabled = true, Audience = ConsoleEmbedAudience.Map },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        var enabledProjection = await ReadDataAsync<ConsoleShareProjection>(enableResponse);
        Assert.True(enabledProjection.EmbedEnabled);
        Assert.Equal(ConsoleEmbedAudience.Map, enabledProjection.EmbedAudience);

        var token = await MintEmbedTokenAsync(item.Id, ttlSeconds: 60);
        var redeemResponse = await _anonymousClient.PostAsync($"/api/v1/console/share/embed/{token.Token}/redeem", content: null);
        Assert.Equal(HttpStatusCode.OK, redeemResponse.StatusCode);
        var redeemBody = await redeemResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token.Token!, redeemBody, StringComparison.Ordinal);
        var redeemed = JsonSerializer.Deserialize<ApiResponse<ConsoleEmbedRedeemResponse>>(redeemBody, JsonOptions)!.Data!;
        Assert.Equal(item.Id, redeemed.ItemId);
        Assert.Equal(ConsoleEmbedAudience.Map, redeemed.Audience);
        Assert.True(redeemed.Endpoints.ContainsKey("self"));

        var expiredToken = await _fixture.GetService<IConsoleShareStore>()
            .MintEmbedTokenAsync(item.Id, ConsoleShareAccessTier.Organization, ConsoleEmbedAudience.Map, TimeSpan.FromSeconds(-1), "test", CancellationToken.None);
        var expiredResponse = await _anonymousClient.PostAsync($"/api/v1/console/share/embed/{expiredToken.Token}/redeem", content: null);
        await AssertNonLeakingNotFoundAsync(expiredResponse, item.Id, item.Title);

        var disableToken = await MintEmbedTokenAsync(item.Id, ttlSeconds: 60);
        var disableResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/embed",
            new SetEmbedRequest { Enabled = false },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        var disabledRedeemResponse = await _anonymousClient.PostAsync($"/api/v1/console/share/embed/{disableToken.Token}/redeem", content: null);
        await AssertNonLeakingNotFoundAsync(disabledRedeemResponse, item.Id, item.Title);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content/{id}/share/link")]
    [Endpoint("GET /api/v1/console/content/{id}/share")]
    [Endpoint("GET /api/v1/console/share/link/{token}")]
    public async Task MintPublicLink_OnVisibilityDerivedPublicItem_PreservesPublicIndexedAndResolves()
    {
        // Public visibility, no explicit share state => effective tier public-indexed (derived).
        var item = await CreateItemAsync("share-legacy-public", ConsoleVisibility.Public, title: "Legacy Public Sentinel");

        // Minting is the first share write; it must not downgrade the item to private.
        var token = await MintPublicLinkAsync(item.Id, expiresAt: null);

        var projection = await ReadDataAsync<ConsoleShareProjection>(
            await _adminClient.GetAsync($"/api/v1/console/content/{item.Id}/share"));
        Assert.Equal(ConsoleShareAccessTier.PublicIndexed, projection.AccessTier);
        Assert.True(projection.PublicLinkEnabled);
        Assert.True(projection.AnonymousEligible);

        // The minted link resolves under the same policy the mint check accepted.
        var resolveResponse = await _anonymousClient.GetAsync($"/api/v1/console/share/link/{token.Token}");
        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
        var resolved = await ReadDataAsync<ConsolePublicLinkResolutionResponse>(resolveResponse);
        Assert.Equal(item.Id, resolved.ItemId);
        Assert.Equal(ConsoleShareAccessTier.PublicIndexed, resolved.AccessTier);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}/share/embed")]
    [Endpoint("GET /api/v1/console/content/{id}/share")]
    public async Task SetEmbed_OnVisibilityDerivedItem_PreservesEffectiveTier()
    {
        // Organization visibility, no explicit share state => effective tier organization.
        var item = await CreateItemAsync("share-embed-tier", ConsoleVisibility.Organization, title: "Embed Tier Sentinel");

        // Enabling embed is the first share write; it must not reset the tier to private.
        var enableResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/embed",
            new SetEmbedRequest { Enabled = true, Audience = ConsoleEmbedAudience.Content },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        var enabled = await ReadDataAsync<ConsoleShareProjection>(enableResponse);
        Assert.Equal(ConsoleShareAccessTier.Organization, enabled.AccessTier);
        Assert.True(enabled.EmbedEnabled);

        // Disabling embed must likewise leave the tier intact.
        var disableResponse = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{item.Id}/share/embed",
            new SetEmbedRequest { Enabled = false },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disabled = await ReadDataAsync<ConsoleShareProjection>(disableResponse);
        Assert.Equal(ConsoleShareAccessTier.Organization, disabled.AccessTier);
        Assert.False(disabled.EmbedEnabled);
    }

    private async Task<ConsoleContentItem> CreateItemAsync(
        string name,
        ConsoleVisibility visibility,
        string? title = null,
        IReadOnlyList<ConsoleProvenanceRef>? provenance = null,
        ConsoleContentItemType itemType = ConsoleContentItemType.Layer)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = name,
            ItemType = itemType,
            Title = title,
            Visibility = visibility,
            Provenance = provenance,
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadDataAsync<ConsoleContentItem>(response);
    }

    private async Task SetAccessTierAsync(string itemId, ConsoleShareAccessTier accessTier)
    {
        var response = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{itemId}/share/access",
            new UpdateShareAccessRequest { AccessTier = accessTier },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ConsolePublicLinkToken> MintPublicLinkAsync(string itemId, DateTimeOffset? expiresAt)
    {
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/v1/console/content/{itemId}/share/link",
            new MintPublicLinkRequest { ExpiresAt = expiresAt },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await ReadDataAsync<ConsolePublicLinkToken>(response);
        Assert.False(string.IsNullOrWhiteSpace(token.Token));
        return token;
    }

    private async Task<ConsoleEmbedToken> MintEmbedTokenAsync(string itemId, int ttlSeconds)
    {
        var response = await _adminClient.PostAsJsonAsync(
            $"/api/v1/console/content/{itemId}/share/embed",
            new MintEmbedTokenRequest { Audience = ConsoleEmbedAudience.Map, TtlSeconds = ttlSeconds },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await ReadDataAsync<ConsoleEmbedToken>(response);
        Assert.False(string.IsNullOrWhiteSpace(token.Token));
        return token;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success, body);
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
    }

    private static async Task<T> ReadFailureDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success, body);
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
    }

    private static async Task AssertNonLeakingNotFoundAsync(HttpResponseMessage response, params string?[] forbiddenValues)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var value in forbiddenValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Assert.DoesNotContain(value, body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }
}
