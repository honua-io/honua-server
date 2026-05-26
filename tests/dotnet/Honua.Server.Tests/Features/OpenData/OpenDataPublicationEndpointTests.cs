// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.OpenData.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.OpenData.Models;
using Honua.Server.Features.Protocols.Stac.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.OpenData;

/// <summary>
/// Integration coverage for Console open-data, DCAT/data.json, and STAC publication APIs.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Protocol(TestProtocols.Stac)]
[Operation(Operations.Metadata)]
public sealed class OpenDataPublicationEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "open-data-admin-key";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly WebAppFixture _fixture;
    private HttpClient _adminClient = null!;
    private HttpClient _publicClient = null!;

    public OpenDataPublicationEndpointTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("OpenData:Enabled", "true");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _publicClient = _fixture.CreateClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("PUT /api/v1/admin/open-data/{itemId}")]
    [Endpoint("GET /api/v1/admin/open-data/{itemId}")]
    [Endpoint("GET /open-data/{itemId}")]
    [Endpoint("GET /open-data")]
    public async Task UpdatePage_ForEligiblePublicItem_AllowsAnonymousOpenDataRead()
    {
        var item = await CreateContentAsync(
            name: "public-layer",
            visibility: ConsoleVisibility.Public,
            title: "Public Layer",
            description: "A public layer for open data.");

        var update = CompleteOpenDataPage("Published Layer");
        var updateResponse = await _adminClient.PutAsJsonAsync($"/api/v1/admin/open-data/{item.Id}", update, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var adminPage = await ReadEnvelopeAsync<OpenDataPageAdminResponse>(updateResponse);
        Assert.True(adminPage.Data!.Eligibility.IsEligible);
        Assert.True(adminPage.Data.Page.IsPublished);

        var getPageResponse = await _adminClient.GetAsync($"/api/v1/admin/open-data/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, getPageResponse.StatusCode);

        var publicResponse = await _publicClient.GetAsync($"/open-data/{item.Id}");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicItem = JsonSerializer.Deserialize<OpenDataItemResponse>(
            await publicResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Equal("Published Layer", publicItem.Title);
        Assert.Equal("Dataset", publicItem.SchemaOrg.Type);
        Assert.Single(publicItem.Distributions);

        using var jsonLdRequest = new HttpRequestMessage(HttpMethod.Get, $"/open-data/{item.Id}");
        jsonLdRequest.Headers.Accept.ParseAdd("application/ld+json");
        var jsonLdResponse = await _publicClient.SendAsync(jsonLdRequest);
        Assert.Equal(HttpStatusCode.OK, jsonLdResponse.StatusCode);
        Assert.Equal("application/ld+json", jsonLdResponse.Content.Headers.ContentType?.MediaType);
        var schemaOrg = JsonSerializer.Deserialize<SchemaOrgDatasetResponse>(
            await jsonLdResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Equal("https://schema.org", schemaOrg.Context);
        Assert.Equal("Dataset", schemaOrg.Type);
        Assert.Equal("Published Layer", schemaOrg.Name);

        var listResponse = await _publicClient.GetAsync("/open-data");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = JsonSerializer.Deserialize<OpenDataListResponse>(
            await listResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Contains(list.Items, listed => listed.ItemId == item.Id);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("PUT /api/v1/admin/open-data/{itemId}")]
    [Endpoint("GET /api/v1/admin/open-data/{itemId}/eligibility")]
    [Endpoint("GET /open-data/{itemId}")]
    [Endpoint("GET /open-data")]
    public async Task UpdatePage_WithOpenDataCapabilityDisabled_ReturnsDisabledEligibilityAndHidesAnonymousRead()
    {
        var disabledFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });

        try
        {
            await disabledFixture.InitializeAsync();
            var adminClient = disabledFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var publicClient = disabledFixture.CreateClient();

            var item = await CreateContentAsync(
                adminClient,
                name: "disabled-capability-layer",
                visibility: ConsoleVisibility.Public,
                title: "Disabled Capability Layer",
                description: "A public layer blocked by the deployment capability.");

            var updateResponse = await adminClient.PutAsJsonAsync(
                $"/api/v1/admin/open-data/{item.Id}",
                CompleteOpenDataPage("Capability Disabled"),
                JsonOptions);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            var adminPage = await ReadEnvelopeAsync<OpenDataPageAdminResponse>(updateResponse);
            Assert.False(adminPage.Data!.Eligibility.IsEligible);
            Assert.Contains(adminPage.Data.Eligibility.Reasons, reason => reason.Code == "OpenDataDisabled");

            var eligibilityResponse = await adminClient.GetAsync($"/api/v1/admin/open-data/{item.Id}/eligibility");
            Assert.Equal(HttpStatusCode.OK, eligibilityResponse.StatusCode);
            var eligibility = await ReadEnvelopeAsync<OpenDataEligibility>(eligibilityResponse);
            Assert.Contains(eligibility.Data!.Reasons, reason => reason.Code == "OpenDataDisabled");

            var publicRead = await publicClient.GetAsync($"/open-data/{item.Id}");
            Assert.Equal(HttpStatusCode.NotFound, publicRead.StatusCode);

            var listResponse = await publicClient.GetAsync("/open-data");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var list = JsonSerializer.Deserialize<OpenDataListResponse>(
                await listResponse.Content.ReadAsStringAsync(),
                JsonOptions)!;
            Assert.DoesNotContain(list.Items, listed => listed.ItemId == item.Id);
        }
        finally
        {
            await disabledFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("PUT /api/v1/admin/open-data/{itemId}")]
    [Endpoint("GET /api/v1/admin/open-data/{itemId}/eligibility")]
    [Endpoint("GET /open-data/{itemId}")]
    public async Task AnonymousRead_ForPrivateAndIneligibleItems_ReturnsNonLeakingNotFound()
    {
        var privateItem = await CreateContentAsync(
            name: "private-layer",
            visibility: ConsoleVisibility.Personal,
            title: "Private Layer",
            description: "Should not be public.");
        var missingTitleItem = await CreateContentAsync(
            name: "missing-title",
            visibility: ConsoleVisibility.Public,
            title: null,
            description: "Title is required.");

        await _adminClient.PutAsJsonAsync($"/api/v1/admin/open-data/{privateItem.Id}", CompleteOpenDataPage("Private Layer"), JsonOptions);
        await _adminClient.PutAsJsonAsync($"/api/v1/admin/open-data/{missingTitleItem.Id}", new OpenDataPageUpdateRequest
        {
            IsPublished = true,
            Description = "Still missing title",
            Publisher = new OpenDataOrganization { Name = "Honua" },
            ContactPoint = new OpenDataContact { Email = "data@example.com" },
            License = "https://creativecommons.org/licenses/by/4.0/",
            Distributions = [new OpenDataDistribution { Title = "GeoJSON", DownloadUrl = "https://example.com/missing-title.geojson" }]
        }, JsonOptions);

        var privatePublicRead = await _publicClient.GetAsync($"/open-data/{privateItem.Id}");
        var missingTitlePublicRead = await _publicClient.GetAsync($"/open-data/{missingTitleItem.Id}");
        Assert.Equal(HttpStatusCode.NotFound, privatePublicRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingTitlePublicRead.StatusCode);
        Assert.Equal(0, privatePublicRead.Content.Headers.ContentLength ?? 0);
        Assert.Equal(0, missingTitlePublicRead.Content.Headers.ContentLength ?? 0);

        var eligibilityResponse = await _adminClient.GetAsync($"/api/v1/admin/open-data/{missingTitleItem.Id}/eligibility");
        Assert.Equal(HttpStatusCode.OK, eligibilityResponse.StatusCode);
        var eligibility = await ReadEnvelopeAsync<OpenDataEligibility>(eligibilityResponse);
        Assert.False(eligibility.Data!.IsEligible);
        Assert.Contains(eligibility.Data.Reasons, reason => reason.Code == "MissingTitle");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("GET /api/v1/admin/open-data/{itemId}/eligibility")]
    public async Task Eligibility_ForBlockedItem_ReturnsAllBlockingReasons()
    {
        var item = await CreateContentAsync(
            name: "blocked-open-data",
            visibility: ConsoleVisibility.Personal,
            title: null,
            description: null,
            labels: new Dictionary<string, string> { ["legalHold"] = "true" });

        var response = await _adminClient.GetAsync($"/api/v1/admin/open-data/{item.Id}/eligibility");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await ReadEnvelopeAsync<OpenDataEligibility>(response);

        var reasonCodes = envelope.Data!.Reasons.Select(static reason => reason.Code).ToArray();
        Assert.Contains("MissingTitle", reasonCodes);
        Assert.Contains("PolicyBlocked", reasonCodes);
        Assert.Contains("ComplianceBlocked", reasonCodes);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("PUT /api/v1/admin/open-data/{itemId}")]
    [Endpoint("GET /open-data/catalog.json")]
    [Endpoint("GET /api/v1/admin/open-data/dcat/status")]
    [Endpoint("POST /api/v1/admin/open-data/dcat/validate")]
    public async Task DcatCatalogAndValidation_ReportDatasetsAndDocumentedExceptions()
    {
        var completeItem = await CreateContentAsync(
            name: "dcat-complete",
            visibility: ConsoleVisibility.Public,
            title: "Complete DCAT",
            description: "Complete DCAT description.");
        await _adminClient.PutAsJsonAsync($"/api/v1/admin/open-data/{completeItem.Id}", CompleteOpenDataPage("Complete DCAT"), JsonOptions);

        var sparseItem = await CreateContentAsync(
            name: "dcat-sparse",
            visibility: ConsoleVisibility.Public,
            title: "Sparse DCAT",
            description: null);
        await _adminClient.PutAsJsonAsync($"/api/v1/admin/open-data/{sparseItem.Id}", new OpenDataPageUpdateRequest
        {
            Title = "Sparse DCAT",
            IsPublished = true
        }, JsonOptions);

        var catalogResponse = await _publicClient.GetAsync("/open-data/catalog.json");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalog = JsonSerializer.Deserialize<DcatCatalogResponse>(
            await catalogResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Contains(catalog.Dataset, dataset => dataset.Identifier.EndsWith(completeItem.Id, StringComparison.Ordinal));

        var statusResponse = await _adminClient.GetAsync("/api/v1/admin/open-data/dcat/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await ReadEnvelopeAsync<OpenDataDcatStatusResponse>(statusResponse);
        Assert.True(status.Data!.Validation.ItemCount >= 2);
        Assert.True(status.Data.Validation.ValidationExceptionCount > 0);

        var validateResponse = await _adminClient.PostAsJsonAsync(
            "/api/v1/admin/open-data/dcat/validate",
            new OpenDataDcatValidateRequest { ItemId = sparseItem.Id },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, validateResponse.StatusCode);
        var validation = await ReadEnvelopeAsync<OpenDataValidationSummary>(validateResponse);
        Assert.True(validation.Data!.IsValid);
        Assert.Contains(
            validation.Data.Items.Single().Issues,
            issue => issue.Field == "description" && issue.DocumentedException);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("POST /api/v1/admin/stac/publications")]
    [Endpoint("GET /api/v1/admin/stac/publications/{collectionId}")]
    [Endpoint("PUT /api/v1/admin/stac/publications/{collectionId}")]
    [Endpoint("DELETE /api/v1/admin/stac/publications/{collectionId}")]
    [Endpoint("GET /stac/collections")]
    [Endpoint("GET /stac/collections/{collectionId}")]
    public async Task StacPublicationLifecycle_PublishUpdateUnpublish_ReturnsStatusReadback()
    {
        var item = await CreateContentAsync(
            name: "stac-layer",
            visibility: ConsoleVisibility.Public,
            title: "STAC Layer",
            description: "STAC publication source.");

        var publishResponse = await _adminClient.PostAsJsonAsync("/api/v1/admin/stac/publications", new StacPublicationPublishRequest
        {
            ItemId = item.Id,
            CollectionId = "stac-life",
            Title = "Initial STAC"
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
        Assert.Equal("http://localhost/stac/collections/stac-life", publishResponse.Headers.Location?.ToString());

        var published = await ReadEnvelopeAsync<StacPublicationStatusResponse>(publishResponse);
        Assert.Equal(OpenDataStacPublicationStatus.Published, published.Data!.Status);
        Assert.Equal("stac-life", published.Data.CollectionId);

        var publicCollectionResponse = await _publicClient.GetAsync("/stac/collections/stac-life");
        Assert.Equal(HttpStatusCode.OK, publicCollectionResponse.StatusCode);
        var publicCollection = JsonSerializer.Deserialize<StacCollection>(
            await publicCollectionResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Equal("stac-life", publicCollection.Id);
        Assert.Equal("Initial STAC", publicCollection.Title);

        var publicCollectionsResponse = await _publicClient.GetAsync("/stac/collections");
        Assert.Equal(HttpStatusCode.OK, publicCollectionsResponse.StatusCode);
        var publicCollections = JsonSerializer.Deserialize<StacCollectionsResponse>(
            await publicCollectionsResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Contains(publicCollections.Collections, collection => collection.Id == "stac-life");

        var statusResponse = await _adminClient.GetAsync("/api/v1/admin/stac/publications/stac-life");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var updateResponse = await _adminClient.PutAsJsonAsync("/api/v1/admin/stac/publications/stac-life", new StacPublicationUpdateRequest
        {
            Title = "Updated STAC",
            Description = "Updated publication metadata."
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadEnvelopeAsync<StacPublicationStatusResponse>(updateResponse);
        Assert.Equal("Updated STAC", updated.Data!.Title);

        var publicUpdatedResponse = await _publicClient.GetAsync("/stac/collections/stac-life");
        Assert.Equal(HttpStatusCode.OK, publicUpdatedResponse.StatusCode);
        var publicUpdated = JsonSerializer.Deserialize<StacCollection>(
            await publicUpdatedResponse.Content.ReadAsStringAsync(),
            JsonOptions)!;
        Assert.Equal("Updated STAC", publicUpdated.Title);

        var deleteResponse = await _adminClient.DeleteAsync("/api/v1/admin/stac/publications/stac-life");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await _adminClient.GetAsync("/api/v1/admin/stac/publications/stac-life");
        Assert.Equal(HttpStatusCode.OK, afterDeleteResponse.StatusCode);
        var afterDelete = await ReadEnvelopeAsync<StacPublicationStatusResponse>(afterDeleteResponse);
        Assert.Equal(OpenDataStacPublicationStatus.Unpublished, afterDelete.Data!.Status);

        var publicAfterDeleteResponse = await _publicClient.GetAsync("/stac/collections/stac-life");
        Assert.Equal(HttpStatusCode.NotFound, publicAfterDeleteResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/stac/publications")]
    [Endpoint("PUT /api/v1/admin/stac/publications/{collectionId}")]
    [Endpoint("DELETE /api/v1/admin/stac/publications/{collectionId}")]
    public async Task StacPublicationMutation_WhenApprovalRequired_ReturnsForbidden()
    {
        var approvalFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("OpenData:Enabled", "true");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOperatorApprovalEvaluator>();
                services.AddSingleton<IOperatorApprovalEvaluator>(new AlwaysRequiresApprovalEvaluator());
            });

        try
        {
            await approvalFixture.InitializeAsync();
            var adminClient = approvalFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            var publishResponse = await adminClient.PostAsJsonAsync(
                "/api/v1/admin/stac/publications",
                new StacPublicationPublishRequest
                {
                    ItemId = "approval-source",
                    CollectionId = "approval-gated"
                },
                JsonOptions);
            Assert.Equal(HttpStatusCode.Forbidden, publishResponse.StatusCode);

            var updateResponse = await adminClient.PutAsJsonAsync(
                "/api/v1/admin/stac/publications/approval-gated",
                new StacPublicationUpdateRequest { Title = "Blocked update" },
                JsonOptions);
            Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);

            var deleteResponse = await adminClient.DeleteAsync("/api/v1/admin/stac/publications/approval-gated");
            Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
        }
        finally
        {
            await approvalFixture.DisposeAsync();
        }
    }

    private static OpenDataPageUpdateRequest CompleteOpenDataPage(string title)
    {
        return new OpenDataPageUpdateRequest
        {
            Title = title,
            Description = $"{title} description.",
            Publisher = new OpenDataOrganization { Name = "Honua", Url = "https://honua.io" },
            ContactPoint = new OpenDataContact { Name = "Data steward", Email = "data@example.com" },
            License = "https://creativecommons.org/licenses/by/4.0/",
            Tags = ["open-data", "test"],
            LandingPage = $"https://example.com/{Uri.EscapeDataString(title)}",
            Distributions =
            [
                new OpenDataDistribution
                {
                    Title = "GeoJSON",
                    Format = "GeoJSON",
                    MediaType = "application/geo+json",
                    DownloadUrl = "https://example.com/data.geojson"
                }
            ],
            SpatialCoverage = new OpenDataSpatialExtent
            {
                MinX = -158.3,
                MinY = 21.2,
                MaxX = -157.6,
                MaxY = 21.8
            },
            TemporalCoverage = new OpenDataTemporalExtent
            {
                Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                End = DateTimeOffset.Parse("2026-12-31T00:00:00Z", CultureInfo.InvariantCulture)
            },
            IsPublished = true
        };
    }

    private async Task<ConsoleContentItem> CreateContentAsync(
        string name,
        ConsoleVisibility visibility,
        string? title,
        string? description,
        IReadOnlyDictionary<string, string>? labels = null)
        => await CreateContentAsync(_adminClient, name, visibility, title, description, labels).ConfigureAwait(false);

    private static async Task<ConsoleContentItem> CreateContentAsync(
        HttpClient adminClient,
        string name,
        ConsoleVisibility visibility,
        string? title,
        string? description,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        var response = await adminClient.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = name,
            ItemType = ConsoleContentItemType.Layer,
            Title = title,
            Description = description,
            Visibility = visibility,
            Labels = labels
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await ReadEnvelopeAsync<ConsoleContentItem>(response);
        return envelope.Data!;
    }

    private static async Task<ApiResponse<T>> ReadEnvelopeAsync<T>(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(payload, JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success, envelope.Message);
        Assert.NotNull(envelope.Data);
        return envelope;
    }

    private sealed class AlwaysRequiresApprovalEvaluator : IOperatorApprovalEvaluator
    {
        public ApprovalRequirement Evaluate(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request)
            => ApprovalRequirement.Required(
                "operator.test-policy",
                "test-approval-required");
    }
}
