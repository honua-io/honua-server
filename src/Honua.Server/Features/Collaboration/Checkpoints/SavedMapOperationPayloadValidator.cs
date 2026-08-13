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
    /// <para>
    /// Validating the projection is also exactly what the applier writes:
    /// <c>SavedMapOperationDraftApplier.ApplyReplaceDocument</c> replaces <c>layers</c>,
    /// <c>view</c> and <c>widgets</c> through <see cref="StudioCompositionBodyEditor"/> and leaves
    /// every other member of the stored document untouched. A replacement therefore cannot delete
    /// the <c>mapPackageId</c>/<c>format</c>/<c>status</c>/<c>createdAt</c> members the canonical
    /// Map/App family contract requires, so admission does not have to hold a composition-shaped
    /// replacement to the stricter whole-package contract the draft body itself may not satisfy
    /// (honua-server#2999 review).
    /// </para>
    /// </remarks>
    /// <summary>
    /// The members a replacement document may carry — the composition projection the applier
    /// writes, and nothing else. Kept in step with <see cref="StudioCompositionBody"/>'s
    /// <c>JsonPropertyName</c> values; anything outside it is a caller error rather than a
    /// silently discarded property.
    /// </summary>
    private static readonly HashSet<string> ReplacementMembers =
        new(StringComparer.Ordinal) { "layers", "view", "widgets", "interactions", "layout", "controls" };

    /// <summary>Members of <c>StudioCompositionLayer</c>, by <c>JsonPropertyName</c>.</summary>
    private static readonly HashSet<string> LayerMembers =
        new(StringComparer.Ordinal) { "id", "sourceId", "type", "title", "visible", "styleRef", "metadata" };

    /// <summary>Members of <c>StudioCompositionWidget</c>, by <c>JsonPropertyName</c>.</summary>
    private static readonly HashSet<string> WidgetMembers =
        new(StringComparer.Ordinal) { "id", "kind", "title", "sourceId", "config" };

    /// <summary>Members of <c>StudioCompositionView</c>, by <c>JsonPropertyName</c>.</summary>
    private static readonly HashSet<string> ViewMembers =
        new(StringComparer.Ordinal) { "bbox", "center", "zoom", "pitch", "bearing", "crs" };

    /// <summary>Members of the ADR-0030 interaction wire objects.</summary>
    private static readonly HashSet<string> InteractionMembers =
        new(StringComparer.Ordinal) { "id", "on", "do", "disabled" };

    private static readonly HashSet<string> InteractionEventMembers =
        new(StringComparer.Ordinal) { "ref", "event" };

    private static readonly HashSet<string> InteractionActionMembers =
        new(StringComparer.Ordinal) { "ref", "verb", "args" };

    /// <summary>Members of the ADR-0030 layout wire objects.</summary>
    private static readonly HashSet<string> LayoutMembers =
        new(StringComparer.Ordinal) { "grid", "items" };

    private static readonly HashSet<string> LayoutGridMembers =
        new(StringComparer.Ordinal) { "columns" };

    private static readonly HashSet<string> LayoutItemMembers =
        new(StringComparer.Ordinal) { "ref", "x", "y", "w", "h" };

    /// <summary>Members of the ADR-0031 control wire object.</summary>
    private static readonly HashSet<string> ControlMembers =
        new(StringComparer.Ordinal) { "id", "kind", "title", "sourceId", "config" };

    /// <summary>Members of a style patch payload; unknown casing is a caller typo, not a clear.</summary>
    private static readonly HashSet<string> StylePatchMembers =
        new(StringComparer.Ordinal) { "layerId", "styleRef" };

    /// <summary>
    /// Refuses any member of <paramref name="element"/> the projection does not model.
    /// </summary>
    /// <remarks>
    /// Applied at EVERY nesting level, not just the payload root. Deserialization is permissive
    /// throughout, so <c>{"layers":[{"id":"roads","visble":false}]}</c> bound cleanly and
    /// persisted <c>visible:true</c>, and a <c>SetViewport</c> payload of <c>{"zom":12}</c>
    /// consumed a cursor and replaced the previous view with an empty object — a typo silently
    /// applying a default in both cases (honua-server#2999 review).
    /// </remarks>
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

    /// <summary>
    /// Applies <see cref="TryRejectUnmappedMembers"/> to every element of an array member.
    /// </summary>
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
        // Control source resolution runs here rather than in the wire-shape pass because it
        // needs the deserialized layers to resolve against (ADR-0031: a control's sourceId
        // resolves like a layer reference).
        foreach (var control in (body.Controls ?? []).Where(control =>
                     control.SourceId is not null
                     && !StudioInteractionVocabulary.IsDeclaredSourceId(body, control.SourceId)))
        {
            error = $"The document-replace payload's control '{control.Id}' has sourceId '{control.SourceId}', "
                + "which does not resolve to a layer or datasource declared in the payload.";
            return false;
        }

        foreach (var interaction in body.Interactions ?? [])
        {
            if (!TryValidateReplacementComponentRef(body, interaction.On.Ref, "interaction on.ref", out error)
                || !TryValidateReplacementComponentRef(body, interaction.Do.Ref, "interaction do.ref", out error))
            {
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

    /// <summary>
    /// Admits the ADR-0031 <c>controls</c> collection on a document-replace payload with
    /// the same rules the Studio composition gates enforce: closed wire members, a
    /// non-empty bounded id unique within the block, and a kind from the closed
    /// vocabulary. Controls have to be admitted here as well as by the composition tools,
    /// because an interaction bound to <c>control:{id}</c> only resolves when the same
    /// replacement carries the control.
    /// </summary>
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

    /// <summary>
    /// Enforces the shared <see cref="StudioCompositionViewBounds"/> contract the wire model
    /// implies but cannot express: <c>bbox</c> is four ordinates, <c>center</c> is two, and
    /// <c>zoom</c>/<c>pitch</c> stay inside the ranges the Studio view contract declares.
    /// </summary>
    /// <remarks>
    /// <c>IReadOnlyList&lt;double&gt;</c> deserializes any-length arrays happily and <c>double?</c>
    /// accepts any magnitude, so <c>{"bbox":[0]}</c>, <c>{"zoom":25}</c> and <c>{"pitch":90}</c>
    /// all reached the success path, consumed a permanent cursor, and then persisted a view no
    /// client can render — the same wedge class as the whitespace-id and non-string-styleRef fixes
    /// (honua-server#2999 review). The bounds themselves live in Core beside the view model so this
    /// admission path and the MCP composition tool schemas cannot drift apart.
    /// </remarks>
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
