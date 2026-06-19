// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Translates cached <see cref="MultidimensionalCoverageMetadata"/> (populated by
/// <see cref="IMultidimensionalCoverageMetadataReader"/> at registration/scan time)
/// into the Esri <c>multidimensionalInfo</c> shape that the ArcGIS Maps SDK reads
/// via <c>ImageryLayer.multidimensional_info</c>.
/// </summary>
internal interface IImageServerMultidimensionalInfoBuilder
{
    /// <summary>
    /// Builds the Esri multidimensional info document for a layer by reading the
    /// layer's registered multidimensional coverage metadata. Returns
    /// <see langword="null"/> when the layer has no multidimensional coverage with
    /// scanned metadata (the caller should then advertise <c>hasMultidimensions=false</c>
    /// and emit an empty <c>variables</c> array).
    /// </summary>
    Task<ImageServerMultidimensionalInfo?> BuildAsync(
        int layerId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default builder backed by <see cref="IMultidimensionalCoverageStore"/>. The store
/// is optional: deployments without a configured multidimensional coverage provider
/// resolve a <see langword="null"/> store and always report "not multidimensional".
/// </summary>
internal sealed class ImageServerMultidimensionalInfoBuilder(
    IMultidimensionalCoverageStore? coverageStore = null) : IImageServerMultidimensionalInfoBuilder
{
    // Esri standard dimension axis names.
    private const string StdTimeDimension = "StdTime";
    private const string StdZDimension = "StdZ";

    // CF coordinate names commonly used for vertical / temporal axes.
    private static readonly HashSet<string> TimeDimensionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "time", "t", "stdtime", "valid_time", "forecast_time"
    };

    private static readonly HashSet<string> VerticalDimensionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "z", "level", "depth", "altitude", "height", "pressure", "plev", "lev", "stdz"
    };

    /// <inheritdoc />
    public async Task<ImageServerMultidimensionalInfo?> BuildAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        if (coverageStore is null)
        {
            return null;
        }

        var registrations = await coverageStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var scanned = registrations
            .Where(static r => r.Metadata is not null && r.Metadata.Variables.Count > 0)
            .ToArray();

        if (scanned.Length == 0)
        {
            return null;
        }

        var variables = scanned
            .SelectMany(static r => r.Metadata!.Variables.Select(v => (Metadata: r.Metadata!, Variable: v)))
            .Select(static pair => MapVariable(pair.Metadata, pair.Variable))
            .ToArray();

        return new ImageServerMultidimensionalInfo { Variables = variables };
    }

    private static ImageServerMultidimensionalVariable MapVariable(
        MultidimensionalCoverageMetadata metadata,
        MultidimensionalCoverageVariable variable)
    {
        var dimensions = variable.Dimensions
            .Select(dimension => MapDimension(metadata, dimension))
            .ToArray();

        return new ImageServerMultidimensionalVariable
        {
            Name = variable.Name,
            Description = variable.LongName ?? variable.StandardName,
            Unit = variable.Units,
            Dimensions = dimensions
        };
    }

    private static ImageServerMultidimensionalDimension MapDimension(
        MultidimensionalCoverageMetadata metadata,
        MultidimensionalCoverageDimension dimension)
    {
        if (TimeDimensionNames.Contains(dimension.Name) && metadata.Temporal is { } temporal)
        {
            var min = (double)temporal.Start.ToUnixTimeMilliseconds();
            var max = (double)temporal.End.ToUnixTimeMilliseconds();
            var size = temporal.StepCount > 0 ? temporal.StepCount : dimension.Size;
            var values = EnumerateRegularValues(min, max, size);
            return new ImageServerMultidimensionalDimension
            {
                Name = StdTimeDimension,
                Unit = "ISO8601",
                Extent = [min, max],
                // A regular axis whose coordinate values we can enumerate; surfacing the
                // values lets the slices operation enumerate one slice per coordinate
                // (honua-server#1825). Irregular/extent-only axes leave Values null.
                Values = values,
                HasRegularIntervals = values is not null,
                DimensionSize = size
            };
        }

        if (VerticalDimensionNames.Contains(dimension.Name) && metadata.Vertical is { } vertical)
        {
            var size = vertical.StepCount > 0 ? vertical.StepCount : dimension.Size;
            var values = EnumerateRegularValues(vertical.Min, vertical.Max, size);
            return new ImageServerMultidimensionalDimension
            {
                Name = StdZDimension,
                Unit = string.IsNullOrWhiteSpace(vertical.Units) ? null : vertical.Units,
                Extent = [vertical.Min, vertical.Max],
                Values = values,
                HasRegularIntervals = values is not null,
                DimensionSize = size
            };
        }

        // Generic / spatial axis: expose name + size only, no synthetic extent.
        return new ImageServerMultidimensionalDimension
        {
            Name = dimension.Name,
            HasRegularIntervals = false,
            DimensionSize = dimension.Size
        };
    }

    /// <summary>
    /// Synthesizes the evenly-spaced coordinate values for a regular dimension whose
    /// inclusive [<paramref name="min"/>, <paramref name="max"/>] extent and step count
    /// (<paramref name="size"/>) are known. ArcGIS enumerates one slice per coordinate,
    /// so a coverage that advertises <c>dimensionSize:N</c> must surface N values for the
    /// slices operation to enumerate (honua-server#1825). Returns <see langword="null"/>
    /// when the axis is not safely enumerable (non-positive size, non-finite bounds, or an
    /// implausibly large size that would balloon the document) so the dimension falls back
    /// to extent-only and contributes no slices.
    /// </summary>
    private static double[]? EnumerateRegularValues(double min, double max, long size)
    {
        const long MaxEnumerableSize = 10_000;

        if (size <= 0 || size > MaxEnumerableSize)
        {
            return null;
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return null;
        }

        var count = (int)size;
        var values = new double[count];
        if (count == 1)
        {
            // A single-coordinate axis pins to its lower bound (== upper bound for a
            // degenerate extent).
            values[0] = min;
            return values;
        }

        var step = (max - min) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            // Anchor the final value exactly on max to avoid floating-point drift at the
            // inclusive upper bound.
            values[i] = i == count - 1 ? max : min + (step * i);
        }

        return values;
    }
}
