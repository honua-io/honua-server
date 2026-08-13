// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

/// <summary>
/// Pure, side-effect-free read/write operations over a Studio package
/// envelope's <see cref="StudioPackageEnvelope.Body"/> composition payload
/// (honua-server#3002, AD-8). Every operation returns a new envelope; none of
/// them touch the lifecycle store, generation, or validation — callers
/// (the composition MCP tools) load a draft, apply one of these mutations to
/// its envelope, and pass the result through
/// <see cref="IStudioPackageLifecycleService.UpdateDraftAsync"/> so generation
/// checking and validation stay owned by the lifecycle service (no lifecycle
/// logic is duplicated here).
/// </summary>
public static class StudioCompositionBodyEditor
{
    /// <summary>Package families whose composition body the editor understands.</summary>
    public static readonly IReadOnlyCollection<StudioPackageFamily> CompositionEligibleFamilies =
        new[] { StudioPackageFamily.Map, StudioPackageFamily.App };

    /// <summary>
    /// Throws <see cref="StudioCompositionFamilyException"/> when <paramref name="family"/>
    /// is not a composition-eligible family. Called by every composition tool before
    /// reading or mutating a draft's body.
    /// </summary>
    public static void EnsureCompositionEligibleFamily(StudioPackageFamily family)
    {
        if (!CompositionEligibleFamilies.Contains(family))
        {
            throw new StudioCompositionFamilyException(family);
        }
    }

    /// <summary>
    /// Reads the composition body from an envelope. A missing/null
    /// <see cref="StudioPackageEnvelope.Body"/> (a brand-new draft) reads as
    /// <see cref="StudioCompositionBody.Empty"/> rather than failing.
    /// </summary>
    public static StudioCompositionBody ReadBody(StudioPackageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Body is not { } body || body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return StudioCompositionBody.Empty;
        }

        try
        {
            var composition = body.Deserialize(StudioJsonContext.Default.StudioCompositionBody);
            if (composition is null)
            {
                return StudioCompositionBody.Empty;
            }

            EnsureReferenceNodesArePresent(composition);

            // Normalize the collections before handing the body to any caller. The
            // source-generated converter assigns only members PRESENT in the payload, so a body
            // that simply omits "layers" or "widgets" -- the legal `{}`, or a view-only document --
            // comes back with those properties NULL rather than with their Array.Empty
            // initializers. Every consumer here and in the collaboration appliers dereferences
            // them directly (Any, ToList, ToDictionary, .Count), so without this a `{}` body turns
            // the next composition edit into an unmapped NullReferenceException.
            //
            // Interactions/Layout/Controls are deliberately NOT normalized: null means "this
            // document declares no such block", which WriteBody must leave alone rather
            // than materialize as an empty member on every unrelated edit (see
            // StudioCompositionBody.Interactions).
            return composition with
            {
                Layers = composition.Layers ?? [],
                Widgets = composition.Widgets ?? [],
            };
        }
        catch (JsonException ex)
        {
            throw new StudioCompositionBodyException(
                "The draft's composition body is not a valid Studio composition payload.", ex);
        }
    }

    private static void EnsureReferenceNodesArePresent(StudioCompositionBody composition)
    {
        if (composition.Interactions?.Any(interaction =>
                interaction is null || interaction.On is null || interaction.Do is null) == true
            || composition.Layout?.Items?.Any(item => item is null) == true
            || composition.Controls?.Any(control => control is null) == true)
        {
            throw new JsonException(
                "Composition interactions, layout items and controls must contain non-null reference nodes.");
        }
    }

    /// <summary>
    /// Writes a composition body back onto an envelope's <see cref="StudioPackageEnvelope.Body"/>,
    /// preserving any members of the stored document this projection does not model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StudioCompositionBody"/> is a PROJECTION of the stored document — it models
    /// <c>layers</c>, <c>view</c>, <c>widgets</c>, <c>interactions</c> and <c>layout</c> and nothing
    /// else — but a canonical map package
    /// also carries <c>mapPackageId</c>, <c>format</c>, <c>status</c>, <c>createdAt</c>,
    /// <c>sourceBindings</c> and <c>initialView</c> (all required by <c>MapGenerationSchema</c>).
    /// Serializing the projection straight over the body therefore DELETED every unmodelled field
    /// on the first edit, silently, through both round trips that use this editor: the
    /// collaboration checkpoint applier and the MCP Studio composition tools. Overlaying only the
    /// projected keys onto the original object keeps the round trip lossless
    /// (honua-server#2999 review).
    /// </para>
    /// <para>
    /// The merged object is emitted with <see cref="Utf8JsonWriter"/> rather than by serializing a
    /// <c>Dictionary&lt;string, JsonElement&gt;</c>. The published Honua.Server image builds with
    /// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> and
    /// <see cref="StudioJsonContext"/> registers no dictionary metadata, so the reflection-based
    /// overload threw at runtime for EVERY MCP or checkpoint mutation of an existing object body —
    /// the exact path this merge exists to serve (honua-server#2999 review). Writing the DOM
    /// directly needs no serializer metadata at all.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The stored member names for the OPTIONAL projected members
    /// (<see cref="StudioCompositionBody.View"/>, <see cref="StudioCompositionBody.Interactions"/>,
    /// <see cref="StudioCompositionBody.Layout"/>, <see cref="StudioCompositionBody.Controls"/>),
    /// matching their <c>JsonPropertyName</c>s.
    /// These are the projected members that can be absent from the serialized projection —
    /// layers and widgets always serialize (as arrays) — so a wholesale replacement has to
    /// clear them explicitly.
    /// </summary>
    private static readonly string[] OptionalProjectedMemberNames = ["view", "interactions", "layout", "controls"];

    /// <summary>
    /// Replaces the composition projection WHOLESALE, clearing projected members the replacement
    /// omits, while leaving every unmodelled member of the stored document untouched.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteBody"/> overlays only the keys the serialized projection actually emits,
    /// and <c>StudioJsonContext</c> writes with <c>WhenWritingNull</c> — so a replacement with no
    /// <c>view</c> never overwrote the stored one and the "wholesale" replacement silently kept
    /// the previous viewport. That overlay behaviour is correct for the incremental edits
    /// (add-layer, set-style, set-viewport) that share the editor, so replacement gets its own
    /// seam rather than changing theirs (honua-server#2999 review). The same reasoning covers
    /// every optional projected member (<see cref="OptionalProjectedMemberNames"/>), so
    /// <c>interactions</c>/<c>layout</c> clear on wholesale replacement exactly as
    /// <c>view</c> does.
    /// </remarks>
    /// <param name="envelope">The stored envelope.</param>
    /// <param name="body">The replacement composition.</param>
    /// <returns>The envelope with its composition projection replaced.</returns>
    public static StudioPackageEnvelope ReplaceComposition(
        StudioPackageEnvelope envelope,
        StudioCompositionBody body)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(body);

        var written = WriteBody(envelope, body);
        if (written.Body is not { ValueKind: JsonValueKind.Object } merged)
        {
            return written;
        }

        // Whichever optional members the replacement omits must GO rather than survive from
        // the stored document; the projection's serialized form is the authority on which
        // ones it carries.
        var replacementMembers = JsonSerializer
            .SerializeToElement(body, StudioJsonContext.Default.StudioCompositionBody)
            .EnumerateObject()
            .Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);
        var dropped = OptionalProjectedMemberNames
            .Where(name => !replacementMembers.Contains(name))
            .ToArray();
        if (dropped.Length == 0)
        {
            return written;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in merged.EnumerateObject())
            {
                if (dropped.Contains(member.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(member.Name);
                member.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return written with { Body = document.RootElement.Clone() };
    }

    public static StudioPackageEnvelope WriteBody(StudioPackageEnvelope envelope, StudioCompositionBody body)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(body);

        var json = JsonSerializer.SerializeToElement(body, StudioJsonContext.Default.StudioCompositionBody);
        if (envelope.Body is not { ValueKind: JsonValueKind.Object } original)
        {
            return envelope with { Body = json };
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in original.EnumerateObject())
        {
            merged[member.Name] = member.Value;
        }

        // The projection wins for the keys it owns; everything else survives untouched.
        foreach (var member in json.EnumerateObject())
        {
            merged[member.Name] = member.Value;
        }

        // Reflection-free by construction: no JsonSerializer metadata exists (or is needed) for
        // Dictionary<string, JsonElement>, so the merged object is written straight to the DOM.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in merged)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        // Clone so the returned envelope stays valid after this JsonDocument is disposed.
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return envelope with { Body = document.RootElement.Clone() };
    }

    /// <summary>
    /// Adds a layer to the composition. <paramref name="beforeId"/> inserts before an
    /// existing layer id (append when omitted or unmatched — mirrors the SDK agent-tools
    /// <c>addLayer(layer, beforeId?)</c> shape).
    /// </summary>
    public static StudioCompositionBody AddLayer(
        StudioCompositionBody body, StudioCompositionLayer layer, string? beforeId = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(layer);
        if (body.Layers.Any(existing => string.Equals(existing.Id, layer.Id, StringComparison.Ordinal)))
        {
            throw new StudioCompositionConflictException(
                $"A layer with id '{layer.Id}' already exists in the composition.");
        }

        var layers = body.Layers.ToList();
        var insertAt = beforeId is null
            ? -1
            : layers.FindIndex(existing => string.Equals(existing.Id, beforeId, StringComparison.Ordinal));
        if (insertAt < 0)
        {
            layers.Add(layer);
        }
        else
        {
            layers.Insert(insertAt, layer);
        }

        return body with { Layers = layers };
    }

    /// <summary>Removes a layer by id. Throws when no layer with that id exists.</summary>
    public static StudioCompositionBody RemoveLayer(StudioCompositionBody body, string layerId)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        if (!body.Layers.Any(existing => string.Equals(existing.Id, layerId, StringComparison.Ordinal)))
        {
            throw new StudioCompositionNotFoundException($"No layer with id '{layerId}' exists in the composition.");
        }

        EnsureComponentIsUnreferenced(body, StudioInteractionVocabulary.LayerRefPrefix + layerId);

        return body with
        {
            Layers = body.Layers.Where(existing => !string.Equals(existing.Id, layerId, StringComparison.Ordinal)).ToList()
        };
    }

    /// <summary>Sets (or clears) a layer's bound <see cref="StudioCompositionLayer.StyleRef"/>.</summary>
    public static StudioCompositionBody SetLayerStyleRef(StudioCompositionBody body, string layerId, string? styleRef)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        var index = body.Layers.ToList().FindIndex(existing => string.Equals(existing.Id, layerId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new StudioCompositionNotFoundException($"No layer with id '{layerId}' exists in the composition.");
        }

        var layers = body.Layers.ToList();
        layers[index] = layers[index] with { StyleRef = styleRef };
        return body with { Layers = layers };
    }

    /// <summary>Replaces the composition view wholesale (mirrors the SDK agent-tools <c>setViewport</c>).</summary>
    public static StudioCompositionBody SetView(StudioCompositionBody body, StudioCompositionView view)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(view);
        return body with { View = view };
    }

    /// <summary>Adds a widget to the composition. Throws when the widget id already exists.</summary>
    public static StudioCompositionBody AddWidget(StudioCompositionBody body, StudioCompositionWidget widget)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(widget);
        if (body.Widgets.Any(existing => string.Equals(existing.Id, widget.Id, StringComparison.Ordinal)))
        {
            throw new StudioCompositionConflictException(
                $"A widget with id '{widget.Id}' already exists in the composition.");
        }

        return body with { Widgets = body.Widgets.Append(widget).ToList() };
    }

    /// <summary>Removes a widget by id. Throws when no widget with that id exists.</summary>
    public static StudioCompositionBody RemoveWidget(StudioCompositionBody body, string widgetId)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        if (!body.Widgets.Any(existing => string.Equals(existing.Id, widgetId, StringComparison.Ordinal)))
        {
            throw new StudioCompositionNotFoundException($"No widget with id '{widgetId}' exists in the composition.");
        }

        EnsureComponentIsUnreferenced(body, StudioInteractionVocabulary.WidgetRefPrefix + widgetId);

        return body with
        {
            Widgets = body.Widgets.Where(existing => !string.Equals(existing.Id, widgetId, StringComparison.Ordinal)).ToList()
        };
    }

    /// <summary>
    /// Adds a declarative interaction to the composition, replacing an existing binding
    /// with the same <see cref="StudioInteraction.Id"/> in place (geospatial-mcp
    /// ADR-0030 <c>bind_interaction</c>: "adds or replaces (by id) one interaction").
    /// </summary>
    /// <remarks>
    /// This is an ADMISSION gate as well as a mutation: a bind whose vocabulary is out of
    /// the closed event/verb sets, whose refs do not resolve within the document, or which
    /// would push the <c>(on.ref, on.event)</c> fan-out past
    /// <see cref="StudioInteractionVocabulary.MaxInteractionsPerEventSource"/> is REJECTED
    /// rather than stored and reported later. The identical rules are re-checked over the
    /// whole document by <c>StudioPackageValidator</c>, because a document can also be
    /// authored wholesale through the draft-update surface, which never passes here.
    /// </remarks>
    /// <param name="body">The composition to bind into.</param>
    /// <param name="interaction">The binding to add or replace.</param>
    /// <returns>The composition with the binding applied.</returns>
    public static StudioCompositionBody BindInteraction(StudioCompositionBody body, StudioInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(interaction);
        if (string.IsNullOrWhiteSpace(interaction.Id))
        {
            throw new StudioCompositionConflictException("An interaction requires a non-empty 'id'.");
        }

        if (interaction.Id.Length > StudioInteractionVocabulary.MaxInteractionIdLength)
        {
            throw new StudioCompositionConflictException(
                $"An interaction 'id' must be {StudioInteractionVocabulary.MaxInteractionIdLength} characters or fewer.");
        }

        EnsureBindable(body, interaction);

        var interactions = (body.Interactions ?? []).ToList();
        var index = interactions.FindIndex(existing => string.Equals(existing.Id, interaction.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            interactions[index] = interaction;
        }
        else
        {
            interactions.Add(interaction);
        }

        return body with { Interactions = interactions };
    }

    /// <summary>
    /// Removes a declarative interaction by id. Throws when no interaction with that id
    /// exists — ADR-0030's <c>remove_interaction</c> makes an unknown id an error, not a
    /// no-op, so an agent cannot believe it unbound something it never bound.
    /// </summary>
    /// <param name="body">The composition to remove from.</param>
    /// <param name="interactionId">Id of the interaction to remove.</param>
    /// <returns>The composition without that binding.</returns>
    public static StudioCompositionBody RemoveInteraction(StudioCompositionBody body, string interactionId)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(interactionId);
        var interactions = body.Interactions ?? [];
        if (!interactions.Any(existing => string.Equals(existing.Id, interactionId, StringComparison.Ordinal)))
        {
            throw new StudioCompositionNotFoundException(
                $"No interaction with id '{interactionId}' exists in the composition.");
        }

        return body with
        {
            Interactions = interactions
                .Where(existing => !string.Equals(existing.Id, interactionId, StringComparison.Ordinal))
                .ToList(),
        };
    }

    /// <summary>
    /// Adds a control to the composition, replacing an existing control with the same
    /// <see cref="StudioCompositionControl.Id"/> in place (geospatial-mcp ADR-0031
    /// <c>add_control</c>: "adds or replaces (by id) one control").
    /// </summary>
    /// <remarks>
    /// An ADMISSION gate as well as a mutation, exactly as
    /// <see cref="BindInteraction"/> is: a control whose kind is outside the closed
    /// ADR-0031 vocabulary, or whose id is empty/oversized, is REJECTED here rather than
    /// stored and reported later. <c>StudioPackageValidator</c> re-checks the identical
    /// rules over the whole document, because a body can also be authored wholesale
    /// through the draft-update surface, which never passes here.
    /// </remarks>
    /// <param name="body">The composition to add the control to.</param>
    /// <param name="control">The control to add or replace.</param>
    /// <returns>The composition with the control applied.</returns>
    public static StudioCompositionBody AddControl(StudioCompositionBody body, StudioCompositionControl control)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(control);
        if (string.IsNullOrWhiteSpace(control.Id))
        {
            throw new StudioCompositionConflictException("A control requires a non-empty 'id'.");
        }

        if (control.Id.Length > StudioInteractionVocabulary.MaxControlIdLength)
        {
            throw new StudioCompositionConflictException(
                $"A control 'id' must be {StudioInteractionVocabulary.MaxControlIdLength} characters or fewer.");
        }

        if (!StudioInteractionVocabulary.IsControlKind(control.Kind))
        {
            throw new StudioCompositionConflictException(
                $"'control.kind' must be one of: {string.Join(", ", StudioInteractionVocabulary.ControlKinds)}. "
                + $"Got '{control.Kind}'.");
        }

        // ADR-0031 makes source resolution a validation-gate responsibility: a control whose
        // sourceId names nothing in the document renders an affordance whose domain no host
        // can populate, and it fails silently at render time rather than at authoring time.
        if (control.SourceId is not null && !StudioInteractionVocabulary.IsDeclaredSourceId(body, control.SourceId))
        {
            throw new StudioCompositionConflictException(
                $"'control.sourceId': '{control.SourceId}' does not resolve to a layer or datasource declared in "
                + "this composition document. Omit it for presentation-only controls, or add the layer first.");
        }

        var controls = (body.Controls ?? []).ToList();
        var index = controls.FindIndex(existing => string.Equals(existing.Id, control.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            controls[index] = control;
        }
        else
        {
            controls.Add(control);
        }

        return body with { Controls = controls };
    }

    /// <summary>
    /// Removes a control by id. Throws when no control with that id exists — ADR-0031
    /// makes an unknown id an error, not a no-op.
    /// </summary>
    /// <remarks>
    /// Removing a control an interaction still references would leave a dangling
    /// <c>control:{id}</c> binding, which ADR-0031 forbids. The default
    /// (<paramref name="cascadeInteractions"/> <see langword="false"/>) therefore REJECTS
    /// the removal; passing <see langword="true"/> removes the referencing interactions
    /// with the control. Silently retaining an unresolvable binding is not conformant, so
    /// there is no third option.
    /// </remarks>
    /// <param name="body">The composition to remove from.</param>
    /// <param name="controlId">Id of the control to remove.</param>
    /// <param name="cascadeInteractions">
    /// When <see langword="true"/>, interactions whose <c>on.ref</c> or <c>do.ref</c> is
    /// <c>control:{controlId}</c> are removed with the control.
    /// </param>
    /// <returns>The composition without that control (and, when cascading, its bindings).</returns>
    public static StudioCompositionBody RemoveControl(
        StudioCompositionBody body,
        string controlId,
        bool cascadeInteractions = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        var controls = body.Controls ?? [];
        if (!controls.Any(existing => string.Equals(existing.Id, controlId, StringComparison.Ordinal)))
        {
            throw new StudioCompositionNotFoundException($"No control with id '{controlId}' exists in the composition.");
        }

        var reference = StudioInteractionVocabulary.ControlRefPrefix + controlId;
        var remaining = body with
        {
            Controls = controls
                .Where(existing => !string.Equals(existing.Id, controlId, StringComparison.Ordinal))
                .ToList(),
        };

        if (!cascadeInteractions)
        {
            var bound = (body.Interactions ?? [])
                .Where(interaction =>
                    string.Equals(interaction.On.Ref, reference, StringComparison.Ordinal)
                    || string.Equals(interaction.Do.Ref, reference, StringComparison.Ordinal))
                .Select(interaction => interaction.Id)
                .ToArray();
            if (bound.Length > 0)
            {
                throw new StudioCompositionConflictException(
                    $"Control '{controlId}' is still referenced by interaction(s) {string.Join(", ", bound)}. "
                    + "Remove those bindings first, or re-run with cascadeInteractions=true to remove them "
                    + "with the control.");
            }

            EnsureComponentIsUnreferenced(body, reference);
            return remaining;
        }

        // Layout items are never control references (controls are chrome, not grid items),
        // so the cascade is scoped to interactions; a layout item naming a control is
        // rejected by the layout gate rather than swept up here.
        if (body.Interactions is null)
        {
            return remaining;
        }

        return remaining with
        {
            Interactions = body.Interactions
                .Where(interaction =>
                    !string.Equals(interaction.On.Ref, reference, StringComparison.Ordinal)
                    && !string.Equals(interaction.Do.Ref, reference, StringComparison.Ordinal))
                .ToList(),
        };
    }

    private static void EnsureBindable(StudioCompositionBody body, StudioInteraction interaction)
    {
        if (!StudioInteractionVocabulary.IsEventName(interaction.On.Event))
        {
            throw new StudioCompositionConflictException(
                $"'on.event' must be one of: {string.Join(", ", StudioInteractionVocabulary.EventNames)}. "
                + $"Got '{interaction.On.Event}'.");
        }

        if (!StudioInteractionVocabulary.IsActionVerb(interaction.Do.Verb))
        {
            throw new StudioCompositionConflictException(
                $"'do.verb' must be one of: {string.Join(", ", StudioInteractionVocabulary.ActionVerbs)}. "
                + $"Got '{interaction.Do.Verb}'.");
        }

        EnsureRefResolves(body, interaction.On.Ref, "on.ref");
        EnsureRefResolves(body, interaction.Do.Ref, "do.ref");
        EnsureFanOutCap(body, interaction);
    }

    private static void EnsureRefResolves(StudioCompositionBody body, string reference, string member)
    {
        var resolution = StudioInteractionVocabulary.ResolveRef(body, reference);
        if (resolution != StudioComponentRefResolution.Resolved)
        {
            throw new StudioCompositionConflictException(
                $"'{member}': {StudioInteractionVocabulary.DescribeResolution(reference, resolution)}");
        }
    }

    private static void EnsureComponentIsUnreferenced(StudioCompositionBody body, string reference)
    {
        var interactionUsesReference = (body.Interactions ?? []).Any(interaction =>
            string.Equals(interaction.On.Ref, reference, StringComparison.Ordinal)
            || string.Equals(interaction.Do.Ref, reference, StringComparison.Ordinal));
        var layoutUsesReference = (body.Layout?.Items ?? []).Any(item =>
            string.Equals(item.Ref, reference, StringComparison.Ordinal));

        if (interactionUsesReference || layoutUsesReference)
        {
            throw new StudioCompositionConflictException(
                $"Component reference '{reference}' is still used by the composition.");
        }
    }

    private static void EnsureFanOutCap(StudioCompositionBody body, StudioInteraction interaction)
    {
        // Replacement of an existing id does not grow the fan-out, so it is excluded from
        // the count: re-binding the 8th interaction on a saturated source must stay legal.
        var sharing = (body.Interactions ?? [])
            .Count(existing =>
                !string.Equals(existing.Id, interaction.Id, StringComparison.Ordinal)
                && string.Equals(existing.On.Ref, interaction.On.Ref, StringComparison.Ordinal)
                && string.Equals(existing.On.Event, interaction.On.Event, StringComparison.Ordinal));

        if (sharing >= StudioInteractionVocabulary.MaxInteractionsPerEventSource)
        {
            throw new StudioCompositionConflictException(
                $"At most {StudioInteractionVocabulary.MaxInteractionsPerEventSource} interactions may share the "
                + $"same event source; '{interaction.On.Ref}'/'{interaction.On.Event}' already has {sharing}.");
        }
    }
}

/// <summary>
/// Raised when a composition operation targets a draft whose package family is
/// not composition-eligible (<see cref="StudioCompositionBodyEditor.CompositionEligibleFamilies"/>).
/// </summary>
public sealed class StudioCompositionFamilyException : Exception
{
    /// <summary>The offending package family.</summary>
    public StudioPackageFamily Family { get; }

    public StudioCompositionFamilyException(StudioPackageFamily family)
        : base($"Composition tools apply only to map/app package families; draft family is '{family}'.")
    {
        Family = family;
    }
}

/// <summary>Raised when a draft's composition body cannot be parsed.</summary>
public sealed class StudioCompositionBodyException : Exception
{
    public StudioCompositionBodyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a composition mutation targets a layer/widget id that does not
/// exist (remove, set-style).
/// </summary>
public sealed class StudioCompositionNotFoundException : Exception
{
    public StudioCompositionNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when a composition mutation would create a duplicate layer/widget id
/// (add).
/// </summary>
public sealed class StudioCompositionConflictException : Exception
{
    public StudioCompositionConflictException(string message) : base(message)
    {
    }
}
