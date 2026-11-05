// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;

namespace Honua.Server.Features.Collaboration.Checkpoints;

internal static class SavedMapOperationDraftApplier
{
    // An operation kind the composition body cannot express fails the checkpoint closed instead of dropping the edit.
    public static StudioPackageEnvelope Apply(
        StudioPackageEnvelope envelope,
        IReadOnlyList<SavedMapOperationEnvelope> operations) =>
        ApplyForCheckpoint(envelope, operations, supersedeConflictingOperationCursor: null).Envelope;

    // A cursor supersedes an operation only while that operation still fails because its target is missing.
    public static SavedMapCheckpointApplyResult ApplyForCheckpoint(
        StudioPackageEnvelope envelope,
        IReadOnlyList<SavedMapOperationEnvelope> operations,
        long? supersedeConflictingOperationCursor)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(operations);

        var current = envelope;
        SavedMapCheckpointSupersededOperation? superseded = null;
        foreach (var operation in operations.OrderBy(static op => op.ServerCursor.Value))
        {
            try
            {
                var applied = ApplyOperation(current, operation);
                if (operation.ServerCursor.Value == supersedeConflictingOperationCursor)
                {
                    throw new SavedMapCheckpointReconciliationException(
                        $"Operation cursor {supersedeConflictingOperationCursor.Value} applies cleanly and cannot be superseded.");
                }

                current = applied;
            }
            catch (StudioCompositionNotFoundException ex)
                when (operation.ServerCursor.Value == supersedeConflictingOperationCursor)
            {
                superseded = new SavedMapCheckpointSupersededOperation(operation, ex.Message);
            }
            catch (StudioCompositionNotFoundException ex)
            {
                throw new SavedMapCheckpointStateConflictException(operation, ex.Message, ex);
            }
        }

        if (supersedeConflictingOperationCursor.HasValue && superseded is null)
        {
            throw new SavedMapCheckpointReconciliationException(
                $"Operation cursor {supersedeConflictingOperationCursor.Value} is not pending for this checkpoint.");
        }

        return new SavedMapCheckpointApplyResult(current, superseded);
    }

    private static StudioPackageEnvelope ApplyOperation(
        StudioPackageEnvelope current,
        SavedMapOperationEnvelope operation) => operation.Kind switch
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

    // The append endpoint admits only operations a checkpoint can persist, and the metadata-bag kind is excluded.
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
        Func<StudioCompositionBody, SavedMapOperationEnvelope, StudioCompositionBody> apply) =>
        StudioCompositionBodyEditor.EditBody(envelope, body => apply(body, operation));

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

        // Shared seam with the honua_studio_set_layer_visibility MCP tool (honua-server#3199),
        // exactly as ApplyPatchStyle shares SetLayerStyleRef: this applier owns only payload
        // admission, never the lookup/not-found behaviour.
        return StudioCompositionBodyEditor.SetLayerVisibility(body, layerId, visibleElement.GetBoolean());
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

    // A document replacement replaces only the composition projection through the shared editor, so it cannot delete package-lifecycle fields.
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
        var normalizedReplacement = replacement with
        {
            Layers = replacement.Layers ?? [],
            Widgets = replacement.Widgets ?? [],
            // sourceBindings is a canonical, unmodelled package member that replacement
            // operations preserve. Project its ids into the replacement for checkpoint-time
            // source validation even though the original JSON block remains untouched.
            SourceBindingIds = StudioCompositionBodyEditor.ReadBody(envelope).SourceBindingIds,
        };

        var unresolvedControl = (normalizedReplacement.Controls ?? []).FirstOrDefault(control =>
            control.SourceId is not null
            && !StudioInteractionVocabulary.IsDeclaredSourceId(normalizedReplacement, control.SourceId));
        if (unresolvedControl is not null)
        {
            throw new StudioCompositionNotFoundException(
                $"Control '{unresolvedControl.Id}' sourceId '{unresolvedControl.SourceId}' does not resolve to a "
                + "preserved source binding, replacement layer, or datasource.");
        }

        return StudioCompositionBodyEditor.ReplaceComposition(envelope, normalizedReplacement);
    }

    // Whitespace-only identifiers take the typed 422 path, matching the editor's whitespace rejection, so a retained operation does not become a 500.
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

internal sealed class SavedMapCheckpointUnsupportedOperationException : Exception
{
    public SavedMapCheckpointUnsupportedOperationException(SavedMapOperationEnvelope operation)
        : base($"Operation '{operation.OperationId.Value}' (kind '{operation.Kind}') cannot be applied to a Studio draft yet.")
    {
    }
}

internal sealed class SavedMapCheckpointPayloadException : Exception
{
    public SavedMapCheckpointPayloadException(SavedMapOperationEnvelope operation, string message, Exception? inner = null)
        : base($"Operation '{operation.OperationId.Value}' (kind '{operation.Kind}'): {message}", inner)
    {
    }
}

internal sealed record SavedMapCheckpointApplyResult(
    StudioPackageEnvelope Envelope,
    SavedMapCheckpointSupersededOperation? SupersededOperation);

internal sealed record SavedMapCheckpointSupersededOperation(
    SavedMapOperationEnvelope Operation,
    string Reason);

// The conflict carries the exact operation and cursor the client must acknowledge so an unrelated edit is not dropped.
internal sealed class SavedMapCheckpointStateConflictException : Exception
{
    public SavedMapCheckpointStateConflictException(
        SavedMapOperationEnvelope operation,
        string message,
        Exception? inner = null)
        : base(
            $"Operation '{operation.OperationId.Value}' (kind '{operation.Kind}', cursor " +
            $"{operation.ServerCursor.Value}) could not be replayed: {message}",
            inner)
    {
        Operation = operation;
    }

    public SavedMapOperationEnvelope Operation { get; }
}

internal sealed class SavedMapCheckpointReconciliationException : Exception
{
    public SavedMapCheckpointReconciliationException(string message)
        : base(message)
    {
    }
}
