// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.PrintingTools.Layout;

/// <summary>
/// Describes a print layout template with page dimensions and element slots.
/// Dimensions are in points (1/72 inch).
/// </summary>
internal sealed class PrintLayoutTemplate
{
    /// <summary>Template name (e.g. "MAP_ONLY", "Letter_Portrait").</summary>
    public required string Name { get; init; }

    /// <summary>Display label for the template.</summary>
    public required string Label { get; init; }

    /// <summary>Page width in points.</summary>
    public required float PageWidth { get; init; }

    /// <summary>Page height in points.</summary>
    public required float PageHeight { get; init; }

    /// <summary>Map frame slot.</summary>
    public required LayoutSlot MapFrame { get; init; }

    /// <summary>Title slot (null if not present).</summary>
    public LayoutSlot? Title { get; init; }

    /// <summary>Legend slot (null if not present).</summary>
    public LayoutSlot? Legend { get; init; }

    /// <summary>Scale bar slot (null if not present).</summary>
    public LayoutSlot? ScaleBar { get; init; }

    /// <summary>Attribution slot (null if not present).</summary>
    public LayoutSlot? Attribution { get; init; }

    /// <summary>Whether this template is the special MAP_ONLY template (no chrome).</summary>
    public bool IsMapOnly => Name.Equals("MAP_ONLY", StringComparison.OrdinalIgnoreCase);

    /// <summary>Minimum edition required for this template.</summary>
    public Honua.Core.Features.Licensing.Domain.HonuaEdition MinimumEdition { get; init; }
        = Honua.Core.Features.Licensing.Domain.HonuaEdition.Community;
}

/// <summary>
/// Rectangular slot within a layout template, positioned in points.
/// </summary>
internal sealed class LayoutSlot
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
}
