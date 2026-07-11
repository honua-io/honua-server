// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Default canonical implementation of bounded point reads from registered Zarr slices.
/// </summary>
public sealed class ZarrPointSliceReader : IZarrPointSliceReader
{
    private const int MaxSelections = 8;
    private const int MaxIdentifierLength = 128;
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Raster.Zarr");

    private readonly IZarrStore _zarrStore;
    private readonly IZarrSubsetReader _subsetReader;
    private readonly IReadOnlyList<ICloudRangeReader> _rangeReaders;

    /// <summary>Initializes the canonical reader.</summary>
    public ZarrPointSliceReader(
        IZarrStore zarrStore,
        IZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        ArgumentNullException.ThrowIfNull(rangeReaders);
        _rangeReaders = rangeReaders.ToArray();
    }

    /// <inheritdoc />
    public async Task<ZarrPointSliceReadResult> ReadAsync(
        int layerId,
        double x,
        double y,
        int? inputSrid,
        IReadOnlyList<ZarrPointSliceSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);

        using var activity = ActivitySource.StartActivity("zarr.point-slice.read", ActivityKind.Internal);
        activity?.SetTag("honua.layer.id", layerId);
        activity?.SetTag("honua.slice.selection_count", selections.Count);

        if (!double.IsFinite(x) || !double.IsFinite(y) || selections.Count is 0 or > MaxSelections)
        {
            return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, null,
                $"A point-slice read requires between 1 and {MaxSelections} finite dimension selections.", activity);
        }

        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Dimension) ||
                selection.Dimension.Length > MaxIdentifierLength ||
                selection.Variable?.Length > MaxIdentifierLength ||
                !double.IsFinite(selection.Coordinate))
            {
                return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, null,
                    "Slice variable, dimension, and coordinate values are invalid.", activity);
            }
        }

        var registrations = await _zarrStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var registration = registrations.FirstOrDefault(static candidate => candidate.Metadata is not null);
        if (registration?.Metadata is not { } metadata)
        {
            return Finish(ZarrPointSliceReadStatus.RegistrationNotFound, null, null,
                "No readable multidimensional coverage is registered for this layer.", activity);
        }

        if (inputSrid is { } srid && metadata.Srid > 0 && srid != metadata.Srid)
        {
            return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, null,
                $"Point coordinates must use the coverage spatial reference EPSG:{metadata.Srid}; slice-point reprojection is not supported.", activity);
        }

        var variableName = selections
            .Select(static selection => selection.Variable)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
        variableName = string.IsNullOrWhiteSpace(variableName)
            ? metadata.PrimaryVariable ?? metadata.Arrays.FirstOrDefault()?.Name
            : variableName;

        var array = metadata.Arrays.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variableName, StringComparison.Ordinal));
        if (array is null)
        {
            return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, variableName,
                $"Variable '{variableName}' is not available on this multidimensional coverage.", activity);
        }

        var start = new int[array.Shape.Length];
        var stop = array.Shape.ToArray();
        if (!TryResolveSpatialCell(metadata, array, x, y, start, stop, out var spatialStatus, out var spatialError))
        {
            return Finish(spatialStatus, null, array.Name, spatialError, activity);
        }

        foreach (var selection in selections)
        {
            if (!string.IsNullOrWhiteSpace(selection.Variable) &&
                !string.Equals(selection.Variable, array.Name, StringComparison.Ordinal))
            {
                return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, array.Name,
                    "All slice selections must target the same variable.", activity);
            }

            if (!ZarrDimensionSliceResolver.TryResolveSliceIndex(
                    metadata, selection.Dimension, selection.Coordinate, out var sliceIndex, out var sliceError))
            {
                return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, array.Name, sliceError, activity);
            }

            if (!ZarrDimensionSliceResolver.TryFindArrayDimension(array, selection.Dimension, out var dimensionIndex))
            {
                return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, array.Name,
                    $"Variable '{array.Name}' does not vary along dimension '{selection.Dimension}'.", activity);
            }

            start[dimensionIndex] = sliceIndex;
            stop[dimensionIndex] = sliceIndex + 1;
        }

        for (var i = 0; i < array.Shape.Length; i++)
        {
            if (stop[i] - start[i] > 1 && !IsSpatial(metadata, array.DimensionNames[i]))
            {
                stop[i] = start[i] + 1;
            }
        }

        var rangeReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == registration.Provider);
        if (rangeReader is null)
        {
            return Finish(ZarrPointSliceReadStatus.ReaderUnavailable, null, array.Name,
                "The storage reader for this multidimensional coverage is not configured.", activity);
        }

        try
        {
            var result = await _subsetReader.ReadSubsetAsync(
                    rangeReader,
                    registration.Bucket,
                    registration.RootPath,
                    metadata,
                    new ZarrSubsetRequest { Variable = array.Name, Start = start, Stop = stop },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!ZarrValueDecoder.TryDecodeFirst(result, out var value))
            {
                return Finish(ZarrPointSliceReadStatus.InvalidSelection, null, array.Name,
                    $"Variable '{array.Name}' uses a data type that cannot be sampled.", activity);
            }

            return Finish(ZarrPointSliceReadStatus.Success, value, array.Name, null, activity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddException(ex);
            return Finish(ZarrPointSliceReadStatus.ReadFailed, null, array.Name,
                "The multidimensional slice could not be read from its backing store.", activity);
        }
    }

    private static ZarrPointSliceReadResult Finish(
        ZarrPointSliceReadStatus status,
        double? value,
        string? variable,
        string? error,
        Activity? activity)
    {
        activity?.SetTag("honua.slice.status", status.ToString());
        if (status == ZarrPointSliceReadStatus.Success)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        else if (status is ZarrPointSliceReadStatus.ReadFailed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }

        return new ZarrPointSliceReadResult(status, value, variable, error);
    }

    private static bool TryResolveSpatialCell(
        ZarrStoreMetadata metadata,
        ZarrArrayMetadata array,
        double x,
        double y,
        int[] start,
        int[] stop,
        out ZarrPointSliceReadStatus status,
        out string error)
    {
        status = ZarrPointSliceReadStatus.InvalidSelection;
        error = string.Empty;
        var extent = metadata.Extent;
        var width = extent.XMax - extent.XMin;
        var height = extent.YMax - extent.YMin;
        if (width <= 0 || height <= 0)
        {
            error = "This multidimensional coverage is not georeferenced and cannot be sampled by point.";
            return false;
        }

        var foundX = false;
        var foundY = false;
        for (var i = 0; i < array.DimensionNames.Length; i++)
        {
            var name = array.DimensionNames[i];
            if (IsXDimension(metadata, name))
            {
                foundX = true;
                if (x < extent.XMin || x > extent.XMax)
                {
                    status = ZarrPointSliceReadStatus.OutsideCoverage;
                    error = "The sample point falls outside the coverage extent.";
                    return false;
                }

                var column = Math.Clamp((int)((x - extent.XMin) / width * array.Shape[i]), 0, array.Shape[i] - 1);
                start[i] = column;
                stop[i] = column + 1;
            }
            else if (IsYDimension(metadata, name))
            {
                foundY = true;
                if (y < extent.YMin || y > extent.YMax)
                {
                    status = ZarrPointSliceReadStatus.OutsideCoverage;
                    error = "The sample point falls outside the coverage extent.";
                    return false;
                }

                var row = Math.Clamp((int)((extent.YMax - y) / height * array.Shape[i]), 0, array.Shape[i] - 1);
                start[i] = row;
                stop[i] = row + 1;
            }
        }

        if (!foundX || !foundY)
        {
            error = "The selected variable does not declare resolvable spatial dimensions.";
            return false;
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
}
