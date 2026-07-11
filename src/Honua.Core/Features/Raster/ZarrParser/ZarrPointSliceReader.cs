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
    private const int MaxBatchSize = 1000;
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

        var results = await ReadBatchAsync(
                layerId,
                [new ZarrPointSliceReadRequest(x, y, inputSrid, selections)],
                cancellationToken)
            .ConfigureAwait(false);
        return results[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ZarrPointSliceReadResult>> ReadBatchAsync(
        int layerId,
        IReadOnlyList<ZarrPointSliceReadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return Array.Empty<ZarrPointSliceReadResult>();
        }

        if (requests.Count > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requests),
                $"A point-slice batch supports at most {MaxBatchSize} requests.");
        }

        using var activity = ActivitySource.StartActivity("zarr.point-slice.read-batch", ActivityKind.Internal);
        activity?.SetTag("honua.layer.id", layerId);
        activity?.SetTag("honua.slice.request_count", requests.Count);

        var results = new ZarrPointSliceReadResult?[requests.Count];
        var validRequestCount = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            if (TryValidateRequest(requests[i], out var validationError))
            {
                validRequestCount++;
            }
            else
            {
                results[i] = validationError;
            }
        }

        if (validRequestCount == 0)
        {
            return CompleteBatch(results, activity);
        }

        var registrations = await _zarrStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        ZarrRegistration? registration = null;
        ICloudRangeReader? rangeReader = null;
        var hasScannedRegistration = false;
        foreach (var candidate in registrations)
        {
            if (candidate.Metadata is null)
            {
                continue;
            }

            hasScannedRegistration = true;
            var candidateReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == candidate.Provider);
            if (candidateReader is not null)
            {
                registration = candidate;
                rangeReader = candidateReader;
                break;
            }
        }

        if (registration?.Metadata is null || rangeReader is null)
        {
            var unavailable = hasScannedRegistration
                ? CreateResult(
                    ZarrPointSliceReadStatus.ReaderUnavailable,
                    error: "No scanned multidimensional coverage has a configured storage reader.")
                : CreateResult(
                    ZarrPointSliceReadStatus.RegistrationNotFound,
                    error: "No readable multidimensional coverage is registered for this layer.");
            FillPending(results, unavailable);
            return CompleteBatch(results, activity);
        }

        for (var i = 0; i < requests.Count; i++)
        {
            if (results[i] is null)
            {
                results[i] = await ReadResolvedAsync(
                        registration,
                        rangeReader,
                        requests[i],
                        activity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return CompleteBatch(results, activity);
    }

    private static bool TryValidateRequest(
        ZarrPointSliceReadRequest request,
        out ZarrPointSliceReadResult error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selections);

        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y) ||
            request.Selections.Count is 0 or > MaxSelections)
        {
            error = CreateResult(
                ZarrPointSliceReadStatus.InvalidSelection,
                error: $"A point-slice read requires between 1 and {MaxSelections} finite dimension selections.");
            return false;
        }

        var dimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in request.Selections)
        {
            if (string.IsNullOrWhiteSpace(selection.Dimension) ||
                selection.Dimension.Length > MaxIdentifierLength ||
                selection.Variable?.Length > MaxIdentifierLength ||
                !double.IsFinite(selection.Coordinate))
            {
                error = CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    error: "Slice variable, dimension, and coordinate values are invalid.");
                return false;
            }

            if (!dimensions.Add(selection.Dimension))
            {
                error = CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    error: $"Dimension '{selection.Dimension}' may be selected only once.");
                return false;
            }
        }

        error = default;
        return true;
    }

    private async Task<ZarrPointSliceReadResult> ReadResolvedAsync(
        ZarrRegistration registration,
        ICloudRangeReader rangeReader,
        ZarrPointSliceReadRequest request,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var metadata = registration.Metadata!;

        if (request.InputSrid is { } srid && metadata.Srid > 0 && srid != metadata.Srid)
        {
            return CreateResult(
                ZarrPointSliceReadStatus.InvalidSelection,
                error: $"Point coordinates must use the coverage spatial reference EPSG:{metadata.Srid}; slice-point reprojection is not supported.");
        }

        var variableName = request.Selections
            .Select(static selection => selection.Variable)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
        variableName = string.IsNullOrWhiteSpace(variableName)
            ? metadata.PrimaryVariable ?? metadata.Arrays.FirstOrDefault()?.Name
            : variableName;

        var array = metadata.Arrays.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, variableName, StringComparison.Ordinal));
        if (array is null)
        {
            return CreateResult(
                ZarrPointSliceReadStatus.InvalidSelection,
                variable: variableName,
                error: $"Variable '{variableName}' is not available on this multidimensional coverage.");
        }

        var start = new int[array.Shape.Length];
        var stop = array.Shape.ToArray();
        if (!TryResolveSpatialCell(
                metadata, array, request.X, request.Y, start, stop, out var spatialStatus, out var spatialError))
        {
            return CreateResult(spatialStatus, variable: array.Name, error: spatialError);
        }

        foreach (var selection in request.Selections)
        {
            if (!string.IsNullOrWhiteSpace(selection.Variable) &&
                !string.Equals(selection.Variable, array.Name, StringComparison.Ordinal))
            {
                return CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    variable: array.Name,
                    error: "All slice selections must target the same variable.");
            }

            if (!ZarrDimensionSliceResolver.TryResolveSliceIndex(
                    metadata, selection.Dimension, selection.Coordinate, out var sliceIndex, out var sliceError))
            {
                return CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    variable: array.Name,
                    error: sliceError);
            }

            if (!ZarrDimensionSliceResolver.TryFindArrayDimension(array, selection.Dimension, out var dimensionIndex))
            {
                return CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    variable: array.Name,
                    error: $"Variable '{array.Name}' does not vary along dimension '{selection.Dimension}'.");
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
                return CreateResult(
                    ZarrPointSliceReadStatus.InvalidSelection,
                    variable: array.Name,
                    error: $"Variable '{array.Name}' uses a data type that cannot be sampled.");
            }

            return CreateResult(ZarrPointSliceReadStatus.Success, value, array.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddException(ex);
            return CreateResult(
                ZarrPointSliceReadStatus.ReadFailed,
                variable: array.Name,
                error: "The multidimensional slice could not be read from its backing store.");
        }
    }

    private static ZarrPointSliceReadResult CreateResult(
        ZarrPointSliceReadStatus status,
        double? value = null,
        string? variable = null,
        string? error = null)
        => new(status, value, variable, error);

    private static void FillPending(
        ZarrPointSliceReadResult?[] results,
        ZarrPointSliceReadResult result)
    {
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= result;
        }
    }

    private static ZarrPointSliceReadResult[] CompleteBatch(
        ZarrPointSliceReadResult?[] results,
        Activity? activity)
    {
        var completed = new ZarrPointSliceReadResult[results.Length];
        var successCount = 0;
        var failureCount = 0;
        var readFailureCount = 0;
        for (var i = 0; i < results.Length; i++)
        {
            completed[i] = results[i]!.Value;
            if (completed[i].Status == ZarrPointSliceReadStatus.Success)
            {
                successCount++;
            }
            else
            {
                failureCount++;
                if (completed[i].Status == ZarrPointSliceReadStatus.ReadFailed)
                {
                    readFailureCount++;
                }
            }
        }

        activity?.SetTag("honua.slice.success_count", successCount);
        activity?.SetTag("honua.slice.failure_count", failureCount);
        activity?.SetTag("honua.slice.read_failure_count", readFailureCount);
        if (readFailureCount > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        else if (failureCount == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        return completed;
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
