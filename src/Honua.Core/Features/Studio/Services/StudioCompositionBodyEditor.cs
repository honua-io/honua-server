// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

            // Normalize the collections before handing the body to any caller. The
            // source-generated converter assigns only members PRESENT in the payload, so a body
            // that simply omits "layers" or "widgets" -- the legal `{}`, or a view-only document --
            // comes back with those properties NULL rather than with their Array.Empty
            // initializers. Every consumer here and in the collaboration appliers dereferences
            // them directly (Any, ToList, ToDictionary, .Count), so without this a `{}` body turns
            // the next composition edit into an unmapped NullReferenceException.
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

    /// <summary>
    /// Writes a composition body back onto an envelope's <see cref="StudioPackageEnvelope.Body"/>.
    /// </summary>
    public static StudioPackageEnvelope WriteBody(StudioPackageEnvelope envelope, StudioCompositionBody body)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(body);

        var json = JsonSerializer.SerializeToElement(body, StudioJsonContext.Default.StudioCompositionBody);
        return envelope with { Body = json };
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

        return body with
        {
            Widgets = body.Widgets.Where(existing => !string.Equals(existing.Id, widgetId, StringComparison.Ordinal)).ToList()
        };
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
