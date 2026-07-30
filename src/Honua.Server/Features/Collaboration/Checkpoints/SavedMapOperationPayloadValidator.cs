// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Admission validation for saved-map operation payloads (honua-server#2999 review). Mirrors the
/// shape requirements <see cref="SavedMapOperationDraftApplier"/> enforces at checkpoint time, so
/// a payload the applier could never express is rejected at the door instead of being accepted,
/// assigned a permanent cursor, and broadcast.
/// </summary>
/// <remarks>
/// <para>
/// Why admission-time validation matters: the op-log cursor advances irrevocably on accept. A
/// malformed-but-checkpointable-kind operation would therefore make every later checkpoint fail
/// with 422 while it is retained, and fail the continuity guard with 409 once it is pruned —
/// wedging the map so it can never save another version. This validator and the applier must
/// stay in lockstep, exactly like <see cref="SavedMapOperationDraftApplier.IsCheckpointable"/>.
/// </para>
/// <para>
/// Scope is deliberately limited to payload SHAPE, not referenced draft state (for example
/// whether <c>layerId</c> currently exists in the composition). Draft state cannot be validated
/// meaningfully at admission time: the Studio draft body reflects only operations already
/// applied by a checkpoint, so it lags the op log. An operation targeting a layer that an
/// earlier, not-yet-checkpointed <c>ReplaceWebMapDocument</c> removed would still pass a
/// draft-state check and then fail at checkpoint — and conversely, a layer absent from the draft
/// may be introduced by an earlier pending operation. A missing target at checkpoint time is a
/// genuine state conflict that correctly surfaces as 409; silently skipping such operations
/// would be data loss.
/// </para>
/// </remarks>
internal static class SavedMapOperationPayloadValidator
{
    /// <summary>
    /// Validates that <paramref name="payload"/> carries everything the checkpoint applier
    /// requires for <paramref name="kind"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the payload is applicable.</returns>
    public static bool TryValidate(SavedMapOperationKind kind, JsonElement payload, out string error)
    {
        switch (kind)
        {
            case SavedMapOperationKind.SetViewport:
                return TryValidateViewport(payload, out error);

            case SavedMapOperationKind.SetLayerVisibility:
                if (!TryValidateRequiredString(payload, "layerId", out error))
                {
                    return false;
                }

                if (payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("visible", out var visible) ||
                    visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "The layer-visibility payload requires a boolean 'visible'.";
                    return false;
                }

                error = string.Empty;
                return true;

            case SavedMapOperationKind.ReorderLayers:
                return TryValidateReorder(payload, out error);

            case SavedMapOperationKind.PatchStyle:
                return TryValidatePatchStyle(payload, out error);

            case SavedMapOperationKind.ReplaceWebMapDocument:
                return TryValidateReplacementDocument(payload, out error);

            default:
                // Unreachable in practice: the endpoint gates on IsCheckpointable first. Fail
                // closed rather than admitting a kind this validator does not model.
                error = $"Operation kind '{kind}' cannot be applied to a saved-map checkpoint.";
                return false;
        }
    }

    /// <summary>
    /// Validates a whole-document replacement as an actual composition body, not merely as "some
    /// object".
    /// </summary>
    /// <remarks>
    /// A shape check alone admitted documents that no later operation can survive, and admission
    /// permanently consumes a cursor. <c>{"layers":[{}]}</c> deserializes no further than the
    /// <c>required</c> <see cref="StudioCompositionLayer.Id"/>, so the next scalar operation makes
    /// <c>StudioCompositionBodyEditor.ReadBody</c> throw; duplicate layer ids deserialize fine but
    /// make the reorder applier's <c>ToDictionary</c> throw an unmapped
    /// <see cref="ArgumentException"/>. Either way every subsequent checkpoint fails against a
    /// replacement cursor that was already persisted, and the map can never save again — the same
    /// wedge class as the whitespace-id and non-string-styleRef fixes (honua-server#2999 review).
    /// </remarks>
    private static bool TryValidateReplacementDocument(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The document-replace payload must be an object body.";
            return false;
        }

        StudioCompositionBody? body;
        try
        {
            body = payload.Deserialize(StudioJsonContext.Default.StudioCompositionBody);
        }
        catch (JsonException ex)
        {
            error = $"The document-replace payload is not a valid Studio composition body: {ex.Message}";
            return false;
        }

        if (body is null)
        {
            error = "The document-replace payload is required.";
            return false;
        }

        // Enforced here because the reorder applier indexes layers by id and a duplicate throws
        // out of ToDictionary rather than surfacing as a typed rejection.
        //
        // `?? []` is load-bearing, not defensive habit: deserializing a body that omits "layers"
        // (for example the legal `{}`) leaves the property NULL rather than applying its
        // Array.Empty initializer, because the source-generated converter assigns only members
        // present in the payload. Iterating it directly throws NullReferenceException.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in body.Layers ?? [])
        {
            if (string.IsNullOrWhiteSpace(layer.Id))
            {
                error = "Every layer in the document-replace payload requires a non-empty 'id'.";
                return false;
            }

            if (!seen.Add(layer.Id))
            {
                error = $"The document-replace payload repeats layer id '{layer.Id}'; layer ids must be unique.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateViewport(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The viewport payload is required.";
            return false;
        }

        StudioCompositionView? view;
        try
        {
            view = payload.Deserialize(StudioJsonContext.Default.StudioCompositionView);
        }
        catch (JsonException ex)
        {
            error = $"The viewport payload is not a valid composition view: {ex.Message}";
            return false;
        }

        if (view is null)
        {
            error = "The viewport payload is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a style patch: a non-empty <c>layerId</c>, plus a <c>styleRef</c> that is either
    /// omitted/null (an intentional clear) or a string.
    /// </summary>
    /// <remarks>
    /// The kind check matters because the applier can only express a string or null style
    /// reference. Admitting <c>{"layerId":"parcels","styleRef":42}</c> would consume a permanent
    /// cursor and then CLEAR the layer's existing style at checkpoint time, turning malformed
    /// input into a destructive edit instead of a 400 (honua-server#2999 review).
    /// </remarks>
    private static bool TryValidatePatchStyle(JsonElement payload, out string error)
    {
        if (!TryValidateRequiredString(payload, "layerId", out error))
        {
            return false;
        }

        if (payload.TryGetProperty("styleRef", out var styleRef) &&
            styleRef.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            error = "The style payload requires 'styleRef' to be a string, or null to clear it.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateReorder(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("layerIds", out var ids) ||
            ids.ValueKind != JsonValueKind.Array)
        {
            error = "The reorder payload requires a 'layerIds' array.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids.EnumerateArray())
        {
            // Whitespace-only entries are rejected for the same reason as a whitespace 'layerId':
            // no composition layer can carry such an id, so the entry could only ever fail at
            // checkpoint time after permanently consuming a cursor.
            if (id.ValueKind != JsonValueKind.String ||
                id.GetString() is not { } value ||
                string.IsNullOrWhiteSpace(value))
            {
                error = "Every entry in 'layerIds' must be a non-empty string.";
                return false;
            }

            // A repeated id has no single meaning as an ordering, and the applier would have to
            // silently drop one occurrence — the same class of silent no-op as an unknown id.
            if (!seen.Add(value))
            {
                error = "Every entry in 'layerIds' must be unique.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a required identifier string. Whitespace-only values are rejected, not just empty
    /// ones: the shared Studio composition editor guards its layer-id arguments with
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>, so a payload such as
    /// <c>{"layerId":" ","styleRef":"roads"}</c> would pass a length-only check, take a permanent
    /// op-log cursor, and then throw an unmapped <see cref="ArgumentException"/> from every later
    /// checkpoint — a 500 while the operation is retained and a continuity 409 once it is pruned,
    /// i.e. a permanently unsaveable map (honua-server#2999 review).
    /// </summary>
    private static bool TryValidateRequiredString(JsonElement payload, string property, out string error)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            error = string.Empty;
            return true;
        }

        error = $"The payload requires a non-empty '{property}'.";
        return false;
    }
}
