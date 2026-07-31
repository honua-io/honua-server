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
    // A genuine composition body must still be accepted: the replacement check deserializes it,
    // so over-rejecting here would break the primary document-replace path.
    [InlineData(
        SavedMapOperationKind.ReplaceWebMapDocument,
        """{"layers":[{"id":"parcels","type":"fill","visible":true},{"id":"roads","type":"line"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"parcels"}],"view":{"zoom":8}}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{}""")]
    // Correctly-shaped coordinate arrays and unique widget ids must still pass, so the arity and
    // widget checks cannot become blanket rejections of valid App/view documents.
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"widgets":[{"id":"legend","kind":"legend"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"bbox":[0,0,10,10],"center":[5,5]}}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"bbox":[0,0,10,10],"center":[5,5],"zoom":8}""")]
    // The shared zoom/pitch bounds are INCLUSIVE, so the extremes must stay usable.
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":0,"pitch":0}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":24,"pitch":85}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"zoom":24,"pitch":85}}""")]
    // Bearing is deliberately unbounded by the shared contract; it must not be over-rejected.
    [InlineData(SavedMapOperationKind.SetViewport, """{"bearing":540}""")]
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
    // Whitespace-only identifiers used to pass the length-only check, take a permanent cursor, and
    // then throw an UNMAPPED ArgumentException out of StudioCompositionBodyEditor at checkpoint
    // time — every later checkpoint 500s while the operation is retained, then fails the
    // continuity guard once it is pruned (honua-server#2999 review).
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":" ","styleRef":"roads"}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"\t","styleRef":null}""")]
    [InlineData(SavedMapOperationKind.SetLayerVisibility, """{"layerId":"  ","visible":true}""")]
    [InlineData(SavedMapOperationKind.ReorderLayers, """{"layerIds":["a"," "]}""")]
    // A present non-string styleRef used to be admitted and then silently CLEARED the layer's
    // style at checkpoint time, turning malformed input into a destructive edit.
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":42}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":{"id":"x"}}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":["night"]}""")]
    [InlineData(SavedMapOperationKind.PatchStyle, """{"layerId":"parcels","styleRef":true}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """[]""")]
    // A whole-document replacement used to pass on shape alone. These three all took a permanent
    // cursor and then wedged every later checkpoint: a layer missing the required 'id' stops
    // deserialization dead in ReadBody, and duplicate ids throw an unmapped ArgumentException out
    // of the reorder applier's ToDictionary (honua-server#2999 review).
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"parcels"},{"id":"parcels"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":" "}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"parcels","visible":"yes"}]}""")]
    // Widgets carry the same invariants as layers: an App composition is indexed by widget id, so a
    // duplicate or blank one wedges the next widget operation.
    [InlineData(
        SavedMapOperationKind.ReplaceWebMapDocument,
        """{"widgets":[{"id":"legend","kind":"a"},{"id":"legend","kind":"b"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"widgets":[{"id":" ","kind":"a"}]}""")]
    // IReadOnlyList<double> deserializes any-length arrays, so the wire model cannot express that
    // bbox is 4 ordinates and center is 2. Both used to take a permanent cursor and fail later.
    [InlineData(SavedMapOperationKind.SetViewport, """{"bbox":[0]}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"center":[1,2,3]}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"center":[1]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"bbox":[0,1,2]}}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """[]""")]
    // Same class of gap on the scalars: double? accepts any magnitude, so {"zoom":25} used to
    // reach the unconditional success path and consume a cursor even though the shared Studio view
    // contract (StudioCompositionViewBounds, which the MCP tool schemas also advertise) caps zoom
    // at 24 and pitch at 85 (honua-server#2999 review).
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":25}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":-1}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"pitch":90}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"pitch":-0.5}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"zoom":25}}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"pitch":90}}""")]
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

    /// <summary>
    /// The rejection reason is returned verbatim in the append endpoint's 400, so it must be a
    /// stable validation message. <c>JsonException.Message</c> carries CLR type names, JSON paths
    /// and byte offsets, which the shared error-handling rule forbids leaking to clients
    /// (honua-server#2999 review).
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"bbox":"invalid"}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":"far"}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":"parcels"}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"center":"here"}}""")]
    public void TryValidate_MalformedPayload_ReturnsStableErrorWithoutExceptionDetail(
        SavedMapOperationKind kind,
        string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(kind, Parse(payload), out var error)
            .Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        error.Should().NotContainAny(
            "System.",
            "Honua.",
            "$.",
            "LineNumber",
            "BytePositionInLine",
            "Path:");
    }

    /// <summary>
    /// System.Text.Json ignores properties it cannot bind, so a misspelled member produced an
    /// empty composition that passed admission and then cleared the document at checkpoint time
    /// (honua-server#2999 review).
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"layres":[{"id":"parcels"}]}""")]
    [InlineData("""{"layers":[],"veiw":{"zoom":3}}""")]
    [InlineData("""{"widget":[{"id":"legend","kind":"legend"}]}""")]
    public void TryValidate_ReplacementWithUnmappedMember_IsRejected(string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(
            SavedMapOperationKind.ReplaceWebMapDocument, Parse(payload), out var error)
            .Should().BeFalse();

        error.Should().Contain("unrecognized member");
    }

    /// <summary>
    /// C# <c>required</c> only demands the JSON member be present; it does not make the value
    /// non-null or non-blank (honua-server#2999 review).
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"widgets":[{"id":"legend","kind":null}]}""")]
    [InlineData("""{"widgets":[{"id":"legend","kind":"   "}]}""")]
    [InlineData("""{"widgets":[{"id":"legend","kind":""}]}""")]
    public void TryValidate_ReplacementWidgetWithBlankKind_IsRejected(string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(
            SavedMapOperationKind.ReplaceWebMapDocument, Parse(payload), out var error)
            .Should().BeFalse();

        error.Should().Contain("kind");
    }

    /// <summary>
    /// Deserialization is permissive at every level, not just the payload root, so a typo
    /// inside layers/widgets/view silently applied a default (honua-server#2999 review).
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"roads","visble":false}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"widgets":[{"id":"legend","kind":"legend","titel":"x"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"view":{"zom":12}}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zom":12}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":12,"pich":30}""")]
    public void TryValidate_UnmappedNestedMember_IsRejected(SavedMapOperationKind kind, string payload)
    {
        SavedMapOperationPayloadValidator.TryValidate(kind, Parse(payload), out var error)
            .Should().BeFalse();

        error.Should().Contain("unrecognized member");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"roads","visible":false,"styleRef":"night"}]}""")]
    [InlineData(SavedMapOperationKind.ReplaceWebMapDocument, """{"widgets":[{"id":"legend","kind":"legend","title":"Legend"}]}""")]
    [InlineData(SavedMapOperationKind.SetViewport, """{"zoom":12,"pitch":30,"crs":"EPSG:3857"}""")]
    public void TryValidate_FullyMappedNestedMembers_AreAccepted(SavedMapOperationKind kind, string payload)
    {
        // The allowlists must cover every modelled member, or a legitimate client is refused.
        SavedMapOperationPayloadValidator.TryValidate(kind, Parse(payload), out var error)
            .Should().BeTrue(because: error);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
