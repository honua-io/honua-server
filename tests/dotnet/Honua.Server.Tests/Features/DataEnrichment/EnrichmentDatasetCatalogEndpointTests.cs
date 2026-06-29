// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.DataEnrichment;

/// <summary>
/// Endpoint-level integration tests for the managed enrichment-dataset catalog
/// (#2280): discovery (<c>GET /api/enrich/datasets</c>, <c>/{id}</c>) and admin
/// registration (<c>POST/PUT/DELETE /api/enrich/datasets</c>). Runs as Pro edition so
/// Pro-tier datasets are visible to discovery.
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class EnrichmentDatasetCatalogEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object SampleRegister(string id, string minimumEdition = "Pro") => new
    {
        id,
        title = $"Dataset {id}",
        category = "boundary",
        layerId = 1,
        geometryType = "Polygon",
        attributes = new[] { "name" },
        defaultPredicate = "intersects",
        provenance = "Natural Earth 1:110m (test)",
        attribution = "Made with Natural Earth",
        license = "Public Domain",
        minimumEdition,
    };

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_ValidDataset_ReturnsCreatedWithAttribution()
    {
        var admin = _fixture.CreateAdminClient();

        var response = await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("reg-countries")));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("id").GetString().Should().Be("reg-countries");
        doc.RootElement.GetProperty("attribution").GetString().Should().Be("Made with Natural Earth");
        doc.RootElement.GetProperty("source").GetString().Should().Be("managed");
    }

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_DuplicateId_ReturnsConflict()
    {
        var admin = _fixture.CreateAdminClient();
        (await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("dup-countries"))))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("dup-countries")));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_InvalidCategory_ReturnsBadRequest()
    {
        var admin = _fixture.CreateAdminClient();
        var payload = new { id = "bad-cat", title = "Bad", category = "nonsense", layerId = 1 };

        var response = await admin.PostAsync("/api/enrich/datasets", JsonBody(payload));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ListEnrichmentDatasets)]
    [Endpoint("GET /api/enrich/datasets")]
    public async Task List_IncludesRegisteredDataset()
    {
        var admin = _fixture.CreateAdminClient();
        await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("list-countries")));

        var response = await _fixture.Client.GetAsync("/api/enrich/datasets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var ids = doc.RootElement.GetProperty("datasets").EnumerateArray()
            .Select(d => d.GetProperty("id").GetString())
            .ToArray();
        ids.Should().Contain("list-countries");
    }

    [IntegrationTest]
    [Operation(Operations.GetEnrichmentDataset)]
    [Endpoint("GET /api/enrich/datasets/{id}")]
    public async Task Get_ReturnsRegisteredDataset()
    {
        var admin = _fixture.CreateAdminClient();
        await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("get-countries")));

        var response = await _fixture.Client.GetAsync("/api/enrich/datasets/get-countries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("id").GetString().Should().Be("get-countries");
        doc.RootElement.GetProperty("license").GetString().Should().Be("Public Domain");
    }

    [IntegrationTest]
    [Operation(Operations.GetEnrichmentDataset)]
    [Endpoint("GET /api/enrich/datasets/{id}")]
    public async Task Get_UnknownDataset_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/api/enrich/datasets/does-not-exist");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_ModifiesDataset()
    {
        var admin = _fixture.CreateAdminClient();
        await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("upd-countries")));

        var update = new { title = "Updated Title", defaultPredicate = "contains" };
        var response = await admin.PutAsync("/api/enrich/datasets/upd-countries", JsonBody(update));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Updated Title");
        doc.RootElement.GetProperty("defaultPredicate").GetString().Should().Be("contains");
    }

    [IntegrationTest]
    [Operation(Operations.UpdateEnrichmentDataset)]
    [Endpoint("PUT /api/enrich/datasets/{id}")]
    public async Task Update_UnknownDataset_ReturnsNotFound()
    {
        var admin = _fixture.CreateAdminClient();
        var response = await admin.PutAsync("/api/enrich/datasets/missing", JsonBody(new { title = "x" }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.DeregisterEnrichmentDataset)]
    [Endpoint("DELETE /api/enrich/datasets/{id}")]
    public async Task Deregister_RemovesDataset()
    {
        var admin = _fixture.CreateAdminClient();
        await admin.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("del-countries")));

        var deleteResponse = await admin.DeleteAsync("/api/enrich/datasets/del-countries");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await _fixture.Client.GetAsync("/api/enrich/datasets/del-countries");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.DeregisterEnrichmentDataset)]
    [Endpoint("DELETE /api/enrich/datasets/{id}")]
    public async Task Deregister_UnknownDataset_ReturnsNotFound()
    {
        var admin = _fixture.CreateAdminClient();
        var response = await admin.DeleteAsync("/api/enrich/datasets/missing");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.RegisterEnrichmentDataset)]
    [Endpoint("POST /api/enrich/datasets")]
    public async Task Register_WithoutAdmin_IsRejected()
    {
        var response = await _fixture.Client.PostAsync("/api/enrich/datasets", JsonBody(SampleRegister("noauth")));
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// Verifies discovery edition filtering (#2280): a Community caller does not see
/// Pro-tier managed datasets.
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class EnrichmentDatasetCatalogEditionFilterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent JsonBody(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    [IntegrationTest]
    [Operation(Operations.ListEnrichmentDatasets)]
    [Endpoint("GET /api/enrich/datasets")]
    public async Task List_CommunityEdition_HidesProDatasets()
    {
        var admin = _fixture.CreateAdminClient();
        await admin.PostAsync("/api/enrich/datasets", JsonBody(new
        {
            id = "pro-only",
            title = "Pro Only",
            category = "boundary",
            layerId = 1,
            minimumEdition = "Pro",
        }));
        await admin.PostAsync("/api/enrich/datasets", JsonBody(new
        {
            id = "community-ok",
            title = "Community OK",
            category = "boundary",
            layerId = 1,
            minimumEdition = "Community",
        }));

        var response = await _fixture.Client.GetAsync("/api/enrich/datasets");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var ids = doc.RootElement.GetProperty("datasets").EnumerateArray()
            .Select(d => d.GetProperty("id").GetString())
            .ToArray();

        ids.Should().Contain("community-ok");
        ids.Should().NotContain("pro-only");
    }
}
