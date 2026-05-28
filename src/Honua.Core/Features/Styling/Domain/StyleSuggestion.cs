// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Advisory style suggestion for a layer.
/// </summary>
public sealed class StyleSuggestion
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Geometry type of the resource.
    /// </summary>
    public required MetadataV2GeometryType GeometryType { get; init; }

    /// <summary>
    /// Suggested field for classification (null for geometry-only defaults).
    /// </summary>
    public FieldSuggestion? SuggestedField { get; init; }

    /// <summary>
    /// Classification result (null for geometry-only defaults).
    /// </summary>
    public ClassificationResult? Classification { get; init; }

    /// <summary>
    /// Name of the selected color palette.
    /// </summary>
    public required string PaletteName { get; init; }

    /// <summary>
    /// Hex color array used in the suggestion.
    /// </summary>
    public required string[] PaletteColors { get; init; }

    /// <summary>
    /// Legend metadata.
    /// </summary>
    public required LegendInfo Legend { get; init; }

    /// <summary>
    /// Human-readable observations about the data analysis.
    /// </summary>
    public required IReadOnlyList<string> Observations { get; init; }

    /// <summary>
    /// Edition that generated this suggestion.
    /// </summary>
    public required HonuaEdition Edition { get; init; }
}

/// <summary>
/// Information about the field selected for styling.
/// </summary>
public sealed class FieldSuggestion
{
    /// <summary>
    /// Field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Field data type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Reason this field was selected.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Statistical profile of the field.
    /// </summary>
    public required FieldProfile Profile { get; init; }
}
