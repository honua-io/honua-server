// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminLayerAuthoringEndpointsTests
{
    private const string RelationshipBatchPath = "/api/v1/admin/metadata/layers/relationships/batch";

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_CompositePair_CommitsTogetherAndRepeatsWithoutDuplicates()
    {
        var before = PrepareRelationshipBatchGraph();
        using var client = _fixture.CreateAdminClient();
        var request = CompositeBatch();
        using var content = JsonContent.Create(request, LayerAuthoringJsonContext.Default.LayerRelationshipBatchUpdateRequest);
        using var response = await client.PutAsync(RelationshipBatchPath, content);
        response.Be200Ok();
        var payload = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(),
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipBatchResponse);
        payload!.Data!.Layers.Should().HaveCount(2);
        payload.Data.Layers.Should().OnlyContain(layer => layer.Relationships.Count == 1 && layer.Relationships[0].Composite);
        var saved = _fixture.GetCurrentV2GraphSnapshot();
        saved.Graph.Revision.Should().Be(before.Graph.Revision + 1);
        var changedIds = before.PublicationsForStorageLayer(0).Concat(before.PublicationsForStorageLayer(1))
            .Where(publication => before.ResolveResource(publication)?.Type
                is MetadataV2ResourceType.FeatureDataset or MetadataV2ResourceType.Table)
            .Select(publication => publication.ResourceId).ToHashSet(StringComparer.Ordinal);
        saved.Graph.Resources.Where(resource => !changedIds.Contains(resource.Metadata.Id))
            .Should().BeEquivalentTo(before.Graph.Resources.Where(resource => !changedIds.Contains(resource.Metadata.Id)));

        using var repeatContent = JsonContent.Create(request, LayerAuthoringJsonContext.Default.LayerRelationshipBatchUpdateRequest);
        using var repeat = await client.PutAsync(RelationshipBatchPath, repeatContent);
        repeat.Be200Ok();
        _fixture.GetCurrentV2GraphSnapshot().Graph.Resources.Should().BeEquivalentTo(saved.Graph.Resources);
        foreach (var layer in request.Layers)
        {
            using var get = await client.GetAsync($"/api/v1/admin/metadata/layers/{layer.LayerId}/relationships");
            get.Be200Ok();
            var readback = await DeserializeRelationshipsAsync(get);
            readback!.Data!.Relationships.Should().ContainSingle().Which.Composite.Should().BeTrue();
            readback.Data.Relationships[0].RelatedLayerId.Should().Be(layer.LayerId == 0 ? 1 : 0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_InvalidSecondSet_DoesNotPersistFirstSet()
    {
        PrepareRelationshipBatchGraph();
        await AssertBatchRejectedWithoutMutationAsync(CompositeBatch(invalidChildField: true));
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/relationships")]
    public async Task PutRelationshipBatch_OneSidedComposite_IsRejectedThroughBothAuthoringRoutes()
    {
        PrepareRelationshipBatchGraph();
        var first = CompositeBatch().Layers[0];
        await AssertBatchRejectedWithoutMutationAsync(new LayerRelationshipBatchUpdateRequest { Layers = [first] });
        var before = _fixture.GetCurrentV2GraphSnapshot();
        using var client = _fixture.CreateAdminClient();
        using var content = JsonContent.Create(new LayerRelationshipUpdateRequest { Relationships = first.Relationships },
            LayerAuthoringJsonContext.Default.LayerRelationshipUpdateRequest);
        using var response = await client.PutAsync("/api/v1/admin/metadata/layers/0/relationships", content);
        response.Be400BadRequest();
        _fixture.GetCurrentV2GraphSnapshot().Graph.Should().BeEquivalentTo(before.Graph);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_EditableComposite_IsRejectedWithoutChangingCapabilities()
    {
        PrepareRelationshipBatchGraph(canModify: true);
        await AssertBatchRejectedWithoutMutationAsync(CompositeBatch());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_DuplicateLayer_IsRejected()
    {
        PrepareRelationshipBatchGraph();
        var first = CompositeBatch().Layers[0];
        await AssertBatchRejectedWithoutMutationAsync(new LayerRelationshipBatchUpdateRequest { Layers = [first, first] });
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_UsesStorageIdsWhenServiceLocalIdsDiffer()
    {
        var before = PrepareRelationshipBatchGraph();
        SetRelationshipBatchGraph(before.Graph with
        {
            Publications = before.Graph.Publications.Select(publication => publication.Identifier.IsNumeric
                ? publication with { Identifier = publication.Identifier with { Value = (publication.LayerIndex.GetValueOrDefault() + 100).ToString(System.Globalization.CultureInfo.InvariantCulture) } }
                : publication).ToArray(),
        });
        using var client = _fixture.CreateAdminClient();
        using var content = JsonContent.Create(CompositeBatch(), LayerAuthoringJsonContext.Default.LayerRelationshipBatchUpdateRequest);
        using var response = await client.PutAsync(RelationshipBatchPath, content);
        response.Be200Ok();
        var payload = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(),
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipBatchResponse);
        payload!.Data!.Layers.Single(layer => layer.LayerId == 0).Relationships[0].RelatedLayerId.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_RetiredTarget_IsRejectedWithoutPartialMapping()
    {
        var before = PrepareRelationshipBatchGraph();
        var childId = before.PublicationsForStorageLayer(1).First().ResourceId;
        SetRelationshipBatchGraph(before.Graph with
        {
            Resources = before.Graph.Resources.Select(resource => resource.Metadata.Id == childId
                ? resource with { Status = resource.Status with { Lifecycle = MetadataV2LifecycleStatus.Retired } }
                : resource).ToArray(),
        });
        await AssertBatchRejectedWithoutMutationAsync(CompositeBatch());
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/relationships/batch")]
    public async Task PutRelationshipBatch_AmbiguousStorageHandle_IsRejected()
    {
        var before = PrepareRelationshipBatchGraph();
        SetRelationshipBatchGraph(before.Graph with
        {
            StorageBindings = before.Graph.StorageBindings.Select(binding => binding.StorageLayerId == 1
                ? binding with { StorageLayerId = 0 }
                : binding).ToArray(),
        });
        await AssertBatchRejectedWithoutMutationAsync(CompositeBatch());
    }

    private MetadataV2GraphSnapshot PrepareRelationshipBatchGraph(bool canModify = false)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resourceIds = snapshot.PublicationsForStorageLayer(0).Concat(snapshot.PublicationsForStorageLayer(1))
            .Where(publication => snapshot.ResolveResource(publication)?.Type
                is MetadataV2ResourceType.FeatureDataset or MetadataV2ResourceType.Table)
            .Select(publication => publication.ResourceId).ToHashSet(StringComparer.Ordinal);
        resourceIds.Should().HaveCount(2);
        SetRelationshipBatchGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(resource => resourceIds.Contains(resource.Metadata.Id)
                ? resource with { Editing = new MetadataV2ResourceEditing { CanModify = canModify }, Relationships = [] }
                : resource).ToArray(),
        });
        return _fixture.GetCurrentV2GraphSnapshot();
    }

    private void SetRelationshipBatchGraph(MetadataV2Graph graph) =>
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(graph, schema: _fixture.MetadataGraphSchema);

    private async Task AssertBatchRejectedWithoutMutationAsync(LayerRelationshipBatchUpdateRequest request)
    {
        var before = _fixture.GetCurrentV2GraphSnapshot();
        using var client = _fixture.CreateAdminClient();
        using var content = JsonContent.Create(request, LayerAuthoringJsonContext.Default.LayerRelationshipBatchUpdateRequest);
        using var response = await client.PutAsync(RelationshipBatchPath, content);
        response.Be400BadRequest();
        var after = _fixture.GetCurrentV2GraphSnapshot();
        after.Etag.Should().Be(before.Etag);
        after.Graph.Should().BeEquivalentTo(before.Graph);
    }

    private static LayerRelationshipBatchUpdateRequest CompositeBatch(bool invalidChildField = false) => new()
    {
        Layers =
        [
            new LayerRelationshipBatchUpdateItem
            {
                LayerId = 0,
                Relationships = [new LayerRelationshipUpdateItem
                {
                    Id = "reviewed-parent", Name = "Reviewed summary", RelatedLayerId = 1,
                    OriginField = "objectid", DestinationField = "related_id", Role = "origin",
                    Composite = true, EsriRelationshipId = 7,
                }],
            },
            new LayerRelationshipBatchUpdateItem
            {
                LayerId = 1,
                Relationships = [new LayerRelationshipUpdateItem
                {
                    Id = "reviewed-child", Name = "Reviewed parent", RelatedLayerId = 0,
                    OriginField = invalidChildField ? "missing_field" : "related_id", DestinationField = "objectid",
                    Role = "destination", Composite = true, EsriRelationshipId = 7,
                }],
            },
        ],
    };
}
