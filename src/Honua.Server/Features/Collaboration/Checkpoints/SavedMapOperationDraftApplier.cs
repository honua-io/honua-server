// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;

namespace Honua.Server.Features.Collaboration.Checkpoints;

/// <summary>
/// Applies committed saved-map operation-log operations onto a Studio package envelope's
/// composition body (honua-server#2999, REQ-003). Every supported operation family is
/// absolute-state (set-view, set-visibility, reorder, style-ref, whole-document replace), so
/// re-applying an already-checkpointed prefix of the log is idempotent — the checkpoint endpoint
/// can safely replay from the start of the retained window. Mutations go through
/// <see cref="StudioCompositionBodyEditor"/> where an editor operation exists so lifecycle
/// semantics stay owned by the shared Studio seam.
/// </summary>
internal static class SavedMapOperationDraftApplier
{
    /// <summary>
    /// Applies <paramref name="operations"/> in server-cursor order to
    /// <paramref name="envelope"/> and returns the mutated envelope.
    /// </summary>
    /// <exception cref="SavedMapCheckpointUnsupportedOperationException">
    /// An operation kind cannot be expressed on the composition body (currently
    /// <see cref="SavedMapOperationKind.SetMetadataField"/>). Checkpointing fails closed instead
    /// of silently dropping edits.
    /// </exception>
    /// <exception cref="SavedMapCheckpointPayloadException">An operation payload is malformed.</exception>
    /// <exception cref="StudioCompositionNotFoundException">A targeted layer does not exist.</exception>
    public static StudioPackageEnvelope Apply(
        StudioPackageEnvelope envelope,
        IReadOnlyList<SavedMapOperationEnvelope> operations)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(operations);

        var current = envelope;
        foreach (var operation in operations.OrderBy(static op => op.ServerCursor.Value))
        {
            current = operation.Kind switch
            {
                // Keep this switch and IsCheckpointable in lockstep: the append endpoint rejects
                // any kind this applier cannot express, so an unsupported kind can never enter a
                // checkpointable log (honua-server#2999 review).
                SavedMapOperationKind.SetViewport => ApplyBody(current, operation, ApplySetViewport),
                SavedMapOperationKind.SetLayerVisibility => ApplyBody(current, operation, ApplySetLayerVisibility),
                SavedMapOperationKind.ReorderLayers => ApplyBody(current, operation, ApplyReorderLayers),
                SavedMapOperationKind.PatchStyle => ApplyBody(current, operation, ApplyPatchStyle),
                SavedMapOperationKind.ReplaceWebMapDocument => ApplyReplaceDocument(current, operation),
                _ => throw new SavedMapCheckpointUnsupportedOperationException(operation)
            };
        }

        return current;
    }

    /// <summary>
    /// Whether <paramref name="kind"/> can be applied to a Studio composition body by
    /// <see cref="Apply"/>. The op-log append endpoint gates on this so the log only ever holds
    /// operations a checkpoint can actually persist.
    /// </summary>
    /// <remarks>
    /// <see cref="SavedMapOperationKind.SetMetadataField"/> is deliberately excluded: the Studio
    /// composition body carries no metadata bag, so there is no non-speculative transform for it.
    /// Saved-map metadata is edited through the Studio draft surface, not the live op log.
    /// </remarks>
    public static bool IsCheckpointable(SavedMapOperationKind kind) => kind switch
    {
        SavedMapOperationKind.SetViewport => true,
        SavedMapOperationKind.SetLayerVisibility => true,
        SavedMapOperationKind.ReorderLayers => true,
        SavedMapOperationKind.PatchStyle => true,
        SavedMapOperationKind.ReplaceWebMapDocument => true,
        _ => false,
    };

    private static StudioPackageEnvelope ApplyBody(
        StudioPackageEnvelope envelope,
        SavedMapOperationEnvelope operation,
        Func<StudioCompositionBody, SavedMapOperationEnvelope, StudioCompositionBody> apply)
    {
        var body = StudioCompositionBodyEditor.ReadBody(envelope);
        return StudioCompositionBodyEditor.WriteBody(envelope, apply(body, operation));
    }

    private static StudioCompositionBody ApplySetViewport(
        StudioCompositionBody body,
        SavedMapOperationEnvelope operation)
    {
        StudioCompositionView? view;
        try
        {
            view = operation.Payload.Deserialize(StudioJsonContext.Default.StudioCompositionView);
        }
        catch (JsonException ex)
        {
            throw new SavedMapCheckpointPayloadException(operation, "The viewport payload is not a valid composition view.", ex);
        }

        if (view is null)
        {
            throw new SavedMapCheckpointPayloadException(operation, "The viewport payload is required.");
        }

        return StudioCompositionBodyEditor.SetView(body, view);
    }

    private static StudioCompositionBody ApplySetLayerVisibility(
        StudioCompositionBody body,
        SavedMapOperationEnvelope operation)
    {
        var layerId = ReadRequiredString(operation, "layerId");
        if (operation.Payload.ValueKind != JsonValueKind.Object ||
            !operation.Payload.TryGetProperty("visible", out var visibleElement) ||
            visibleElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SavedMapCheckpointPayloadException(operation, "The layer-visibility payload requires a boolean 'visible'.");
        }

        var layers = body.Layers.ToList();
        var index = layers.FindIndex(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new StudioCompositionNotFoundException($"No layer with id '{layerId}' exists in the composition.");
        }

        layers[index] = layers[index] with { Visible = visibleElement.GetBoolean() };
        return body with { Layers = layers };
    }

    private static StudioCompositionBody ApplyReorderLayers(
        StudioCompositionBody body,
        SavedMapOperationEnvelope operation)
    {
        if (operation.Payload.ValueKind != JsonValueKind.Object ||
            !operation.Payload.TryGetProperty("layerIds", out var idsElement) ||
            idsElement.ValueKind != JsonValueKind.Array)
        {
            throw new SavedMapCheckpointPayloadException(operation, "The reorder payload requires a 'layerIds' array.");
        }

        var order = new List<string>();
        foreach (var idElement in idsElement.EnumerateArray())
        {
            if (idElement.ValueKind != JsonValueKind.String ||
                idElement.GetString() is not { } id ||
                string.IsNullOrWhiteSpace(id))
            {
                throw new SavedMapCheckpointPayloadException(operation, "Every entry in 'layerIds' must be a non-empty string.");
            }

            order.Add(id);
        }

        var byId = body.Layers.ToDictionary(static layer => layer.Id, StringComparer.Ordinal);
        var known = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        var reordered = new List<StudioCompositionLayer>(body.Layers.Count);
        // Remove-as-we-go leaves byId holding exactly the layers the order omitted (consumed by
        // the tail append below). A requested id that is NOT removable is never skipped: an id
        // absent from the composition is a genuine state conflict (the layer was removed by
        // another pending or direct draft update) and an id requested twice has no single
        // ordering. Silently dropping either would mark the operation's cursor persisted while
        // the edit was not applied (honua-server#2999 review).
        foreach (var id in order)
        {
            if (byId.Remove(id, out var layer))
            {
                reordered.Add(layer);
                continue;
            }

            if (known.Contains(id))
            {
                throw new SavedMapCheckpointPayloadException(
                    operation, $"The reorder payload lists layer '{id}' more than once.");
            }

            throw new StudioCompositionNotFoundException(
                $"No layer with id '{id}' exists in the composition.");
        }

        // Layers omitted from the requested order keep their relative position at the end so a
        // reorder authored against a stale layer set never drops layers.
        reordered.AddRange(body.Layers.Where(layer => byId.ContainsKey(layer.Id)));
        return body with { Layers = reordered };
    }

    private static StudioCompositionBody ApplyPatchStyle(
        StudioCompositionBody body,
        SavedMapOperationEnvelope operation)
    {
        var layerId = ReadRequiredString(operation, "layerId");
        string? styleRef = null;
        if (operation.Payload.TryGetProperty("styleRef", out var styleElement) &&
            styleElement.ValueKind != JsonValueKind.Null)
        {
            // Lockstep with SavedMapOperationPayloadValidator: a present, non-null styleRef must
            // be a string. Treating any other JSON kind as null (the previous behavior) turned
            // malformed input into a silent style CLEAR (honua-server#2999 review).
            if (styleElement.ValueKind != JsonValueKind.String)
            {
                throw new SavedMapCheckpointPayloadException(
                    operation, "The style payload requires 'styleRef' to be a string, or null to clear it.");
            }

            styleRef = styleElement.GetString();
        }

        return StudioCompositionBodyEditor.SetLayerStyleRef(body, layerId, styleRef);
    }

    /// <summary>
    /// Applies a whole-document replacement as a replacement of the COMPOSITION projection
    /// (<c>layers</c>, <c>view</c>, <c>widgets</c>) through the shared
    /// <see cref="StudioCompositionBodyEditor"/>, not as a wholesale overwrite of the envelope
    /// body.
    /// </summary>
    /// <remarks>
    /// Cloning the payload over <see cref="StudioPackageEnvelope.Body"/> installed the operation
    /// payload as the ENTIRE body, so a replacement such as <c>{"layers":[]}</c> deleted
    /// <c>mapPackageId</c>, <c>format</c>, <c>status</c>, <c>createdAt</c>, <c>sourceBindings</c>
    /// and <c>initialView</c> — all required by the canonical <c>MapPackage</c>/<c>AppPackage</c>
    /// contract that <c>StudioPackageValidator.ValidateFamilyBody</c> enforces. The lifecycle
    /// records that validation failure but still creates the immutable version, so an accepted
    /// replacement corrupted both the draft and its checkpoint (honua-server#2999 review).
    /// <para>
    /// Routing through the editor makes this kind lossless exactly like every other kind in this
    /// applier (this was the only one bypassing the shared seam): the projected keys — including
    /// a <c>view</c> the replacement omits — are replaced wholesale and every unmodelled member
    /// of the stored document survives, so no collaboration
    /// operation can delete or corrupt package-lifecycle metadata the op log has no authority
    /// over. It also keeps the applier in exact lockstep with
    /// <see cref="SavedMapOperationPayloadValidator"/>, which admits the payload by deserializing
    /// it as this same projection.
    /// </para>
    /// </remarks>
    private static StudioPackageEnvelope ApplyReplaceDocument(
        StudioPackageEnvelope envelope,
        SavedMapOperationEnvelope operation)
    {
        if (operation.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new SavedMapCheckpointPayloadException(operation, "The document-replace payload must be an object body.");
        }

        StudioCompositionBody? replacement;
        try
        {
            replacement = operation.Payload.Deserialize(StudioJsonContext.Default.StudioCompositionBody);
        }
        catch (JsonException ex)
        {
            throw new SavedMapCheckpointPayloadException(
                operation, "The document-replace payload is not a valid Studio composition body.", ex);
        }

        if (replacement is null)
        {
            throw new SavedMapCheckpointPayloadException(operation, "The document-replace payload is required.");
        }

        // Same normalization ReadBody performs: the source-generated converter assigns only
        // members present in the payload, so a document that omits "layers"/"widgets" leaves them
        // NULL rather than applying their Array.Empty initializers.
        //
        // ReplaceComposition, not WriteBody: the projection serializes with WhenWritingNull, so a
        // replacement omitting "view" would leave the STORED viewport in place and the wholesale
        // replacement would not be wholesale (honua-server#2999 review).
        return StudioCompositionBodyEditor.ReplaceComposition(
            envelope,
            replacement with
            {
                Layers = replacement.Layers ?? [],
                Widgets = replacement.Widgets ?? [],
            });
    }

    /// <summary>
    /// Reads a required identifier from the operation payload. Whitespace-only values are rejected
    /// in lockstep with <see cref="SavedMapOperationPayloadValidator"/>: the shared Studio
    /// composition editor guards its layer-id arguments with
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>, and that exception is not mapped by the
    /// checkpoint surface — an operation carrying <c>{"layerId":" "}</c> would surface as a 500
    /// instead of a typed 422 for every checkpoint while it is retained (honua-server#2999 review).
    /// Admission validation rejects such payloads first; this keeps an operation that predates the
    /// validator (or arrives from another writer) on the typed error path.
    /// </summary>
    private static string ReadRequiredString(SavedMapOperationEnvelope operation, string property)
    {
        if (operation.Payload.ValueKind == JsonValueKind.Object &&
            operation.Payload.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            element.GetString() is { } value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new SavedMapCheckpointPayloadException(operation, $"The payload requires a non-empty '{property}'.");
    }
}

/// <summary>
/// Raised when a checkpoint contains an operation kind that cannot be expressed on the Studio
/// composition body yet.
/// </summary>
internal sealed class SavedMapCheckpointUnsupportedOperationException : Exception
{
    public SavedMapCheckpointUnsupportedOperationException(SavedMapOperationEnvelope operation)
        : base($"Operation '{operation.OperationId.Value}' (kind '{operation.Kind}') cannot be applied to a Studio draft yet.")
    {
    }
}

/// <summary>
/// Raised when a checkpointed operation carries a payload the applier cannot interpret.
/// </summary>
internal sealed class SavedMapCheckpointPayloadException : Exception
{
    public SavedMapCheckpointPayloadException(SavedMapOperationEnvelope operation, string message, Exception? inner = null)
        : base($"Operation '{operation.OperationId.Value}' (kind '{operation.Kind}'): {message}", inner)
    {
    }
}
