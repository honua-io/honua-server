// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Console;

public sealed partial class PublishedContentProjectionTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    public async Task GetContent_DatasetFilterAndDetail_ReturnCanonicalDatasetMetadata()
    {
        using var listed = await ReadContentAsync("?itemType=dataset");
        Assert.Equal(1, listed.RootElement.GetProperty("total").GetInt32());
        var dataset = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("resource", dataset.GetProperty("id").GetString());
        Assert.Equal("dataset", dataset.GetProperty("itemType").GetString());
        Assert.Equal("Parcels", dataset.GetProperty("title").GetString());
        Assert.Equal(["view"], dataset.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        var metadata = dataset.GetProperty("typeMetadata");
        Assert.Equal("resource", metadata.GetProperty("datasetId").GetString());
        Assert.Equal("feature-type", metadata.GetProperty("kind").GetString());
        Assert.Equal(0, metadata.GetProperty("fieldCount").GetInt32());
        Assert.False(metadata.TryGetProperty("connectionId", out _));
        using var layers = await ReadContentAsync("?itemType=layer");
        var layerMetadata = Assert.Single(layers.RootElement.GetProperty("items").EnumerateArray()).GetProperty("typeMetadata");
        Assert.Equal("resource", layerMetadata.GetProperty("datasetId").GetString());
        Assert.False(layerMetadata.TryGetProperty("resourceId", out _));
        using var response = await _admin.GetAsync("/api/v1/console/content/resource");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(dataset.GetRawText(), detail.RootElement.GetProperty("data").GetRawText());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_DatasetWithMultipleServices_ListsOneDataset()
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = [graph.Services[0], graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Id = "service-b", Name = "other" },
                PublicName = "Other"
            }],
            Publications = [graph.Publications[0], graph.Publications[0] with
            {
                Metadata = graph.Publications[0].Metadata with { Id = "publication-b" },
                ServiceId = "service-b"
            }]
        });
        using var listed = await ReadContentAsync("?itemType=dataset");
        Assert.Equal(1, listed.RootElement.GetProperty("total").GetInt32());
        Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
    }

    [IntegrationTheory]
    [InlineData(MetadataV2ObjectType.FeatureDataset, "feature-type")]
    [InlineData(MetadataV2ObjectType.Table, "feature-type")]
    [InlineData(MetadataV2ObjectType.RasterDataset, "coverage")]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_DatasetKinds_UseTheCanonicalKind(MetadataV2ObjectType objectType, string kind)
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Objects = [graph.Objects[0] with { Type = objectType }] });
        using var listed = await ReadContentAsync("?itemType=dataset");
        var dataset = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(kind, dataset.GetProperty("typeMetadata").GetProperty("kind").GetString());
    }

    [IntegrationTheory]
    [InlineData(MetadataV2ObjectType.Map)]
    [InlineData(MetadataV2ObjectType.Style)]
    [InlineData(MetadataV2ObjectType.Unknown)]
    [InlineData(MetadataV2ObjectType.TileDataset)]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_UnclassifiedObject_DoesNotListAsDataset(MetadataV2ObjectType objectType)
    {
        var graph = Graph();
        _provider.SetGraph(graph with { Objects = [graph.Objects[0] with { Type = objectType }] });
        using var listed = await ReadContentAsync("?itemType=dataset");
        Assert.Equal(0, listed.RootElement.GetProperty("total").GetInt32());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    public async Task GetContent_UnreadableDatasetIdentity_CannotBeShadowedByAuthoredContent()
    {
        var store = _fixture.Services.GetRequiredService<IAuthoredConsoleContent>();
        await store.CreateAsync(new()
        {
            Id = "resource", Name = "shadow", ItemType = ConsoleContentItemType.Layer,
            Visibility = ConsoleVisibility.Public
        });
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Objects = [graph.Objects[0] with { AccessPolicy = new() { AllowedRoles = ["survey-reader"] } }]
        });
        using var listed = await ReadContentAsync();
        Assert.Equal(0, listed.RootElement.GetProperty("total").GetInt32());
        using var detail = await _admin.GetAsync("/api/v1/console/content/resource");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/search")]
    [Endpoint("DELETE /api/v1/console/content/{id}")]
    public async Task GetContent_DatasetSearchAndDelete_PreservesReadOnlyIdentity()
    {
        using var found = await ReadContentAsync("?q=Parcels&itemType=dataset");
        Assert.Equal(1, found.RootElement.GetProperty("total").GetInt32());
        using var searched = await ReadContentAsync("/search?q=Parcels");
        Assert.Contains(searched.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "resource"
                && item.GetProperty("itemType").GetString() == "dataset");
        using var deleted = await _admin.DeleteAsync("/api/v1/console/content/resource");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        using var remaining = await ReadContentAsync("?itemType=dataset");
        Assert.Single(remaining.RootElement.GetProperty("items").EnumerateArray());
    }

    [IntegrationTheory]
    [InlineData("saved-map")]
    [InlineData("generated-app")]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_RetiredContentKind_ReturnsBadRequest(string itemType)
    {
        using var response = await _admin.GetAsync("/api/v1/console/content?itemType=" + itemType);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTheory]
    [InlineData("service")]
    [InlineData("publication")]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_DatasetIdCollision_PreservesServiceAndLayerIdentity(string datasetId)
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Objects = [graph.Objects[0] with { Metadata = graph.Objects[0].Metadata with { Id = datasetId } }],
            Publications = [graph.Publications[0] with { ResourceId = datasetId }]
        });
        using var listed = await ReadContentAsync();
        var items = listed.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.GetProperty("itemType").GetString() == "service");
        Assert.Contains(items, item => item.GetProperty("itemType").GetString() == "layer");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    public async Task GetContent_DatasetAcrossAccessTiers_UsesMostPublicAuthorizedExposure()
    {
        var graph = Graph();
        _provider.SetGraph(graph with
        {
            Services = [graph.Services[0] with { AccessPolicy = new() }, graph.Services[0] with
            {
                Metadata = graph.Services[0].Metadata with { Id = "service-b", Name = "other" },
                PublicName = "Other"
            }],
            Publications = [graph.Publications[0], graph.Publications[0] with
            {
                Metadata = graph.Publications[0].Metadata with { Id = "publication-b" },
                ServiceId = "service-b"
            }]
        });
        using var listed = await ReadContentAsync("?itemType=dataset");
        var dataset = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("public", dataset.GetProperty("visibility").GetString());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/console/content")]
    [Endpoint("DELETE /api/v1/console/content/{id}")]
    [Endpoint("GET /api/v1/console/session")]
    [Endpoint("POST /api/v1/console/actions/check")]
    public async Task GetContent_PublishedIdentity_DoesNotCreateMutableContentRows()
    {
        using var listed = await ReadContentAsync("?itemType=service");
        Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        using var removed = await _admin.DeleteAsync("/api/v1/console/content/publication");
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        using var stillPublished = await ReadContentAsync("?itemType=layer");
        var item = Assert.Single(stillPublished.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(["view"], item.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        using var sessionResponse = await _admin.GetAsync("/api/v1/console/session");
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        using var session = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, session.RootElement.GetProperty("data").GetProperty("content").GetProperty("total").GetInt32());
        using var actionResponse = await _admin.PostAsJsonAsync("/api/v1/console/actions/check", new ConsoleActionCheckRequest
        {
            Targets = [new ConsoleActionCheckTarget { ItemId = "publication" }],
            Actions = [ConsoleContentAction.View, ConsoleContentAction.Edit]
        });
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);
        using var actions = JsonDocument.Parse(await actionResponse.Content.ReadAsStringAsync());
        var result = Assert.Single(actions.RootElement.GetProperty("data").GetProperty("results").EnumerateArray());
        Assert.Equal(["view"], result.GetProperty("allowed").EnumerateArray().Select(action => action.GetString()));
        Assert.Equal(["edit"], result.GetProperty("denied").EnumerateArray().Select(action => action.GetString()));
    }
}
