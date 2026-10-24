// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The admin layer configuration PUTs (filter, fields, popup-info, drawing-info) address a layer
/// by its service-local layer id. When two services publish different resources at the same id,
/// the endpoints must refuse the ambiguous id instead of validating one resource and writing to
/// every resource that shares the id (honua-server#6111).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AdminLayerSharedLayerIndexTests : IAsyncLifetime
{
    private const int LayerId = WebAppFixture.TestLayerId;
    private const string TwinServiceId = "svc-twin-6111";
    private const string TwinServiceName = "twin-service-6111";
    private const string TwinResourceId = "res-twin-6111";
    private const string TwinBindingId = "bind-twin-6111";
    private const string TwinPublicationId = "pub-twin-6111";
    private const int TwinStorageLayerId = 961110;

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/filter")]
    public async Task UpdateLayerFilter_LayerIdSharedByTwoResources_Returns409AndChangesNeither()
    {
        var request = new LayerFilterConfigurationUpdateRequest { PermanentFilter = null };
        using var content = JsonContent.Create(
            request, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest);

        await AssertAmbiguousPutRejectedAsync($"/api/v1/admin/metadata/layers/{LayerId}/filter", content);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/fields")]
    public async Task UpdateLayerFields_LayerIdSharedByTwoResources_Returns409AndChangesNeither()
    {
        var request = new LayerFieldConfigurationUpdateRequest
        {
            Fields = [new LayerFieldConfigurationUpdateItem { Name = "description", Hidden = false }],
        };
        using var content = JsonContent.Create(
            request, LayerFieldConfigurationJsonContext.Default.LayerFieldConfigurationUpdateRequest);

        await AssertAmbiguousPutRejectedAsync($"/api/v1/admin/metadata/layers/{LayerId}/fields", content);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/popup-info")]
    public async Task SetPopupInfo_LayerIdSharedByTwoResources_Returns409AndChangesNeither()
    {
        var document = JsonSerializer.Deserialize<JsonElement>("""{ "title": "{name}" }""");
        using var content = JsonContent.Create(document, LayerAuthoringJsonContext.Default.JsonElement);

        await AssertAmbiguousPutRejectedAsync($"/api/v1/admin/metadata/layers/{LayerId}/popup-info", content);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    public async Task SetDrawingInfo_LayerIdSharedByTwoResources_Returns409AndChangesNeither()
    {
        var document = JsonSerializer.Deserialize<JsonElement>("""{ "renderer": { "type": "simple" } }""");
        using var content = JsonContent.Create(document, LayerAuthoringJsonContext.Default.JsonElement);

        await AssertAmbiguousPutRejectedAsync($"/api/v1/admin/metadata/layers/{LayerId}/drawing-info", content);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/filter")]
    public async Task UpdateLayerFilter_RetiredTwinAtSameLayerId_WritesOnlyTheResolvedResource()
    {
        var (before, targetResourceId) = AddTwinAtLayerId(retireTwin: true);
        var request = new LayerFilterConfigurationUpdateRequest
        {
            PermanentFilter = new LayerPermanentFilterConfiguration
            {
                Expression = "category = 'test'",
                Language = "arcgis-sql",
            },
        };
        using var content = JsonContent.Create(
            request, LayerFieldConfigurationJsonContext.Default.LayerFilterConfigurationUpdateRequest);
        using var client = _fixture.CreateAdminClient();

        using var response = await client.PutAsync($"/api/v1/admin/metadata/layers/{LayerId}/filter", content);

        response.Be200Ok();
        var after = _fixture.GetCurrentV2GraphSnapshot();
        after.Index.ResourcesById[targetResourceId].PermanentFilter!.Expression.Should().Be("category = 'test'");
        after.Index.ResourcesById[TwinResourceId].Should().BeEquivalentTo(before.Index.ResourcesById[TwinResourceId]);
    }

    private async Task AssertAmbiguousPutRejectedAsync(string path, HttpContent content)
    {
        var (before, targetResourceId) = AddTwinAtLayerId(retireTwin: false);
        var targetServiceName = before.Index.ServicesById[
            before.Graph.Publications.First(publication => publication.ResourceId == targetResourceId).ServiceId].Metadata.Name;
        using var client = _fixture.CreateAdminClient();

        using var response = await client.PutAsync(path, content);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain(TwinServiceName).And.Contain(targetServiceName);
        var after = _fixture.GetCurrentV2GraphSnapshot();
        after.Etag.Should().Be(before.Etag);
        after.Graph.Resources.Should().BeEquivalentTo(before.Graph.Resources);
    }

    /// <summary>
    /// Publishes a second, unrelated resource at <see cref="LayerId"/> through its own service: a
    /// clone of the seeded layer's resource (same schema, so the request validates against both)
    /// carrying its own storage binding and a distinct permanent filter / hidden field.
    /// </summary>
    private (MetadataV2GraphSnapshot Before, string TargetResourceId) AddTwinAtLayerId(bool retireTwin)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(candidate =>
            candidate.LayerIndex == LayerId
            && snapshot.IsRoutable(candidate)
            && snapshot.ResolveStorageLayerId(candidate).HasValue);
        var resource = snapshot.ResolveResource(publication)!;
        var service = snapshot.Index.ServicesById[publication.ServiceId];
        var binding = snapshot.ResolveStorageBinding(publication)!;

        var twinBinding = binding with
        {
            Metadata = binding.Metadata with { Id = TwinBindingId, Name = TwinBindingId },
            ResourceId = TwinResourceId,
            StorageLayerId = TwinStorageLayerId,
        };
        var twinResource = resource with
        {
            Metadata = resource.Metadata with { Id = TwinResourceId, Name = TwinResourceId },
            StorageBindingIds = [TwinBindingId],
            PrimaryStorageBindingId = TwinBindingId,
            PermanentFilter = new MetadataV2PermanentFilter { Expression = "category = 'twin'", Language = "arcgis-sql" },
            SchemaFields = resource.SchemaFields
                .Select(field => field.Name == "description" ? field with { Hidden = true } : field)
                .ToArray(),
            Status = retireTwin
                ? resource.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired }
                : resource.Status,
        };
        var twinService = service with
        {
            Metadata = service.Metadata with { Id = TwinServiceId, Name = TwinServiceName },
            Route = TwinServiceName,
        };
        var twinPublication = publication with
        {
            Metadata = publication.Metadata with { Id = TwinPublicationId, Name = TwinPublicationId },
            ServiceId = TwinServiceId,
            ResourceId = TwinResourceId,
            StorageBindingId = publication.StorageBindingId is null ? null : TwinBindingId,
        };

        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(
            snapshot.Graph with
            {
                Revision = snapshot.Graph.Revision + 1,
                Resources = [.. snapshot.Graph.Resources, twinResource],
                StorageBindings = [.. snapshot.Graph.StorageBindings, twinBinding],
                Services = [.. snapshot.Graph.Services, twinService],
                Publications = [.. snapshot.Graph.Publications, twinPublication],
            },
            schema: _fixture.MetadataGraphSchema);
        return (_fixture.GetCurrentV2GraphSnapshot(), resource.Metadata.Id);
    }
}
