// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.PrintingTools.Models;

namespace Honua.Server.Features.PrintingTools.Layout;

/// <summary>
/// Registry of built-in print layout templates.
/// Page dimensions use points (1 pt = 1/72 inch).
/// </summary>
internal static class LayoutTemplateRegistry
{
    // Page sizes in points (72 points per inch)
    private const float LetterWidth = 612f;   // 8.5 inches
    private const float LetterHeight = 792f;  // 11 inches
    private const float A4Width = 595.28f;    // 210mm
    private const float A4Height = 841.89f;   // 297mm
    private const float A3Width = 841.89f;    // 297mm
    private const float A3Height = 1190.55f;  // 420mm
    private const float Margin = 36f;         // 0.5 inch margin
    private const float TitleHeight = 36f;    // Title bar height
    private const float ScaleBarHeight = 24f;
    private const float NorthArrowSize = 36f;
    private const float LegendWidth = 144f;   // 2 inches
    private const float AttributionHeight = 18f;

    // Spacing constants used in slot geometry
    private const float ElementGap = 8f;
    private const float BottomGap = 4f;
    // Sum of inter-element vertical gaps within the body area
    private const float BodyVerticalGaps = 24f;

    /// <summary>
    /// Map-only template (no layout elements, just the rendered map).
    /// </summary>
    public static PrintLayoutTemplate MapOnly { get; } = new()
    {
        Name = "MAP_ONLY",
        Label = "Map Only",
        PageWidth = LetterWidth,
        PageHeight = LetterHeight,
        MapFrame = new LayoutSlot { X = 0, Y = 0, Width = LetterWidth, Height = LetterHeight }
    };

    /// <summary>
    /// Letter (ANSI A) portrait layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate LetterAnsiAPortrait { get; } =
        CreateFullLayout("Letter ANSI A Portrait", LetterWidth, LetterHeight);

    /// <summary>
    /// Letter (ANSI A) landscape layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate LetterAnsiALandscape { get; } =
        CreateFullLayout("Letter ANSI A Landscape", LetterHeight, LetterWidth);

    /// <summary>
    /// A4 portrait layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate A4Portrait { get; } =
        CreateFullLayout("A4 Portrait", A4Width, A4Height);

    /// <summary>
    /// A4 landscape layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate A4Landscape { get; } =
        CreateFullLayout("A4 Landscape", A4Height, A4Width);

    /// <summary>
    /// A3 portrait layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate A3Portrait { get; } =
        CreateFullLayout("A3 Portrait", A3Width, A3Height);

    /// <summary>
    /// A3 landscape layout with legend, title, scale bar, and north arrow.
    /// </summary>
    public static PrintLayoutTemplate A3Landscape { get; } =
        CreateFullLayout("A3 Landscape", A3Height, A3Width);

    /// <summary>
    /// All registered templates indexed by name (case-insensitive).
    /// </summary>
    private static readonly Dictionary<string, PrintLayoutTemplate> _templates = new(StringComparer.OrdinalIgnoreCase)
    {
        [MapOnly.Name] = MapOnly,
        [LetterAnsiAPortrait.Name] = LetterAnsiAPortrait,
        [LetterAnsiALandscape.Name] = LetterAnsiALandscape,
        [A4Portrait.Name] = A4Portrait,
        [A4Landscape.Name] = A4Landscape,
        [A3Portrait.Name] = A3Portrait,
        [A3Landscape.Name] = A3Landscape,
    };

    /// <summary>
    /// Tries to resolve a template by name.
    /// </summary>
    public static bool TryGetTemplate(string? name, out PrintLayoutTemplate template)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            template = MapOnly;
            return true;
        }

        return _templates.TryGetValue(name, out template!);
    }

    /// <summary>
    /// Gets all registered template names.
    /// </summary>
    public static IReadOnlyList<string> GetTemplateNames() => [.. _templates.Keys];

    /// <summary>
    /// Gets all registered templates.
    /// </summary>
    public static IReadOnlyList<PrintLayoutTemplate> GetTemplates() => [.. _templates.Values];

    /// <summary>
    /// Creates a full-layout template with title, map frame, legend, scale bar,
    /// north arrow, and attribution positioned from page dimensions.
    /// </summary>
    private static PrintLayoutTemplate CreateFullLayout(string name, float pageWidth, float pageHeight)
    {
        var bodyHeight = pageHeight - 2 * Margin - TitleHeight - ScaleBarHeight - AttributionHeight - BodyVerticalGaps;

        return new PrintLayoutTemplate
        {
            Name = name,
            Label = name,
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            Title = new LayoutSlot
            {
                X = Margin,
                Y = Margin,
                Width = pageWidth - 2 * Margin,
                Height = TitleHeight
            },
            MapFrame = new LayoutSlot
            {
                X = Margin,
                Y = Margin + TitleHeight + ElementGap,
                Width = pageWidth - 2 * Margin - LegendWidth - ElementGap,
                Height = bodyHeight
            },
            Legend = new LayoutSlot
            {
                X = pageWidth - Margin - LegendWidth,
                Y = Margin + TitleHeight + ElementGap,
                Width = LegendWidth,
                Height = bodyHeight
            },
            ScaleBar = new LayoutSlot
            {
                X = Margin,
                Y = pageHeight - Margin - AttributionHeight - ScaleBarHeight - BottomGap,
                Width = pageWidth - 2 * Margin - NorthArrowSize - ElementGap,
                Height = ScaleBarHeight
            },
            NorthArrow = new LayoutSlot
            {
                X = pageWidth - Margin - NorthArrowSize,
                Y = pageHeight - Margin - AttributionHeight - NorthArrowSize - BottomGap,
                Width = NorthArrowSize,
                Height = NorthArrowSize
            },
            Attribution = new LayoutSlot
            {
                X = Margin,
                Y = pageHeight - Margin - AttributionHeight,
                Width = pageWidth - 2 * Margin,
                Height = AttributionHeight
            }
        };
    }
}
