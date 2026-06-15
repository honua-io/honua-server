// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Resolves Esri <c>Colormap</c> raster-function named colour ramps
/// (<c>ColorrampName</c>) into a canonical <see cref="RasterColormap"/>. Esri ships a
/// fixed catalogue of named multi-part ramps (e.g. <c>Elevation #1</c>, <c>Red to Green</c>);
/// this resolver implements the most commonly requested members as evenly spaced anchor
/// stops over a normalised display range so the rendered output matches the named ramp's
/// hue progression. Values between anchors are interpolated by the raster store.
/// </summary>
public static class NamedColorRamp
{
    // Esri's named ramps are authored over the post-stretch 8-bit display range. The
    // anchors below are spread linearly across 0..255 so a single-band stretched raster
    // (the renderingRule's typical Stretch -> Colormap chain) renders the ramp end to end.
    private const double RangeMin = 0;
    private const double RangeMax = 255;

    // Each ramp is a list of RGB anchor colours, low value to high value.
    private static readonly IReadOnlyDictionary<string, byte[][]> Ramps =
        new Dictionary<string, byte[][]>(StringComparer.OrdinalIgnoreCase)
        {
            // Multi-hue perceptual ramps (the default continuous-raster choices).
            ["Elevation"] = ElevationAnchors,
            ["Elevation #1"] = ElevationAnchors,
            ["Surface"] = ElevationAnchors,

            // Single-hue and divergent ramps Esri exposes by default.
            ["Black to White"] = [[0, 0, 0], [255, 255, 255]],
            ["White to Black"] = [[255, 255, 255], [0, 0, 0]],
            ["Gray"] = [[0, 0, 0], [255, 255, 255]],
            ["Grayscale"] = [[0, 0, 0], [255, 255, 255]],
            ["Red to Green"] = [[214, 47, 39], [255, 255, 191], [26, 150, 65]],
            ["Green to Red"] = [[26, 150, 65], [255, 255, 191], [214, 47, 39]],
            ["Blue to Red"] = [[44, 123, 182], [255, 255, 191], [215, 25, 28]],
            ["Red to Blue"] = [[215, 25, 28], [255, 255, 191], [44, 123, 182]],
            ["Slope"] = [[26, 150, 65], [255, 255, 191], [214, 47, 39]],
            ["Aspect"] = [[228, 26, 28], [255, 255, 51], [77, 175, 74], [55, 126, 184], [228, 26, 28]],

            // Viridis-style perceptual ramp (matches the legend default ramp).
            ["Viridis"] = [[68, 1, 84], [59, 82, 139], [33, 144, 140], [94, 201, 98], [253, 231, 37]],

            // Precipitation / NDVI-friendly ramps.
            ["Precipitation"] = [[255, 255, 229], [120, 198, 121], [35, 132, 67], [0, 69, 41]],
            ["NDVI"] = [[166, 97, 26], [223, 194, 125], [245, 245, 245], [128, 205, 193], [1, 133, 113]],
        };

    private static byte[][] ElevationAnchors =>
    [
        [0, 97, 71],     // low – deep green
        [120, 171, 48],  // green
        [221, 220, 93],  // yellow
        [191, 129, 45],  // tan / brown
        [140, 81, 10],   // brown
        [255, 255, 255], // peak – snow white
    ];

    /// <summary>
    /// Attempts to resolve a named colour ramp into a <see cref="RasterColormap"/>.
    /// </summary>
    /// <param name="name">The Esri <c>ColorrampName</c> to resolve (case-insensitive).</param>
    /// <param name="colormap">The resolved colormap when the name is recognised.</param>
    /// <returns><c>true</c> when <paramref name="name"/> maps to a known ramp.</returns>
    public static bool TryResolve(string? name, out RasterColormap colormap)
    {
        colormap = null!;
        if (string.IsNullOrWhiteSpace(name) || !Ramps.TryGetValue(name.Trim(), out var anchors))
        {
            return false;
        }

        colormap = BuildColormap(anchors);
        return true;
    }

    /// <summary>
    /// Gets the catalogue of supported named colour ramp identifiers, for error messages
    /// and capability advertising.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedNames =>
        Ramps.Keys.ToArray();

    private static RasterColormap BuildColormap(byte[][] anchors)
    {
        var entries = new List<RasterColormapEntry>(anchors.Length);
        var span = RangeMax - RangeMin;
        for (var i = 0; i < anchors.Length; i++)
        {
            var fraction = anchors.Length == 1 ? 0 : (double)i / (anchors.Length - 1);
            var value = RangeMin + (fraction * span);
            var anchor = anchors[i];
            entries.Add(new RasterColormapEntry(value, anchor[0], anchor[1], anchor[2], 255));
        }

        return new RasterColormap { Entries = entries };
    }

    /// <summary>
    /// Renders the supported ramp names as a stable, comma-separated string for diagnostics.
    /// </summary>
    public static string SupportedNamesText()
        => string.Join(", ", Ramps.Keys.OrderBy(static k => k, StringComparer.Ordinal)
            .Select(static k => string.Create(CultureInfo.InvariantCulture, $"'{k}'")));
}
