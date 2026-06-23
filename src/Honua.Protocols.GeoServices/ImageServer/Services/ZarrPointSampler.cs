// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Resolves an ImageServer <c>multidimensionalDefinition</c> per-slice request to
/// an actual pixel read against a registered Zarr store (#1869). Maps the sample
/// point to grid column/row, pins each requested non-spatial dimension to its
/// resolved slice index via <see cref="ZarrDimensionSliceResolver"/>, reads the
/// pinned 1x1 cell through <see cref="IZarrSubsetReader"/>, and decodes the value.
/// </summary>
internal sealed class ZarrPointSampler
{
    private readonly IZarrStore _zarrStore;
    private readonly IZarrSubsetReader _subsetReader;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;

    public ZarrPointSampler(
        IZarrStore zarrStore,
        IZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        _rangeReaders = rangeReaders ?? throw new ArgumentNullException(nameof(rangeReaders));
    }

    /// <summary>
    /// Returns the first registered Zarr store for the layer whose metadata scan has
    /// completed, or null when the layer has no servable multidimensional store.
    /// </summary>
    public async Task<ZarrRegistration?> FindServableRegistrationAsync(int layerId, CancellationToken cancellationToken)
    {
        var registrations = await _zarrStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        foreach (var registration in registrations)
        {
            if (registration.Metadata is not null)
            {
                return registration;
            }
        }
        return null;
    }

    /// <summary>
    /// Samples one pinned slice value at a point. The result tuple reports success,
    /// the decoded value, and a client-safe error when a constraint cannot be
    /// resolved or the point falls outside the store grid.
    /// </summary>
    /// <param name="registration">A servable Zarr registration (metadata present).</param>
    /// <param name="x">Sample X (longitude/easting) in the store CRS.</param>
    /// <param name="y">Sample Y (latitude/northing) in the store CRS.</param>
    /// <param name="constraints">Parsed per-dimension slice constraints (single value each).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<(bool Ok, double Value, string? Error)> TrySampleAsync(
        ZarrRegistration registration,
        double x,
        double y,
        IReadOnlyList<ImageServerDimensionConstraint> constraints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(constraints);

        var metadata = registration.Metadata
            ?? throw new InvalidOperationException("Zarr registration has no scanned metadata.");

        // Resolve the variable: prefer a constraint-named variable, else the primary.
        var variableName = constraints
            .Select(c => c.VariableName)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
        variableName = string.IsNullOrWhiteSpace(variableName)
            ? metadata.PrimaryVariable ?? (metadata.Arrays.Length > 0 ? metadata.Arrays[0].Name : null)
            : variableName;

        var array = metadata.Arrays.FirstOrDefault(a => string.Equals(a.Name, variableName, StringComparison.Ordinal));
        if (array is null)
        {
            return (false, double.NaN, $"Variable '{variableName}' is not available on this multidimensional coverage.");
        }

        var rank = array.Shape.Length;
        var start = new int[rank];
        var stop = new int[rank];
        for (var i = 0; i < rank; i++)
        {
            start[i] = 0;
            stop[i] = array.Shape[i];
        }

        // Pin the spatial axes to the cell containing the sample point.
        if (!TryResolveSpatialCell(metadata, array, x, y, start, stop, out var spatialError))
        {
            return (false, double.NaN, spatialError);
        }

        // Pin each requested non-spatial dimension to its resolved slice index.
        foreach (var constraint in constraints)
        {
            // isSlice with multiple values, or a non-slice range, is not a single-cell sample.
            var coordinate = constraint.Values.Length > 0 ? constraint.Values[0] : 0d;

            if (!ZarrDimensionSliceResolver.TryResolveSliceIndex(metadata, constraint.DimensionName, coordinate, out var sliceIndex, out var sliceError))
            {
                return (false, double.NaN, sliceError);
            }

            if (!ZarrDimensionSliceResolver.TryFindArrayDimension(array, constraint.DimensionName, out var dimIndex))
            {
                return (false, double.NaN,
                    $"Variable '{array.Name}' does not vary along dimension '{constraint.DimensionName}'.");
            }

            start[dimIndex] = sliceIndex;
            stop[dimIndex] = sliceIndex + 1;
        }

        // Any non-spatial dimension left un-pinned must collapse to a single index so
        // the read is a single cell; pin to index 0 (the first slice) as the default.
        for (var i = 0; i < rank; i++)
        {
            if (stop[i] - start[i] > 1 && !IsSpatial(metadata, array.DimensionNames[i]))
            {
                stop[i] = start[i] + 1;
            }
        }

        var rangeReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == registration.Provider);
        if (rangeReader is null)
        {
            return (false, double.NaN, "The storage backend for this multidimensional coverage is not configured.");
        }

        ZarrSubsetResult result;
        try
        {
            result = await _subsetReader.ReadSubsetAsync(
                    rangeReader,
                    registration.Bucket,
                    registration.RootPath,
                    metadata,
                    new ZarrSubsetRequest { Variable = array.Name, Start = start, Stop = stop },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return (false, double.NaN, ex.Message);
        }

        if (!ZarrValueDecoder.TryDecodeFirst(result, out var sampled))
        {
            return (false, double.NaN, $"Variable '{array.Name}' uses a data type that cannot be sampled.");
        }

        return (true, sampled, null);
    }

    private static bool TryResolveSpatialCell(
        ZarrStoreMetadata metadata,
        ZarrArrayMetadata array,
        double x,
        double y,
        int[] start,
        int[] stop,
        out string? error)
    {
        error = null;
        var extent = metadata.Extent;
        var width = extent.XMax - extent.XMin;
        var height = extent.YMax - extent.YMin;
        if (width <= 0 || height <= 0)
        {
            error = "This multidimensional coverage is not georeferenced and cannot be sampled by point.";
            return false;
        }

        for (var i = 0; i < array.DimensionNames.Length; i++)
        {
            var name = array.DimensionNames[i];
            if (IsXDimension(metadata, name))
            {
                if (x < extent.XMin || x > extent.XMax)
                {
                    error = "The sample point falls outside the coverage extent.";
                    return false;
                }
                var col = (int)((x - extent.XMin) / width * array.Shape[i]);
                col = Math.Clamp(col, 0, array.Shape[i] - 1);
                start[i] = col;
                stop[i] = col + 1;
            }
            else if (IsYDimension(metadata, name))
            {
                if (y < extent.YMin || y > extent.YMax)
                {
                    error = "The sample point falls outside the coverage extent.";
                    return false;
                }
                // Row 0 is the northernmost row (north-up convention).
                var row = (int)((extent.YMax - y) / height * array.Shape[i]);
                row = Math.Clamp(row, 0, array.Shape[i] - 1);
                start[i] = row;
                stop[i] = row + 1;
            }
        }

        return true;
    }

    private static bool IsSpatial(ZarrStoreMetadata metadata, string name)
        => IsXDimension(metadata, name) || IsYDimension(metadata, name);

    private static bool IsXDimension(ZarrStoreMetadata metadata, string name)
        => metadata.SpatialXDimension is { } declared
            ? string.Equals(declared, name, StringComparison.OrdinalIgnoreCase)
            : name.ToLowerInvariant() is "x" or "lon" or "longitude";

    private static bool IsYDimension(ZarrStoreMetadata metadata, string name)
        => metadata.SpatialYDimension is { } declared
            ? string.Equals(declared, name, StringComparison.OrdinalIgnoreCase)
            : name.ToLowerInvariant() is "y" or "lat" or "latitude";

    internal static string FormatValue(double value)
        => double.IsFinite(value) ? value.ToString(CultureInfo.InvariantCulture) : "NoData";
}
