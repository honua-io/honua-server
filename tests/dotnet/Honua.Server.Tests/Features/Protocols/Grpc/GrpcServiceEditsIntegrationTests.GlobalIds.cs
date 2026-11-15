// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcServiceEditsIntegrationTests
{
    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyServiceEdits")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/ApplyServiceEdits")]
    public async Task ApplyServiceEdits_GlobalIdSelectors_UpdateAndDeleteAcrossLayers()
    {
        await ConfigureGlobalIdsAsync();
        var add = Request();
        foreach (var layer in add.Edits)
        {
            layer.UseGlobalIds = true;
            layer.Adds[0].GlobalId = Guid.NewGuid().ToString("D");
        }

        var added = await _client!.ApplyServiceEditsAsync(add, _headers);
        added.Error.Should().BeNull();
        added.Results.Select(layer => layer.Results.AddResults.Single().GlobalId)
            .Should().Equal(add.Edits.Select(layer => layer.Adds[0].GlobalId));
        var edit = new Proto.ApplyServiceEditsRequest { ServiceId = WebAppFixture.TestServiceId, RollbackOnFailure = true };
        edit.Edits.Add(new Proto.ServiceLayerEdits
        {
            LayerId = 0, UseGlobalIds = true,
            Updates = { new Proto.Feature { GlobalId = add.Edits[0].Adds[0].GlobalId.ToUpperInvariant(),
                Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "global ID update" } } } }
        });
        edit.Edits.Add(new Proto.ServiceLayerEdits { LayerId = 1, UseGlobalIds = true, DeleteGlobalIds = { add.Edits[1].Adds[0].GlobalId } });

        var changed = await _client.ApplyServiceEditsAsync(edit, _headers);

        changed.Error.Should().BeNull();
        changed.Results[0].Results.UpdateResults.Should().ContainSingle().Which.GlobalId.Should().Be(add.Edits[0].Adds[0].GlobalId);
        changed.Results[1].Results.DeleteResults.Should().ContainSingle().Which.GlobalId.Should().Be(add.Edits[1].Adds[0].GlobalId);
        (await _fixture.ReadStoredFeatureNameAsync(0, added.Results[0].Results.AddResults[0].ObjectId)).Should().Be("global ID update");
        (await _fixture.CountStoredFeaturesByNameAsync(1, "service edit")).Should().Be(0);
    }

    private async Task ConfigureGlobalIdsAsync()
    {
        foreach (var layerId in new[] { 0, 1 })
        {
            await _fixture.UpdateV2ResourceAsync(layerId, resource => resource with
            {
                Editing = (resource.Editing ?? new MetadataV2ResourceEditing()) with { GlobalIdField = "globalid" },
                SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = "globalid", Type = MetadataV2FieldType.Uuid, Nullable = true }]
            });
        }
    }
}
