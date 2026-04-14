// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A composed map package produced by a geoprocessing workflow.
/// </summary>
public sealed record MapPackage
{
    /// <summary>
    /// Unique identifier for this map package.
    /// </summary>
    public required string MapPackageId { get; init; }

    /// <summary>
    /// Package format identifier (e.g. "honua_map_package.v1").
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    /// Current lifecycle status of the package.
    /// </summary>
    public required PackageStatus Status { get; init; }

    /// <summary>
    /// Optional reference to a MapTemplate metadata resource.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Data sources bound to the map.
    /// </summary>
    public IReadOnlyList<SourceBinding> SourceBindings { get; init; } = [];

    /// <summary>
    /// Style resource identifiers applied to the map.
    /// </summary>
    public IReadOnlyList<string> StyleRefs { get; init; } = [];

    /// <summary>
    /// Optional reference to a Theme metadata resource.
    /// </summary>
    public string? ThemeId { get; init; }

    /// <summary>
    /// MapLibre style-spec v8 object for the composed map.
    /// </summary>
    public JsonElement? MapSpec { get; init; }

    /// <summary>
    /// Initial map viewport.
    /// </summary>
    public MapInitialView? InitialView { get; init; }

    /// <summary>
    /// Legend entries for the composed map.
    /// </summary>
    public IReadOnlyList<LegendEntry> Legend { get; init; } = [];

    /// <summary>
    /// Popup field bindings per source.
    /// </summary>
    public IReadOnlyList<PopupBinding> PopupBindings { get; init; } = [];

    /// <summary>
    /// Label field bindings per source.
    /// </summary>
    public IReadOnlyList<LabelBinding> LabelBindings { get; init; } = [];

    /// <summary>
    /// Artifact identifier for the map preview image, when available.
    /// </summary>
    public string? PreviewArtifactId { get; init; }

    /// <summary>
    /// Artifact identifiers bound to this package.
    /// </summary>
    public IReadOnlyList<string> BoundArtifacts { get; init; } = [];

    /// <summary>
    /// Timestamp when the package was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp of the last status change.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Creates a new draft map package.
    /// </summary>
    public static MapPackage CreateDraft(
        string mapPackageId,
        string format,
        IReadOnlyList<SourceBinding> sourceBindings,
        IReadOnlyList<string> styleRefs,
        string? templateId = null,
        string? themeId = null)
        => new()
        {
            MapPackageId = mapPackageId,
            Format = format,
            Status = PackageStatus.Draft,
            TemplateId = templateId,
            SourceBindings = sourceBindings,
            StyleRefs = styleRefs,
            ThemeId = themeId,
            CreatedAt = DateTimeOffset.UtcNow
        };
}

/// <summary>
/// Initial viewport for a composed map.
/// </summary>
public sealed record MapInitialView
{
    /// <summary>
    /// Bounding box as [minLon, minLat, maxLon, maxLat].
    /// </summary>
    public required double[] Bbox { get; init; }

    /// <summary>
    /// Coordinate reference system (e.g. "EPSG:4326").
    /// </summary>
    public required string Crs { get; init; }
}

/// <summary>
/// Binding of a popup to a source field.
/// </summary>
public sealed record PopupBinding
{
    /// <summary>
    /// Source identifier this binding applies to.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Field name to display in the popup.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Optional template string for popup rendering.
    /// </summary>
    public string? Template { get; init; }
}

/// <summary>
/// Binding of a label to a source field.
/// </summary>
public sealed record LabelBinding
{
    /// <summary>
    /// Source identifier this binding applies to.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Field name to use for the label.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Optional label placement strategy.
    /// </summary>
    public string? Placement { get; init; }
}
