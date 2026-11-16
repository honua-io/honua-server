// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public static class StudioCompositionBodyEditor
{
    public static readonly IReadOnlyCollection<StudioPackageFamily> CompositionEligibleFamilies =
        new[] { StudioPackageFamily.Map, StudioPackageFamily.App, StudioPackageFamily.Dashboard };

    public static void EnsureCompositionEligibleFamily(StudioPackageFamily family)
    {
        if (!CompositionEligibleFamilies.Contains(family))
        {
            throw new StudioCompositionFamilyException(family);
        }
    }

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
                SourceBindingIds = ReadSourceBindingIds(body),
            };
        }
        catch (JsonException ex)
        {
            throw new StudioCompositionBodyException(
                "The draft's composition body is not a valid Studio composition payload.", ex);
        }
    }

    private static HashSet<string> ReadSourceBindingIds(JsonElement body)
    {
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        if (!body.TryGetProperty("sourceBindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
        {
            return sourceIds;
        }

        foreach (var binding in bindings.EnumerateArray().Where(static binding =>
                     binding.ValueKind == JsonValueKind.Object
                     && binding.TryGetProperty("sourceId", out var sourceId)
                     && sourceId.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(sourceId.GetString())))
        {
            sourceIds.Add(binding.GetProperty("sourceId").GetString()!);
        }

        return sourceIds;
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

    private static readonly string[] OptionalProjectedMemberNames = ["view", "interactions", "layout", "controls"];

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

    public static StudioPackageEnvelope EditBody(
        StudioPackageEnvelope envelope,
        Func<StudioCompositionBody, StudioCompositionBody> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return WriteBody(envelope, edit(ReadBody(envelope)));
    }

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

    public static StudioCompositionBody RemoveLayer(StudioCompositionBody body, string layerId)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        var removedLayer = body.Layers.FirstOrDefault(
            existing => string.Equals(existing.Id, layerId, StringComparison.Ordinal));
        if (removedLayer is null)
        {
            throw new StudioCompositionNotFoundException($"No layer with id '{layerId}' exists in the composition.");
        }

        EnsureComponentIsUnreferenced(body, StudioInteractionVocabulary.LayerRefPrefix + layerId);

        var remainingLayers = body.Layers
            .Where(existing => !string.Equals(existing.Id, layerId, StringComparison.Ordinal))
            .ToList();
        var updated = body with
        {
            Layers = remainingLayers,
            SourceBindingIds = WithoutSource(body.SourceBindingIds, SourceOrphanedBy(removedLayer, remainingLayers)),
        };
        var unbound = SourcedComponents(body).FirstOrDefault(component =>
            component.SourceId is not null
            && IsSourceOf(removedLayer, component.SourceId)
            && !AnyLayerReadsSource(remainingLayers, component.SourceId));
        if (unbound is not null)
        {
            throw new StudioCompositionConflictException(
                $"Cannot remove layer '{layerId}' because {unbound.Kind} '{unbound.Id}' still uses sourceId "
                + $"'{unbound.SourceId}'. Remove or rebind the {unbound.Kind} first.");
        }

        return updated;
    }

    private sealed record SourcedComponent(string Kind, string Id, string? SourceId);

    private static IEnumerable<SourcedComponent> SourcedComponents(StudioCompositionBody body) =>
        (body.Controls ?? [])
            .Select(control => new SourcedComponent("control", control.Id, control.SourceId))
            .Concat(body.Widgets.Select(widget => new SourcedComponent("widget", widget.Id, widget.SourceId)));

    private static bool IsSourceOf(StudioCompositionLayer layer, string sourceId) =>
        string.Equals(sourceId, layer.Id, StringComparison.Ordinal)
        || string.Equals(sourceId, layer.SourceId, StringComparison.Ordinal);

    internal static string? SourceOrphanedBy(
        StudioCompositionLayer removedLayer,
        IEnumerable<StudioCompositionLayer> remainingLayers) =>
        removedLayer.SourceId is { } sourceId && !AnyLayerReadsSource(remainingLayers, sourceId)
            ? sourceId
            : null;

    private static bool AnyLayerReadsSource(IEnumerable<StudioCompositionLayer> layers, string sourceId) =>
        layers.Any(layer => string.Equals(layer.SourceId, sourceId, StringComparison.Ordinal));

    private static HashSet<string> WithoutSource(IReadOnlySet<string> sourceIds, string? removedSourceId)
    {
        var remaining = new HashSet<string>(sourceIds, StringComparer.Ordinal);
        if (removedSourceId is not null)
        {
            remaining.Remove(removedSourceId);
        }

        return remaining;
    }

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

    public static StudioCompositionBody SetLayerVisibility(StudioCompositionBody body, string layerId, bool visible)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerId);
        var layers = body.Layers.ToList();
        var index = layers.FindIndex(existing => string.Equals(existing.Id, layerId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new StudioCompositionNotFoundException($"No layer with id '{layerId}' exists in the composition.");
        }

        layers[index] = layers[index] with { Visible = visible };
        return body with { Layers = layers };
    }

    public static StudioCompositionBody SetView(StudioCompositionBody body, StudioCompositionView view)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(view);
        return body with { View = view };
    }

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

        if (control.Title is { Length: > StudioInteractionVocabulary.MaxControlTitleLength })
        {
            throw new StudioCompositionConflictException(
                $"A control 'title' must be {StudioInteractionVocabulary.MaxControlTitleLength} characters or fewer.");
        }

        if (control.SourceId is { Length: > StudioInteractionVocabulary.MaxControlSourceIdLength })
        {
            throw new StudioCompositionConflictException(
                $"A control 'sourceId' must be {StudioInteractionVocabulary.MaxControlSourceIdLength} characters or fewer.");
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
        if (!StudioInteractionVocabulary.IsEventSupportedBySource(interaction.On.Ref, interaction.On.Event))
        {
            throw new StudioCompositionConflictException(
                $"Component '{interaction.On.Ref}' does not emit event '{interaction.On.Event}'.");
        }

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

public sealed class StudioCompositionFamilyException : Exception
{
    public StudioPackageFamily Family { get; }

    public StudioCompositionFamilyException(StudioPackageFamily family)
        : base($"Composition tools apply only to map/app package families; draft family is '{family}'.")
    {
        Family = family;
    }
}

public sealed class StudioCompositionBodyException : Exception
{
    public StudioCompositionBodyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class StudioCompositionNotFoundException : Exception
{
    public StudioCompositionNotFoundException(string message) : base(message)
    {
    }
}

public sealed class StudioCompositionConflictException : InvalidOperationException
{
    public StudioCompositionConflictException(string message) : base(message)
    {
    }
}
