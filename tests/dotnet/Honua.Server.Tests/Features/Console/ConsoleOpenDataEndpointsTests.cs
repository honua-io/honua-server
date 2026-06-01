// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Integration tests for the Console open-data DCAT and STAC publication API (#1214):
/// open-data page read/write, eligibility, DCAT export + validation, STAC
/// publish/update/unpublish/status, and the anonymous open-data/STAC reads with
/// non-leaking denial.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class ConsoleOpenDataEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "console-open-data-admin-key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: true) },
    };

    private static readonly double[] ExpectedBbox = { -122, 37, -121, 38 };
    private static readonly string[] PageTags = { "transportation", "trails" };
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _anonymousClient = null!;

    public ConsoleOpenDataEndpointsTests()
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
            .ReplaceService<IConsoleShareStore>(new InMemoryConsoleShareStore(_clock))
            .ReplaceService<IConsoleOpenDataStore>(new InMemoryConsoleOpenDataStore(_clock));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _anonymousClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/open-data/eligibility")]
    public async Task Eligibility_NonDistributableType_IsIneligibleWithStableReasonCode()
    {
        var dashboard = await CreateItemAsync("od-dashboard", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Dashboard);

        var response = await _adminClient.GetAsync($"/api/v1/console/content/{dashboard.Id}/open-data/eligibility");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var eligibility = await ReadDataAsync<ConsoleOpenDataEligibilityResponse>(response);
        Assert.False(eligibility.Eligible);
        Assert.Equal("not-distributable-type", eligibility.ReasonCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/open-data/eligibility")]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    public async Task Eligibility_DistributableType_FlipsWhenAccessTierBecomesPublicIndexed()
    {
        var layer = await CreateItemAsync("od-layer-elig", ConsoleVisibility.Organization, itemType: ConsoleContentItemType.Layer);

        var before = await ReadDataAsync<ConsoleOpenDataEligibilityResponse>(
            await _adminClient.GetAsync($"/api/v1/console/content/{layer.Id}/open-data/eligibility"));
        Assert.False(before.Eligible);
        Assert.Equal("not-public-indexed", before.ReasonCode);

        await SetPublicIndexedAsync(layer.Id);

        var after = await ReadDataAsync<ConsoleOpenDataEligibilityResponse>(
            await _adminClient.GetAsync($"/api/v1/console/content/{layer.Id}/open-data/eligibility"));
        Assert.True(after.Eligible);
        Assert.Equal("eligible", after.ReasonCode);
        Assert.Equal(ConsoleShareAccessTier.PublicIndexed, after.AccessTier);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/open-data")]
    [Endpoint("PUT /api/v1/console/content/{id}/open-data")]
    [Endpoint("GET /api/v1/console/content/{id}/open-data/dcat")]
    public async Task SavePage_ThenDcat_ReportsValidationSuccessAndMapsFields()
    {
        var layer = await CreateItemAsync("od-layer-dcat", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Roads");
        await SetPublicIndexedAsync(layer.Id);

        var saved = await SaveCompletePageAsync(layer.Id, title: "Public Roads");
        Assert.True(saved.DcatValidation.IsValid);
        Assert.True(saved.Eligibility.Eligible);

        var dcatResponse = await _adminClient.GetAsync($"/api/v1/console/content/{layer.Id}/open-data/dcat");
        Assert.Equal(HttpStatusCode.OK, dcatResponse.StatusCode);
        var dcat = await ReadDataAsync<ConsoleDcatExportResponse>(dcatResponse);
        Assert.True(dcat.Validation.IsValid);
        var dataset = Assert.Single(dcat.Catalog.Dataset);
        Assert.Equal(layer.Id, dataset.Identifier);
        Assert.Equal("Public Roads", dataset.Title);
        Assert.Equal("public", dataset.AccessLevel);
        Assert.NotNull(dataset.Publisher);
        Assert.Equal("City of Honua", dataset.Publisher!.Name);
        Assert.Equal("mailto:gis@honua.example", dataset.ContactPoint!.HasEmail);
        Assert.Equal("-122,37,-121,38", dataset.Spatial);
        var distribution = Assert.Single(dataset.Distribution!);
        Assert.Equal("https://data.honua.example/roads.geojson", distribution.AccessUrl);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content/{id}/open-data/dcat")]
    public async Task Dcat_WithIncompletePage_ReportsValidationErrorsForMissingRequiredFields()
    {
        var layer = await CreateItemAsync("od-layer-invalid", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Bare");
        await SetPublicIndexedAsync(layer.Id);

        // No page authored: only title/description default from the item; publisher,
        // contact, and license are missing => validation errors.
        var dcat = await ReadDataAsync<ConsoleDcatExportResponse>(
            await _adminClient.GetAsync($"/api/v1/console/content/{layer.Id}/open-data/dcat"));
        Assert.False(dcat.Validation.IsValid);
        var fields = dcat.Validation.Issues
            .Where(i => i.Severity == ConsoleOpenDataValidationSeverity.Error)
            .Select(i => i.Field)
            .ToHashSet();
        Assert.Contains("publisherName", fields);
        Assert.Contains("contactName", fields);
        Assert.Contains("license", fields);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/console/content/{id}/open-data")]
    public async Task SavePage_RejectsDistributionWithEmptyAccessUrl()
    {
        var layer = await CreateItemAsync("od-layer-badurl", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer);
        await SetPublicIndexedAsync(layer.Id);

        var response = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{layer.Id}/open-data",
            new UpdateOpenDataPageRequest
            {
                Distributions = new[] { new ConsoleOpenDataDistribution { AccessUrl = "  " } },
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content/{id}/open-data/stac/publish")]
    [Endpoint("GET /api/v1/console/content/{id}/open-data/stac")]
    [Endpoint("DELETE /api/v1/console/content/{id}/open-data/stac")]
    public async Task StacPublication_Publish_Update_Status_Unpublish_TransitionsState()
    {
        var layer = await CreateItemAsync("od-layer-stac", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Parks");
        await SetPublicIndexedAsync(layer.Id);
        await SaveCompletePageAsync(layer.Id);

        var publishResponse = await _adminClient.PostAsync($"/api/v1/console/content/{layer.Id}/open-data/stac/publish", content: null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await ReadDataAsync<ConsoleStacPublicationState>(publishResponse);
        Assert.Equal(ConsoleStacPublicationStatus.Published, published.Status);
        Assert.Equal(1, published.Revision);
        Assert.NotNull(published.CollectionId);
        var collectionId = published.CollectionId!;

        // Re-publish (update) increments the revision but keeps the collection id.
        var updated = await ReadDataAsync<ConsoleStacPublicationState>(
            await _adminClient.PostAsync($"/api/v1/console/content/{layer.Id}/open-data/stac/publish", content: null));
        Assert.Equal(2, updated.Revision);
        Assert.Equal(collectionId, updated.CollectionId);

        var status = await ReadDataAsync<ConsoleStacPublicationState>(
            await _adminClient.GetAsync($"/api/v1/console/content/{layer.Id}/open-data/stac"));
        Assert.Equal(ConsoleStacPublicationStatus.Published, status.Status);

        var unpublishResponse = await _adminClient.DeleteAsync($"/api/v1/console/content/{layer.Id}/open-data/stac");
        Assert.Equal(HttpStatusCode.OK, unpublishResponse.StatusCode);
        var unpublished = await ReadDataAsync<ConsoleStacPublicationState>(unpublishResponse);
        Assert.Equal(ConsoleStacPublicationStatus.Unpublished, unpublished.Status);

        // Unpublishing again is a 404 (nothing to unpublish).
        var repeatUnpublish = await _adminClient.DeleteAsync($"/api/v1/console/content/{layer.Id}/open-data/stac");
        Assert.Equal(HttpStatusCode.NotFound, repeatUnpublish.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content/{id}/open-data/stac/publish")]
    public async Task StacPublish_IneligibleOrInvalidPage_ReturnsConflict()
    {
        // Ineligible: distributable but not public-indexed.
        var notPublic = await CreateItemAsync("od-stac-ineligible", ConsoleVisibility.Organization, itemType: ConsoleContentItemType.Layer);
        var ineligibleResponse = await _adminClient.PostAsync($"/api/v1/console/content/{notPublic.Id}/open-data/stac/publish", content: null);
        Assert.Equal(HttpStatusCode.Conflict, ineligibleResponse.StatusCode);

        // Eligible but DCAT-invalid (no publisher/contact/license).
        var invalid = await CreateItemAsync("od-stac-invalid", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Bare");
        await SetPublicIndexedAsync(invalid.Id);
        var invalidResponse = await _adminClient.PostAsync($"/api/v1/console/content/{invalid.Id}/open-data/stac/publish", content: null);
        Assert.Equal(HttpStatusCode.Conflict, invalidResponse.StatusCode);
        var validation = await ReadFailureDataAsync<ConsoleOpenDataValidationResult>(invalidResponse);
        Assert.False(validation.IsValid);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/open-data/datasets/{id}")]
    [Endpoint("GET /api/v1/open-data/datasets/{id}/data.json")]
    [Endpoint("GET /api/v1/open-data/datasets/{id}/schema.org")]
    public async Task AnonymousReads_ForPublicIndexedItem_ReturnDcatStacAndSchemaOrg()
    {
        var layer = await CreateItemAsync("od-anon", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Trails");
        await SetPublicIndexedAsync(layer.Id);
        await SaveCompletePageAsync(layer.Id);

        var datasetResponse = await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{layer.Id}");
        Assert.Equal(HttpStatusCode.OK, datasetResponse.StatusCode);
        var dataset = await ReadDataAsync<ConsoleOpenDataPage>(datasetResponse);
        Assert.Equal("Public Trails", dataset.Title);

        var dataJsonResponse = await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{layer.Id}/data.json");
        Assert.Equal(HttpStatusCode.OK, dataJsonResponse.StatusCode);
        var catalog = await dataJsonResponse.Content.ReadFromJsonAsync<DcatCatalog>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Single(catalog!.Dataset);

        var schemaResponse = await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{layer.Id}/schema.org");
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var schemaBody = await schemaResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"@type\":\"Dataset\"", schemaBody, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/open-data/datasets/{id}")]
    [Endpoint("GET /api/v1/open-data/datasets/{id}/data.json")]
    public async Task AnonymousReads_ForPrivateOrIneligibleItem_Return404WithoutLeakingTitle()
    {
        var privateLayer = await CreateItemAsync("od-private", ConsoleVisibility.Personal, itemType: ConsoleContentItemType.Layer, title: "Do Not Leak Layer");
        await AssertNonLeakingNotFoundAsync(
            await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{privateLayer.Id}"),
            privateLayer.Title);
        await AssertNonLeakingNotFoundAsync(
            await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{privateLayer.Id}/data.json"),
            privateLayer.Title);

        // Public-indexed but a non-distributable type is also ineligible.
        var dashboard = await CreateItemAsync("od-public-dash", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Dashboard, title: "Secret Dashboard");
        await SetPublicIndexedAsync(dashboard.Id);
        await AssertNonLeakingNotFoundAsync(
            await _anonymousClient.GetAsync($"/api/v1/open-data/datasets/{dashboard.Id}"),
            dashboard.Title);

        // Missing item.
        var missing = await _anonymousClient.GetAsync("/api/v1/open-data/datasets/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/open-data/stac")]
    [Endpoint("GET /api/v1/open-data/stac/collections/{collectionId}")]
    [Endpoint("GET /api/v1/open-data/stac/collections/{collectionId}/items/{itemId}")]
    public async Task AnonymousStac_AfterPublish_ExposesCatalogCollectionAndItem_AndDropsThemAfterUnpublish()
    {
        var layer = await CreateItemAsync("od-stac-anon", ConsoleVisibility.Public, itemType: ConsoleContentItemType.Layer, title: "Rivers");
        await SetPublicIndexedAsync(layer.Id);
        await SaveCompletePageAsync(layer.Id);
        var published = await ReadDataAsync<ConsoleStacPublicationState>(
            await _adminClient.PostAsync($"/api/v1/console/content/{layer.Id}/open-data/stac/publish", content: null));
        var collectionId = published.CollectionId!;

        var catalog = await ReadStacAsync<StacProjectionCatalog>(
            await _anonymousClient.GetAsync("/api/v1/open-data/stac"));
        Assert.Contains(catalog.Links, link => link.Rel == "child" && link.Href.Contains(collectionId, StringComparison.Ordinal));

        var collectionResponse = await _anonymousClient.GetAsync($"/api/v1/open-data/stac/collections/{collectionId}");
        Assert.Equal(HttpStatusCode.OK, collectionResponse.StatusCode);
        var collection = await ReadStacAsync<StacProjectionCollection>(collectionResponse);
        Assert.Equal(collectionId, collection.Id);
        var bbox = Assert.Single(collection.Extent.Spatial.Bbox);
        Assert.Equal(ExpectedBbox, bbox);

        var itemResponse = await _anonymousClient.GetAsync($"/api/v1/open-data/stac/collections/{collectionId}/items/{collectionId}");
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        var item = await ReadStacAsync<StacProjectionItem>(itemResponse);
        Assert.Equal(collectionId, item.Collection);
        Assert.NotNull(item.Geometry);

        // A mismatched item id under the collection is a non-leaking 404.
        var wrongItem = await _anonymousClient.GetAsync($"/api/v1/open-data/stac/collections/{collectionId}/items/other");
        Assert.Equal(HttpStatusCode.NotFound, wrongItem.StatusCode);

        // After unpublish the STAC reads deny and the catalog drops the child.
        var unpublishResponse = await _adminClient.DeleteAsync($"/api/v1/console/content/{layer.Id}/open-data/stac");
        Assert.Equal(HttpStatusCode.OK, unpublishResponse.StatusCode);
        var afterCollection = await _anonymousClient.GetAsync($"/api/v1/open-data/stac/collections/{collectionId}");
        await AssertNonLeakingNotFoundAsync(afterCollection, layer.Title);
        var afterCatalog = await ReadStacAsync<StacProjectionCatalog>(
            await _anonymousClient.GetAsync("/api/v1/open-data/stac"));
        Assert.DoesNotContain(afterCatalog.Links, link => link.Rel == "child" && link.Href.Contains(collectionId, StringComparison.Ordinal));
    }

    private async Task<ConsoleContentItem> CreateItemAsync(
        string name,
        ConsoleVisibility visibility,
        ConsoleContentItemType itemType,
        string? title = null)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = name,
            ItemType = itemType,
            Title = title,
            Visibility = visibility,
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadDataAsync<ConsoleContentItem>(response);
    }

    private async Task SetPublicIndexedAsync(string itemId)
    {
        var response = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{itemId}/share/access",
            new UpdateShareAccessRequest { AccessTier = ConsoleShareAccessTier.PublicIndexed },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ConsoleOpenDataPageResponse> SaveCompletePageAsync(string itemId, string title = "Public Trails")
    {
        var response = await _adminClient.PutAsJsonAsync(
            $"/api/v1/console/content/{itemId}/open-data",
            new UpdateOpenDataPageRequest
            {
                Title = title,
                Description = "A complete open-data dataset.",
                PublisherName = "City of Honua",
                ContactName = "GIS Team",
                ContactEmail = "gis@honua.example",
                License = "https://creativecommons.org/licenses/by/4.0/",
                LandingPage = "https://data.honua.example/trails",
                Tags = PageTags,
                Distributions = new[]
                {
                    new ConsoleOpenDataDistribution
                    {
                        Title = "GeoJSON download",
                        AccessUrl = "https://data.honua.example/roads.geojson",
                        MediaType = "application/geo+json",
                        Format = "GeoJSON",
                    },
                },
                SpatialExtent = new ConsoleSpatialExtent { West = -122, South = 37, East = -121, North = 38 },
                TemporalExtent = new ConsoleTemporalExtent { Start = DateTimeOffset.Parse("2020-01-01T00:00:00Z", CultureInfo.InvariantCulture) },
            },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadDataAsync<ConsoleOpenDataPageResponse>(response);
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

    private static async Task<T> ReadStacAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
        Assert.NotNull(value);
        return value!;
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

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
