// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Integration tests for the Console session bootstrap, content CRUD, action
/// check, and provenance endpoints (#1162).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public class ConsoleSessionEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "console-admin-key";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ConsoleSessionEndpointTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/session")]
    public async Task GetSession_AsAdmin_ReturnsProfileCapabilitiesEntitlementsAndContent()
    {
        var response = await _client.GetAsync("/api/v1/console/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<ConsoleSessionContext>>(payload, JsonOptions);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.NotNull(envelope.Data!.User);
        Assert.NotNull(envelope.Data.Content);
        Assert.NotEmpty(envelope.Data.NavigationEntitlements);
        Assert.Contains("admin.rbac.write", envelope.Data.Capabilities);
        Assert.Contains(envelope.Data.NavigationEntitlements, e => e.RouteKey == "admin" && e.Allowed);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    public async Task CreateThenGetContentItem_ProducesComputedActions()
    {
        var create = new CreateConsoleContentItemRequest
        {
            Name = "test-map",
            ItemType = ConsoleContentItemType.SavedMap,
            Title = "Test Map",
            Visibility = ConsoleVisibility.Organization,
            Tags = new[] { "test" },
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", create, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.NotNull(created?.Data);
        Assert.False(string.IsNullOrWhiteSpace(created!.Data!.Id));
        Assert.Contains(ConsoleContentAction.Administer, created.Data.Actions);
        Assert.Equal(1, created.Data.Generation);

        var getResponse = await _client.GetAsync($"/api/v1/console/content/{created.Data.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await getResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.Equal(created.Data.Id, fetched!.Data!.Id);
        Assert.Equal(ConsoleContentItemType.SavedMap, fetched.Data.ItemType);
        Assert.NotEmpty(fetched.Data.Actions);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    public async Task ListContent_WithItemTypeFilter_AppliesFilter()
    {
        await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "list-layer",
            ItemType = ConsoleContentItemType.Layer,
            Visibility = ConsoleVisibility.Public,
        }, JsonOptions);
        await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "list-dashboard",
            ItemType = ConsoleContentItemType.Dashboard,
            Visibility = ConsoleVisibility.Organization,
        }, JsonOptions);

        var response = await _client.GetAsync("/api/v1/console/content?itemType=layer&limit=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listing = JsonSerializer.Deserialize<ApiResponse<ConsoleContentListResponse>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);

        Assert.NotNull(listing?.Data);
        Assert.NotEmpty(listing!.Data!.Items);
        Assert.All(listing.Data.Items, item => Assert.Equal(ConsoleContentItemType.Layer, item.ItemType));
    }

    [IntegrationTest]
    [Endpoint("PATCH /api/v1/console/content/{id}")]
    public async Task PatchContent_UpdatesDisplayableFields()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "patch-target",
            ItemType = ConsoleContentItemType.Report,
            Visibility = ConsoleVisibility.Personal,
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var patch = new PatchConsoleContentItemRequest
        {
            Title = "Patched title",
            Visibility = ConsoleVisibility.Organization,
            Tags = new[] { "patched" },
        };
        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/console/content/{created.Data!.Id}")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var patchResponse = await _client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patched = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await patchResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.Equal("Patched title", patched!.Data!.Title);
        Assert.Equal(ConsoleVisibility.Organization, patched.Data.Visibility);
        Assert.Contains("patched", patched.Data.Tags);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ReplaceContent_IncrementsGeneration()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "put-target",
            ItemType = ConsoleContentItemType.Dashboard,
            Visibility = ConsoleVisibility.Organization,
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var update = new UpdateConsoleContentItemRequest
        {
            Name = "put-target",
            ItemType = ConsoleContentItemType.Dashboard,
            Title = "Replaced",
            Generation = created.Data!.Generation,
        };

        var putResponse = await _client.PutAsJsonAsync($"/api/v1/console/content/{created.Data.Id}", update, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var updated = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await putResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.Equal("Replaced", updated!.Data!.Title);
        Assert.True(updated.Data.Generation > created.Data.Generation);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ReplaceContent_OmittedNullableFields_AreCleared()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "put-clear-target",
            ItemType = ConsoleContentItemType.Dashboard,
            Title = "Original title",
            Description = "Original description",
            Visibility = ConsoleVisibility.Organization,
            Tags = ["alpha", "beta"],
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var replace = new UpdateConsoleContentItemRequest
        {
            Name = "put-clear-target",
            ItemType = ConsoleContentItemType.Dashboard,
            // Title, Description, Tags, Visibility intentionally omitted — PUT
            // must treat them as cleared/defaulted, not preserved.
            Generation = created.Data!.Generation,
        };

        var putResponse = await _client.PutAsJsonAsync($"/api/v1/console/content/{created.Data.Id}", replace, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var replaced = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await putResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.Null(replaced!.Data!.Title);
        Assert.Null(replaced.Data.Description);
        Assert.Empty(replaced.Data.Tags);
        Assert.Equal(ConsoleVisibility.Personal, replaced.Data.Visibility);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}")]
    public async Task ReplaceContent_WithMismatchedGeneration_ReturnsConflict()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "put-conflict-target",
            ItemType = ConsoleContentItemType.Dashboard,
            Visibility = ConsoleVisibility.Organization,
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var update = new UpdateConsoleContentItemRequest
        {
            Name = "put-conflict-target",
            ItemType = ConsoleContentItemType.Dashboard,
            Title = "Should not land",
            Generation = (created.Data!.Generation ?? 1) + 99,
        };

        var putResponse = await _client.PutAsJsonAsync($"/api/v1/console/content/{created.Data.Id}", update, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, putResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/search")]
    public async Task SearchContent_FindsByTerm()
    {
        await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "search-unique-needle",
            ItemType = ConsoleContentItemType.Layer,
            Visibility = ConsoleVisibility.Public,
        }, JsonOptions);

        var response = await _client.GetAsync("/api/v1/console/content/search?q=needle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonSerializer.Deserialize<ApiResponse<ConsoleContentListResponse>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);

        Assert.NotNull(payload?.Data);
        Assert.Contains(payload!.Data!.Items, item => item.Name == "search-unique-needle");
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/console/content/{id}")]
    public async Task DeleteContent_RemovesItem()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "delete-target",
            ItemType = ConsoleContentItemType.GeneratedApp,
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var deleteResponse = await _client.DeleteAsync($"/api/v1/console/content/{created.Data!.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/v1/console/content/{created.Data.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/actions/check")]
    public async Task CheckActions_ReturnsAllowedAndDeniedSets()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "check-target",
            ItemType = ConsoleContentItemType.Service,
            Visibility = ConsoleVisibility.Public,
        }, JsonOptions);
        var created = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await createResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var checkRequest = new ConsoleActionCheckRequest
        {
            Targets = new[]
            {
                new ConsoleActionCheckTarget { ItemId = created.Data!.Id },
                new ConsoleActionCheckTarget { RouteKey = "admin" },
                new ConsoleActionCheckTarget { ItemId = "missing-id" },
            },
            Actions = new[]
            {
                ConsoleContentAction.View,
                ConsoleContentAction.Administer,
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/console/actions/check", checkRequest, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonSerializer.Deserialize<ApiResponse<ConsoleActionCheckResponse>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);

        Assert.NotNull(payload?.Data);
        Assert.Equal(3, payload!.Data!.Results.Count);

        var itemResult = payload.Data.Results.Single(r => r.ItemId == created.Data.Id);
        Assert.Contains(ConsoleContentAction.View, itemResult.Allowed);
        Assert.Contains(ConsoleContentAction.Administer, itemResult.Allowed);

        var routeResult = payload.Data.Results.Single(r => r.RouteKey == "admin");
        Assert.NotEmpty(routeResult.Allowed);

        var missingResult = payload.Data.Results.Single(r => r.ItemId == "missing-id");
        Assert.True(missingResult.NotFound);
        Assert.Empty(missingResult.Allowed);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/provenance")]
    public async Task GetProvenance_ReturnsChain()
    {
        var catalogResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "prov-catalog",
            ItemType = ConsoleContentItemType.Layer,
        }, JsonOptions);
        var catalog = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await catalogResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var serviceResponse = await _client.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "prov-service",
            ItemType = ConsoleContentItemType.Service,
            Provenance = new[]
            {
                new ConsoleProvenanceRef { Kind = "catalog-resource", ItemId = catalog.Data!.Id, Rel = "publishes" },
            },
        }, JsonOptions);
        var service = JsonSerializer.Deserialize<ApiResponse<ConsoleContentItem>>(
            await serviceResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        var chainResponse = await _client.GetAsync($"/api/v1/console/content/{service.Data!.Id}/provenance");
        Assert.Equal(HttpStatusCode.OK, chainResponse.StatusCode);
        var chain = JsonSerializer.Deserialize<ApiResponse<ConsoleProvenanceChainResponse>>(
            await chainResponse.Content.ReadAsStringAsync(), JsonOptions);

        Assert.NotNull(chain?.Data);
        Assert.Equal(service.Data.Id, chain!.Data!.ItemId);
        Assert.Equal(2, chain.Data.Chain.Count);
        Assert.Equal(service.Data.Id, chain.Data.Chain[0].Id);
        Assert.Equal(catalog.Data.Id, chain.Data.Chain[1].Id);
    }
}
