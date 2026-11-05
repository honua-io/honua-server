// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;

namespace Honua.Server.Features.Collaboration.Checkpoints;

// Malformed payloads are rejected at admission because the op-log cursor is permanent, and accepting one wedges every later checkpoint.
// Admission checks payload shape only, because the checkpointed draft omits pending operations and cannot prove a referenced target exists.
internal static class SavedMapOperationPayloadValidator
{
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

    // A replacement is validated as a composition projection, not merely an object, because admission permanently consumes a cursor.
    private static readonly HashSet<string> ReplacementMembers =
        new(StringComparer.Ordinal) { "layers", "view", "widgets", "interactions", "layout", "controls" };

    private static readonly HashSet<string> LayerMembers =
        new(StringComparer.Ordinal) { "id", "sourceId", "type", "title", "visible", "styleRef", "metadata" };

    private static readonly HashSet<string> WidgetMembers =
        new(StringComparer.Ordinal) { "id", "kind", "title", "sourceId", "config" };

    private static readonly HashSet<string> ViewMembers =
        new(StringComparer.Ordinal) { "bbox", "center", "zoom", "pitch", "bearing", "crs" };

    private static readonly HashSet<string> InteractionMembers =
        new(StringComparer.Ordinal) { "id", "on", "do", "disabled" };

    private static readonly HashSet<string> InteractionEventMembers =
        new(StringComparer.Ordinal) { "ref", "event" };

    private static readonly HashSet<string> InteractionActionMembers =
        new(StringComparer.Ordinal) { "ref", "verb", "args" };

    private static readonly HashSet<string> LayoutMembers =
        new(StringComparer.Ordinal) { "grid", "items" };

    private static readonly HashSet<string> LayoutGridMembers =
        new(StringComparer.Ordinal) { "columns" };

    private static readonly HashSet<string> LayoutItemMembers =
        new(StringComparer.Ordinal) { "ref", "x", "y", "w", "h" };

    private static readonly HashSet<string> ControlMembers =
        new(StringComparer.Ordinal) { "id", "kind", "title", "sourceId", "config" };

    private static readonly HashSet<string> StylePatchMembers =
        new(StringComparer.Ordinal) { "layerId", "styleRef" };

    private static bool TryRejectUnmappedMembers(
        JsonElement element,
        HashSet<string> allowed,
        string subject,
        out string error)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in element.EnumerateObject().Where(member => !allowed.Contains(member.Name)))
            {
                error = $"{subject} has an unrecognized member '{member.Name}'; expected only "
                    + $"{string.Join(", ", allowed.Select(name => $"'{name}'"))}.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryRejectUnmappedArrayMembers(
        JsonElement payload,
        string memberName,
        HashSet<string> allowed,
        string subject,
        out string error)
    {
        var items = payload.TryGetProperty(memberName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

        foreach (var item in items)
        {
            // Reject, never skip. System.Text.Json permits null elements in these reference-type
            // collections, so a payload such as {"layers":[null]} passed this check when the
            // entry was merely filtered out, and the layer/widget invariant loops below then
            // dereferenced layer.Id — an unhandled NullReferenceException and a 500 for what is
            // simply a malformed append that deserves a 400 (honua-server#2999 review).
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} must be an object.";
                return false;
            }

            if (!TryRejectUnmappedMembers(item, allowed, subject, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateReplacementDocument(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The document-replace payload must be an object body.";
            return false;
        }

        // System.Text.Json ignores properties it cannot bind, so a misspelled member such as
        // {"layres":[...]} deserializes to an EMPTY composition, passes every check below, and
        // the applier then replaces the document with nothing — a typo silently clears every
        // layer and widget at checkpoint time. Enumerate the payload and refuse anything the
        // projection does not model (honua-server#2999 review).
        if (!TryRejectUnmappedMembers(payload, ReplacementMembers, "The document-replace payload", out error)
            || !TryRejectUnmappedArrayMembers(payload, "layers", LayerMembers, "A layer in the document-replace payload", out error)
            || !TryRejectUnmappedArrayMembers(payload, "widgets", WidgetMembers, "A widget in the document-replace payload", out error)
            || !TryValidateReplacementControls(payload, out error)
            || !TryValidateReplacementInteractions(payload, out error)
            || !TryValidateReplacementLayout(payload, out error))
        {
            return false;
        }

        if (payload.TryGetProperty("view", out var replacementView)
            && !TryRejectUnmappedMembers(replacementView, ViewMembers, "The document-replace payload's view", out error))
        {
            return false;
        }

        StudioCompositionBody? body;
        try
        {
            body = payload.Deserialize(StudioJsonContext.Default.StudioCompositionBody);
        }
        // The rejection reason is stable and payload-shaped on purpose: JsonException.Message
        // carries CLR type names, JSON paths and byte offsets, and this string is returned
        // verbatim in the append endpoint's 400 (honua-server#2999 review).
        catch (JsonException)
        {
            error = "The document-replace payload is not a valid Studio composition body; " +
                "expected an object with optional 'layers', 'view', 'widgets', 'interactions', 'layout' " +
                "and 'controls' members.";
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

        // Widgets carry the same invariants as layers and the same consequence: an App
        // composition is indexed by widget id, so a duplicate or blank one is a wedge waiting for
        // the next widget operation, not a cosmetic issue (honua-server#2999 review).
        var seenWidgets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in body.Widgets ?? [])
        {
            if (string.IsNullOrWhiteSpace(widget.Id))
            {
                error = "Every widget in the document-replace payload requires a non-empty 'id'.";
                return false;
            }

            if (!seenWidgets.Add(widget.Id))
            {
                error = $"The document-replace payload repeats widget id '{widget.Id}'; widget ids must be unique.";
                return false;
            }

            // C# `required` only demands the JSON member be PRESENT; it does not make the value
            // non-null or non-blank, so {"widgets":[{"id":"legend","kind":null}]} bound cleanly
            // and persisted a widget no composition client can interpret
            // (honua-server#2999 review).
            if (string.IsNullOrWhiteSpace(widget.Kind))
            {
                error = $"Widget '{widget.Id}' in the document-replace payload requires a non-empty 'kind'.";
                return false;
            }
        }

        if (!TryValidateReplacementComponentSemantics(body, out error))
        {
            return false;
        }

        // A replacement carries a view too, so it must satisfy the same coordinate-shape rules an
        // explicit SetViewport operation does.
        return TryValidateViewShape(body.View, out error);
    }

    private static bool TryValidateReplacementComponentSemantics(
        StudioCompositionBody body,
        out string error)
    {
        foreach (var interaction in body.Interactions ?? [])
        {
            if (!TryValidateReplacementComponentRef(body, interaction.On.Ref, "interaction on.ref", out error)
                || !TryValidateReplacementComponentRef(body, interaction.Do.Ref, "interaction do.ref", out error))
            {
                return false;
            }

            if (!StudioInteractionVocabulary.IsEventSupportedBySource(
                    interaction.On.Ref,
                    interaction.On.Event))
            {
                error = $"The document-replace payload's component '{interaction.On.Ref}' does not emit "
                    + $"event '{interaction.On.Event}'.";
                return false;
            }
        }

        foreach (var item in body.Layout?.Items ?? [])
        {
            // Controls are chrome, not grid items (ADR-0031), so they stay out of the
            // layout reference space even though control:{id} now resolves elsewhere.
            if (StudioInteractionVocabulary.IsControlRef(item.Ref))
            {
                error = $"The document-replace payload's layout item ref '{item.Ref}' is invalid: layout items "
                    + "place the map and widgets; controls are chrome and are not grid items.";
                return false;
            }

            if (!TryValidateReplacementComponentRef(body, item.Ref, "layout item ref", out error))
            {
                return false;
            }

            if (item.X < 0 || item.Y < 0)
            {
                error = "A layout item in the document-replace payload requires x/y to be zero or greater.";
                return false;
            }

            if (item.W < 1 || item.H < 1)
            {
                error = "A layout item in the document-replace payload requires w/h to be one or greater.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateReplacementComponentRef(
        StudioCompositionBody body,
        string reference,
        string subject,
        out string error)
    {
        var resolution = StudioInteractionVocabulary.ResolveRef(body, reference);
        if (resolution == StudioComponentRefResolution.Resolved)
        {
            error = string.Empty;
            return true;
        }

        error = $"The document-replace payload's {subject} is invalid: "
            + StudioInteractionVocabulary.DescribeResolution(reference, resolution);
        return false;
    }

    // Controls on a replacement are admitted with the same closed rules, because an interaction bound to a control resolves only when that replacement carries the control.
    private static bool TryValidateReplacementControls(JsonElement payload, out string error)
    {
        if (!payload.TryGetProperty("controls", out var controls))
        {
            error = string.Empty;
            return true;
        }

        if (controls.ValueKind != JsonValueKind.Array)
        {
            error = "The document-replace payload's 'controls' must be an array.";
            return false;
        }

        var controlIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls.EnumerateArray())
        {
            const string subject = "A control in the document-replace payload";
            if (control.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} must be an object.";
                return false;
            }

            if (!TryRejectUnmappedMembers(control, ControlMembers, subject, out error)
                || !TryValidateRequiredString(control, "id", out error)
                || !TryValidateRequiredString(control, "kind", out error))
            {
                return false;
            }

            var controlId = control.GetProperty("id").GetString()!;
            if (controlId.Length > StudioInteractionVocabulary.MaxControlIdLength)
            {
                error = $"{subject}'s 'id' must be "
                    + $"{StudioInteractionVocabulary.MaxControlIdLength} characters or fewer.";
                return false;
            }

            if (!controlIds.Add(controlId))
            {
                error = $"The document-replace payload repeats control id '{controlId}'; control ids must be unique.";
                return false;
            }

            if (control.TryGetProperty("title", out var title)
                && title.ValueKind != JsonValueKind.Null
                && (title.ValueKind != JsonValueKind.String
                    || title.GetString()!.Length > StudioInteractionVocabulary.MaxControlTitleLength))
            {
                error = $"{subject}'s 'title' must be a string of "
                    + $"{StudioInteractionVocabulary.MaxControlTitleLength} characters or fewer.";
                return false;
            }

            if (control.TryGetProperty("sourceId", out var sourceId)
                && sourceId.ValueKind != JsonValueKind.Null
                && (sourceId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(sourceId.GetString())
                    || sourceId.GetString()!.Length > StudioInteractionVocabulary.MaxControlSourceIdLength))
            {
                error = $"{subject}'s 'sourceId' must be a non-empty string of "
                    + $"{StudioInteractionVocabulary.MaxControlSourceIdLength} characters or fewer.";
                return false;
            }

            if (!StudioInteractionVocabulary.IsControlKind(control.GetProperty("kind").GetString()))
            {
                error = $"{subject}'s 'kind' must be one of: "
                    + $"{string.Join(", ", StudioInteractionVocabulary.ControlKinds)}.";
                return false;
            }

            if (control.TryGetProperty("config", out var config) && config.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject}'s 'config' must be an object.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateReplacementInteractions(JsonElement payload, out string error)
    {
        if (!payload.TryGetProperty("interactions", out var interactions))
        {
            error = string.Empty;
            return true;
        }

        if (interactions.ValueKind != JsonValueKind.Array)
        {
            error = "The document-replace payload's 'interactions' must be an array.";
            return false;
        }

        var interactionIds = new HashSet<string>(StringComparer.Ordinal);
        var fanOut = new Dictionary<(string Ref, string Event), int>();
        foreach (var interaction in interactions.EnumerateArray())
        {
            const string subject = "An interaction in the document-replace payload";
            if (interaction.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} must be an object.";
                return false;
            }

            if (!TryRejectUnmappedMembers(interaction, InteractionMembers, subject, out error)
                || !TryValidateRequiredString(interaction, "id", out error))
            {
                return false;
            }

            var interactionId = interaction.GetProperty("id").GetString()!;
            if (interactionId.Length > StudioInteractionVocabulary.MaxInteractionIdLength)
            {
                error = $"{subject}'s 'id' must be "
                    + $"{StudioInteractionVocabulary.MaxInteractionIdLength} characters or fewer.";
                return false;
            }

            if (!interactionIds.Add(interactionId))
            {
                error = $"The document-replace payload repeats interaction id '{interactionId}'; interaction ids must be unique.";
                return false;
            }

            if (!interaction.TryGetProperty("on", out var on) || on.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} requires an object 'on'.";
                return false;
            }

            if (!TryRejectUnmappedMembers(on, InteractionEventMembers, $"{subject}'s 'on'", out error)
                || !TryValidateRequiredString(on, "ref", out error)
                || !TryValidateRequiredString(on, "event", out error))
            {
                return false;
            }

            var eventRef = on.GetProperty("ref").GetString()!;
            var eventName = on.GetProperty("event").GetString()!;
            if (!StudioInteractionVocabulary.IsEventName(eventName))
            {
                error = $"{subject}'s 'on.event' must be one of: {string.Join(", ", StudioInteractionVocabulary.EventNames)}.";
                return false;
            }

            if (!interaction.TryGetProperty("do", out var action) || action.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} requires an object 'do'.";
                return false;
            }

            if (!TryRejectUnmappedMembers(action, InteractionActionMembers, $"{subject}'s 'do'", out error)
                || !TryValidateRequiredString(action, "ref", out error)
                || !TryValidateRequiredString(action, "verb", out error))
            {
                return false;
            }

            var actionVerb = action.GetProperty("verb").GetString()!;
            if (!StudioInteractionVocabulary.IsActionVerb(actionVerb))
            {
                error = $"{subject}'s 'do.verb' must be one of: {string.Join(", ", StudioInteractionVocabulary.ActionVerbs)}.";
                return false;
            }

            if (action.TryGetProperty("args", out var args) && args.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject}'s 'do.args' must be an object.";
                return false;
            }

            if (interaction.TryGetProperty("disabled", out var disabled)
                && disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = $"{subject}'s 'disabled' must be a boolean.";
                return false;
            }

            var eventSource = (eventRef, eventName);
            var eventSourceCount = fanOut.TryGetValue(eventSource, out var existingCount)
                ? existingCount + 1
                : 1;
            if (eventSourceCount > StudioInteractionVocabulary.MaxInteractionsPerEventSource)
            {
                error = $"At most {StudioInteractionVocabulary.MaxInteractionsPerEventSource} interactions may share "
                    + $"the same event source; '{eventRef}'/'{eventName}' exceeds that limit.";
                return false;
            }

            fanOut[eventSource] = eventSourceCount;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateReplacementLayout(JsonElement payload, out string error)
    {
        if (!payload.TryGetProperty("layout", out var layout))
        {
            error = string.Empty;
            return true;
        }

        if (layout.ValueKind != JsonValueKind.Object)
        {
            error = "The document-replace payload's 'layout' must be an object.";
            return false;
        }

        if (!TryRejectUnmappedMembers(layout, LayoutMembers, "The document-replace payload's layout", out error))
        {
            return false;
        }

        if (layout.TryGetProperty("grid", out var grid))
        {
            if (grid.ValueKind != JsonValueKind.Object)
            {
                error = "The document-replace payload's 'layout.grid' must be an object.";
                return false;
            }

            if (!TryRejectUnmappedMembers(grid, LayoutGridMembers, "The document-replace payload's layout grid", out error))
            {
                return false;
            }

            if (grid.TryGetProperty("columns", out var columns))
            {
                if (columns.ValueKind != JsonValueKind.Number || !columns.TryGetInt32(out var columnCount))
                {
                    error = "The document-replace payload's 'layout.grid.columns' must be an integer.";
                    return false;
                }

                if (columnCount < StudioInteractionVocabulary.MinGridColumns
                    || columnCount > StudioInteractionVocabulary.MaxGridColumns)
                {
                    error = "The document-replace payload's 'layout.grid.columns' must be between "
                        + $"{StudioInteractionVocabulary.MinGridColumns} and {StudioInteractionVocabulary.MaxGridColumns}.";
                    return false;
                }
            }
        }

        if (!layout.TryGetProperty("items", out var items))
        {
            error = string.Empty;
            return true;
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            error = "The document-replace payload's 'layout.items' must be an array.";
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            const string subject = "A layout item in the document-replace payload";
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"{subject} must be an object.";
                return false;
            }

            if (!TryRejectUnmappedMembers(item, LayoutItemMembers, subject, out error)
                || !TryValidateRequiredString(item, "ref", out error)
                || !TryValidateRequiredInteger(item, "x", subject, out error)
                || !TryValidateRequiredInteger(item, "y", subject, out error)
                || !TryValidateRequiredInteger(item, "w", subject, out error)
                || !TryValidateRequiredInteger(item, "h", subject, out error))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateRequiredInteger(
        JsonElement payload,
        string memberName,
        string subject,
        out string error)
    {
        if (payload.TryGetProperty(memberName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out _))
        {
            error = string.Empty;
            return true;
        }

        error = $"{subject} requires an integer '{memberName}'.";
        return false;
    }

    // Bbox is four ordinates, center is two, and zoom and pitch stay in the Studio view ranges; the bounds live in Core so this path and the MCP schemas cannot drift.
    private static bool TryValidateViewShape(StudioCompositionView? view, out string error)
        => StudioCompositionViewBounds.TryValidate(view, out error);

    private static bool TryValidateViewport(JsonElement payload, out string error)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "The viewport payload is required.";
            return false;
        }

        if (!TryRejectUnmappedMembers(payload, ViewMembers, "The viewport payload", out error))
        {
            return false;
        }

        StudioCompositionView? view;
        try
        {
            view = payload.Deserialize(StudioJsonContext.Default.StudioCompositionView);
        }
        // Stable message for the same reason as the document-replace path above: the raw
        // JsonException text would leak implementation detail through the 400 response.
        catch (JsonException)
        {
            error = "The viewport payload is not a valid composition view; expected an object with " +
                "optional 'bbox', 'center', 'zoom', 'pitch', 'bearing' and 'crs' members.";
            return false;
        }

        if (view is null)
        {
            error = "The viewport payload is required.";
            return false;
        }

        return TryValidateViewShape(view, out error);
    }

    // A non-string styleRef would clear the layer's style at checkpoint time, so it is a 400 at admission.
    private static bool TryValidatePatchStyle(JsonElement payload, out string error)
    {
        if (!TryRejectUnmappedMembers(payload, StylePatchMembers, "The style payload", out error))
        {
            return false;
        }

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

    // Whitespace-only identifiers are rejected so they cannot take a permanent cursor and then throw an unmapped exception.
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
