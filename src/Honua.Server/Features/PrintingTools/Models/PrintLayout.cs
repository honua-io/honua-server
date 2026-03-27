// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.PrintingTools.Models;

/// <summary>
/// Defines a print layout template with page geometry and element placement slots.
/// </summary>
internal sealed class PrintLayoutTemplate
{
    /// <summary>
    /// Unique template name used for selection.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable display label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Page width in points (1/72 inch).
    /// </summary>
    public required float PageWidth { get; init; }

    /// <summary>
    /// Page height in points (1/72 inch).
    /// </summary>
    public required float PageHeight { get; init; }

    /// <summary>
    /// Map frame slot on the page.
    /// </summary>
    public required LayoutSlot MapFrame { get; init; }

    /// <summary>
    /// Title text slot, null if template has no title area.
    /// </summary>
    public LayoutSlot? Title { get; init; }

    /// <summary>
    /// Legend slot, null if template has no legend area.
    /// </summary>
    public LayoutSlot? Legend { get; init; }

    /// <summary>
    /// Scale bar slot, null if template has no scale bar.
    /// </summary>
    public LayoutSlot? ScaleBar { get; init; }

    /// <summary>
    /// North arrow slot, null if template has no north arrow.
    /// </summary>
    public LayoutSlot? NorthArrow { get; init; }

    /// <summary>
    /// Attribution/copyright text slot.
    /// </summary>
    public LayoutSlot? Attribution { get; init; }

    /// <summary>
    /// Whether this is a map-only template (no layout elements).
    /// </summary>
    public bool IsMapOnly => Title is null && Legend is null && ScaleBar is null && NorthArrow is null;
}

/// <summary>
/// Defines a rectangular slot on a page layout in points.
/// </summary>
internal sealed class LayoutSlot
{
    /// <summary>
    /// Left edge in points from page left.
    /// </summary>
    public required float X { get; init; }

    /// <summary>
    /// Top edge in points from page top.
    /// </summary>
    public required float Y { get; init; }

    /// <summary>
    /// Slot width in points.
    /// </summary>
    public required float Width { get; init; }

    /// <summary>
    /// Slot height in points.
    /// </summary>
    public required float Height { get; init; }
}
