// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Checkpoints;
using Xunit;

namespace Honua.Server.Tests.Features.Collaboration.Checkpoints;

/// <summary>
/// The admission validator must accept exactly what the checkpoint applier can express: anything
/// it lets through would otherwise take a permanent op-log cursor and wedge every later
/// checkpoint (honua-server#2999 review).
/// </summary>
public sealed class SavedMapOperationPayloadValidatorTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":8}""")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"layerId":"parcels","visible":true}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":["a","b"]}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":[]}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels"}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":"night"}""")]
    // An explicit null clears the style; that is the ONLY non-string form the applier can express.
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":null}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[]}""")]
    public void TryValidate_ApplicablePayload_Accepts(SavedMapOperationKind kind, string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(kind, Parse(payload), out var error)
            .Should().BeTrue(error);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"visible":true}""")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"layerId":"parcels"}""")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"layerId":"","visible":true}""")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"layerId":"parcels","visible":"yes"}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":"parcels"}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":[1]}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":[""]}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":["a","a"]}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"styleRef":"night"}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":42}""")]
    // A present non-string styleRef used to be admitted and then silently CLEARED the layer's
    // style at checkpoint time, turning malformed input into a destructive edit.
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":42}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":{"id":"x"}}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":["night"]}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":true}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """[]""")]
    [InlineData(SavedMapOperationKind.SetViewport, """[]""")]
    // Not checkpointable at all: the endpoint gates on IsCheckpointable first, but the validator
    // fails closed rather than modelling it as applicable.
    [InlineData(SavedMapOperationKind.SetMetadataField, """{"title":"x"}""")]
    public void TryValidate_PayloadTheApplierWouldReject_IsRejectedWithReason(
        SavedMapOperationKind kind,
        string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(kind, Parse(payload), out var error)
            .Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
