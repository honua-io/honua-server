// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

public sealed record StudioCompositionBody
{
    public static StudioCompositionBody Empty { get; } = new();

    [JsonPropertyName("layers")]
    public IReadOnlyList<StudioCompositionLayer> Layers { get; init; } = Array.Empty<StudioCompositionLayer>();

    [JsonPropertyName("view")]
    public StudioCompositionView? View { get; init; }

    [JsonPropertyName("widgets")]
    public IReadOnlyList<StudioCompositionWidget> Widgets { get; init; } = Array.Empty<StudioCompositionWidget>();

    [JsonPropertyName("interactions")]
    public IReadOnlyList<StudioInteraction>? Interactions { get; init; }

    [JsonPropertyName("layout")]
    public StudioLayout? Layout { get; init; }

    [JsonPropertyName("controls")]
    public IReadOnlyList<StudioCompositionControl>? Controls { get; init; }

    [JsonIgnore]
    public IReadOnlySet<string> SourceBindingIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record StudioCompositionControl
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }
}

public sealed record StudioInteraction
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("on")]
    public required StudioInteractionEvent On { get; init; }

    [JsonPropertyName("do")]
    public required StudioInteractionAction Do { get; init; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }
}

public sealed record StudioInteractionEvent
{
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; }
}

public sealed record StudioInteractionAction
{
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("verb")]
    public required string Verb { get; init; }

    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }
}

public sealed record StudioLayout
{
    [JsonPropertyName("grid")]
    public StudioLayoutGrid? Grid { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<StudioLayoutItem>? Items { get; init; }
}

public sealed record StudioLayoutGrid
{
    [JsonPropertyName("columns")]
    public int? Columns { get; init; }
}

public sealed record StudioLayoutItem
{
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("w")]
    public int W { get; init; }

    [JsonPropertyName("h")]
    public int H { get; init; }
}

public enum StudioComponentRefResolution
{
    Resolved = 0,

    Malformed = 1,

    Unresolved = 2,
}

public static class StudioInteractionVocabulary
{
    public const string MapRef = "map";

    public const string LayerRefPrefix = "layer:";

    public const string WidgetRefPrefix = "widget:";

    public const string ControlRefPrefix = "control:";

    public const int MaxInteractionsPerEventSource = 8;

    public const int MaxInteractionIdLength = 200;

    public const int MaxControlIdLength = 200;

    public const int MaxControlTitleLength = 200;

    public const int MaxControlSourceIdLength = 200;

    public const int MinGridColumns = 1;

    public const int MaxGridColumns = 24;

    public const int DefaultGridColumns = 12;

    public static IReadOnlyList<string> EventNames { get; } =
        ["featureSelect", "featureHover", "selection", "change", "viewportChange"];

    public static IReadOnlyList<string> ActionVerbs { get; } =
        ["setFilter", "setViewport", "selectFeature", "runWidgetQuery", "setVisibility"];

    public static bool IsEventName(string? value) =>
        value is not null && EventNames.Contains(value, StringComparer.Ordinal);

    public static bool IsEventSupportedBySource(string? reference, string? eventName)
        => reference switch
        {
            MapRef => string.Equals(eventName, "viewportChange", StringComparison.Ordinal),
            not null when reference.StartsWith(LayerRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "featureSelect", StringComparison.Ordinal)
                || string.Equals(eventName, "featureHover", StringComparison.Ordinal),
            not null when reference.StartsWith(WidgetRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "selection", StringComparison.Ordinal),
            not null when reference.StartsWith(ControlRefPrefix, StringComparison.Ordinal) =>
                string.Equals(eventName, "change", StringComparison.Ordinal),
            _ => false,
        };

    public static IReadOnlyList<string> ControlKinds { get; } =
    [
        "navigation",
        "scale",
        "fullscreen",
        "geolocate",
        "search",
        "measure",
        "timeSlider",
        "filterSelect",
        "filterSlider",
        "filterDateRange",
        "bookmarks",
        "opacity",
        "attribution",
        "basemapSwitcher",
    ];

    public static bool IsActionVerb(string? value) =>
        value is not null && ActionVerbs.Contains(value, StringComparer.Ordinal);

    public static bool IsControlKind(string? value) =>
        value is not null && ControlKinds.Contains(value, StringComparer.Ordinal);

    public static bool IsControlRef(string? value) =>
        value is not null && HasNonEmptyId(value, ControlRefPrefix);

    public static bool IsDeclaredSourceId(StudioCompositionBody body, string? sourceId)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        return body.SourceBindingIds.Contains(sourceId)
            || (body.Layers ?? []).Any(layer => layer is not null
            && (string.Equals(layer.Id, sourceId, StringComparison.Ordinal)
                || string.Equals(layer.SourceId, sourceId, StringComparison.Ordinal)));
    }

    public static bool IsComponentRef(string? value) =>
        value is not null
        && (string.Equals(value, MapRef, StringComparison.Ordinal)
            || HasNonEmptyId(value, LayerRefPrefix)
            || HasNonEmptyId(value, WidgetRefPrefix)
            || HasNonEmptyId(value, ControlRefPrefix));

    public static StudioComponentRefResolution ResolveRef(StudioCompositionBody body, string? value)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsComponentRef(value))
        {
            return StudioComponentRefResolution.Malformed;
        }

        if (string.Equals(value, MapRef, StringComparison.Ordinal))
        {
            return StudioComponentRefResolution.Resolved;
        }

        if (value!.StartsWith(ControlRefPrefix, StringComparison.Ordinal))
        {
            var controlId = value[ControlRefPrefix.Length..];
            return (body.Controls ?? []).Any(control => control is not null
                    && string.Equals(control.Id, controlId, StringComparison.Ordinal))
                ? StudioComponentRefResolution.Resolved
                : StudioComponentRefResolution.Unresolved;
        }

        if (value.StartsWith(LayerRefPrefix, StringComparison.Ordinal))
        {
            var layerId = value[LayerRefPrefix.Length..];
            return (body.Layers ?? []).Any(layer => layer is not null
                    && string.Equals(layer.Id, layerId, StringComparison.Ordinal))
                ? StudioComponentRefResolution.Resolved
                : StudioComponentRefResolution.Unresolved;
        }

        var widgetId = value[WidgetRefPrefix.Length..];
        return (body.Widgets ?? []).Any(widget => widget is not null
                && string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            ? StudioComponentRefResolution.Resolved
            : StudioComponentRefResolution.Unresolved;
    }

    public static string DescribeResolution(string? value, StudioComponentRefResolution resolution) => resolution switch
    {
        StudioComponentRefResolution.Resolved => string.Empty,
        StudioComponentRefResolution.Malformed =>
            $"Component reference '{value}' is not a valid reference; use 'map', 'layer:{{id}}', 'widget:{{id}}' or 'control:{{id}}'.",
        _ => $"Component reference '{value}' does not resolve to a component declared in this composition document.",
    };

    private static bool HasNonEmptyId(string value, string prefix) =>
        value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.Ordinal);
}

public sealed record StudioCompositionLayer
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    [JsonPropertyName("styleRef")]
    public string? StyleRef { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record StudioCompositionView
{
    [JsonPropertyName("bbox")]
    public IReadOnlyList<double>? Bbox { get; init; }

    [JsonPropertyName("center")]
    public IReadOnlyList<double>? Center { get; init; }

    [JsonPropertyName("zoom")]
    public double? Zoom { get; init; }

    [JsonPropertyName("pitch")]
    public double? Pitch { get; init; }

    [JsonPropertyName("bearing")]
    public double? Bearing { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }
}

public static class StudioCompositionViewBounds
{
    public const int BboxOrdinateCount = 4;

    public const int CenterOrdinateCount = 2;

    public const double MinZoom = 0;

    public const double MaxZoom = 24;

    public const double MinPitch = 0;

    public const double MaxPitch = 85;

    public const double MinBearing = 0;

    public const double MaxBearingExclusive = 360;

    public static bool IsZoomInBounds(double zoom) => zoom >= MinZoom && zoom <= MaxZoom;

    public static bool IsPitchInBounds(double pitch) => pitch >= MinPitch && pitch <= MaxPitch;

    public static bool IsBearingInBounds(double bearing) => bearing >= MinBearing && bearing < MaxBearingExclusive;

    public static bool TryValidate(StudioCompositionView? view, out string error)
    {
        if (view is null)
        {
            error = string.Empty;
            return true;
        }

        if (view.Bbox is { Count: not BboxOrdinateCount } bbox)
        {
            error = $"The viewport 'bbox' requires exactly {BboxOrdinateCount} coordinates; got {bbox.Count}.";
            return false;
        }

        if (view.Bbox is { Count: BboxOrdinateCount } orderedBbox &&
            !(orderedBbox[0] <= orderedBbox[2] && orderedBbox[1] <= orderedBbox[3]))
        {
            // The same ordering is required by the canonical Studio map validator. NaN also
            // fails these comparisons, so a collaboration edit cannot checkpoint an extent
            // that the package lifecycle subsequently rejects.
            error = "The viewport 'bbox' must be ordered as [minX,minY,maxX,maxY].";
            return false;
        }

        if (view.Center is { Count: not CenterOrdinateCount } center)
        {
            error = $"The viewport 'center' requires exactly {CenterOrdinateCount} coordinates; got {center.Count}.";
            return false;
        }

        // NaN/Infinity fail both comparisons, which is intended: neither is a renderable view.
        if (view.Zoom is { } zoom && !IsZoomInBounds(zoom))
        {
            error = $"The viewport 'zoom' must be between {MinZoom} and {MaxZoom}.";
            return false;
        }

        if (view.Pitch is { } pitch && !IsPitchInBounds(pitch))
        {
            error = $"The viewport 'pitch' must be between {MinPitch} and {MaxPitch}.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public sealed record StudioCompositionWidget
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }
}
