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
            return new ImageServerMultidimensionalDimension
            {
                Name = StdTimeDimension,
                Unit = "ISO8601",
                Extent =
                [
                    temporal.Start.ToUnixTimeMilliseconds(),
                    temporal.End.ToUnixTimeMilliseconds()
                ],
                HasRegularIntervals = false,
                DimensionSize = temporal.StepCount > 0 ? temporal.StepCount : dimension.Size
            };
        }

        if (VerticalDimensionNames.Contains(dimension.Name) && metadata.Vertical is { } vertical)
        {
            return new ImageServerMultidimensionalDimension
            {
                Name = StdZDimension,
                Unit = string.IsNullOrWhiteSpace(vertical.Units) ? null : vertical.Units,
                Extent = [vertical.Min, vertical.Max],
                HasRegularIntervals = false,
                DimensionSize = vertical.StepCount > 0 ? vertical.StepCount : dimension.Size
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
}
