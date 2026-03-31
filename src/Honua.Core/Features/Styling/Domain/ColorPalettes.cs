// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Built-in colorblind-safe palettes for style suggestions.
/// </summary>
internal static class ColorPalettes
{
    /// <summary>
    /// Viridis sequential palette (2-9 classes). Default for numeric/choropleth.
    /// </summary>
    public static ColorPalette Viridis { get; } = new()
    {
        Name = "Viridis",
        Category = PaletteCategory.Sequential,
        IsColorblindSafe = true,
        Colors = new Dictionary<int, string[]>
        {
            [2] = ["#440154", "#FDE725"],
            [3] = ["#440154", "#21918C", "#FDE725"],
            [4] = ["#440154", "#31688E", "#35B779", "#FDE725"],
            [5] = ["#440154", "#3B528B", "#21918C", "#5EC962", "#FDE725"],
            [6] = ["#440154", "#443983", "#31688E", "#21918C", "#5EC962", "#FDE725"],
            [7] = ["#440154", "#443983", "#31688E", "#21918C", "#35B779", "#90D743", "#FDE725"],
            [8] = ["#440154", "#46327E", "#365C8D", "#277F8E", "#1FA187", "#4AC16D", "#9FDA3A", "#FDE725"],
            [9] = ["#440154", "#472D7B", "#3B528B", "#2C728E", "#21918C", "#27AD81", "#5EC962", "#AADC32", "#FDE725"]
        }
    };

    /// <summary>
    /// CARTO Bold qualitative palette (2-12 classes). Default for categorical.
    /// </summary>
    public static ColorPalette CartoBold { get; } = new()
    {
        Name = "CartoBold",
        Category = PaletteCategory.Qualitative,
        IsColorblindSafe = true,
        Colors = new Dictionary<int, string[]>
        {
            [2] = ["#E58606", "#5D69B1"],
            [3] = ["#E58606", "#5D69B1", "#52BCA3"],
            [4] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945"],
            [5] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0"],
            [6] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C"],
            [7] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B"],
            [8] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B", "#2F8AC4"],
            [9] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B", "#2F8AC4", "#764E9F"],
            [10] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B", "#2F8AC4", "#764E9F", "#ED645A"],
            [11] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B", "#2F8AC4", "#764E9F", "#ED645A", "#A5AA99"],
            [12] = ["#E58606", "#5D69B1", "#52BCA3", "#99C945", "#CC61B0", "#24796C", "#DAA51B", "#2F8AC4", "#764E9F", "#ED645A", "#A5AA99", "#CC3A8E"]
        }
    };

    /// <summary>
    /// RdBu diverging palette (2-9 classes). For data with meaningful midpoint.
    /// </summary>
    public static ColorPalette RdBu { get; } = new()
    {
        Name = "RdBu",
        Category = PaletteCategory.Diverging,
        IsColorblindSafe = true,
        Colors = new Dictionary<int, string[]>
        {
            [2] = ["#CA0020", "#0571B0"],
            [3] = ["#CA0020", "#F7F7F7", "#0571B0"],
            [4] = ["#CA0020", "#F4A582", "#92C5DE", "#0571B0"],
            [5] = ["#CA0020", "#F4A582", "#F7F7F7", "#92C5DE", "#0571B0"],
            [6] = ["#B2182B", "#EF8A62", "#FDDBC7", "#D1E5F0", "#67A9CF", "#2166AC"],
            [7] = ["#B2182B", "#EF8A62", "#FDDBC7", "#F7F7F7", "#D1E5F0", "#67A9CF", "#2166AC"],
            [8] = ["#B2182B", "#D6604D", "#F4A582", "#FDDBC7", "#D1E5F0", "#92C5DE", "#4393C3", "#2166AC"],
            [9] = ["#B2182B", "#D6604D", "#F4A582", "#FDDBC7", "#F7F7F7", "#D1E5F0", "#92C5DE", "#4393C3", "#2166AC"]
        }
    };

    /// <summary>
    /// Gets a palette by name (case-insensitive), or null if not found.
    /// </summary>
    public static ColorPalette? GetByName(string name)
    {
        if (string.Equals(name, Viridis.Name, StringComparison.OrdinalIgnoreCase))
            return Viridis;
        if (string.Equals(name, CartoBold.Name, StringComparison.OrdinalIgnoreCase))
            return CartoBold;
        if (string.Equals(name, RdBu.Name, StringComparison.OrdinalIgnoreCase))
            return RdBu;
        return null;
    }

    /// <summary>
    /// Selects the best palette for the given field profile and classification method.
    /// </summary>
    public static ColorPalette SelectPalette(FieldProfile profile, ClassificationMethod method)
    {
        if (method == ClassificationMethod.UniqueValue)
            return CartoBold;

        // Check for diverging distribution: high stddev relative to range, mean near center
        if (profile.MinValue.HasValue && profile.MaxValue.HasValue &&
            profile.MeanValue.HasValue && profile.StandardDeviation.HasValue)
        {
            var range = profile.MaxValue.Value - profile.MinValue.Value;
            if (range > 0)
            {
                var midpoint = (profile.MinValue.Value + profile.MaxValue.Value) / 2d;
                var meanOffset = Math.Abs(profile.MeanValue.Value - midpoint) / range;
                var cvRatio = profile.StandardDeviation.Value / range;

                // Mean near center and high spread suggests diverging
                if (meanOffset < 0.15 && cvRatio > 0.2)
                    return RdBu;
            }
        }

        return Viridis;
    }
}
