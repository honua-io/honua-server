// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Coverages.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.Ogc.Api.Coverages.Services;

internal sealed partial class ZarrCoverageService
{
    internal const string CoverageJsonContentType = "application/prs.coverage+json";

    private const long MaxCoverageOutputBytes = 16L * 1024L * 1024L;

    private static readonly ImmutableHashSet<string> SupportedQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f", "subset", "properties", "datetime", "token");

    private static readonly string[] AcceptableMediaTypes = [CoverageJsonContentType, MediaTypes.Json, GeoTiffContentType];

    private readonly IZarrStore _zarrStore;
    private readonly ZarrSubsetReader _subsetReader;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ILogger<ZarrCoverageService> _logger;

    public ZarrCoverageService(
        IZarrStore zarrStore,
        ZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders,
        ILogger<ZarrCoverageService> logger)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        _rangeReaders = rangeReaders ?? throw new ArgumentNullException(nameof(rangeReaders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ZarrRegistration?> FindServableRegistrationAsync(int storageLayerId, CancellationToken cancellationToken)
    {
        var registrations = await _zarrStore.ListByLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        return registrations.FirstOrDefault(registration => registration.Metadata is not null);
    }

    public static OgcCoverageCollection CreateCollection(
        MetadataV2Object resource,
        int storageLayerId,
        ZarrRegistration registration,
        string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(registration);
        var metadata = RequireMetadata(registration);

        var collectionId = storageLayerId.ToString(CultureInfo.InvariantCulture);
        var basePath = $"{baseUrl}/ogc/coverages/collections/{Uri.EscapeDataString(collectionId)}";
        var displayName = resource.Metadata.Title ?? resource.Metadata.Name;

        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(Link.Create(href: basePath, rel: RelationTypes.Self, type: MediaTypes.Json, title: displayName));
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/coverages/collections",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Coverage collections"));
        links.Add(Link.Create(
            href: $"{basePath}/schema",
            rel: RelationTypes.Schema,
            type: MediaTypes.SchemaJson,
            title: "Coverage schema"));
        links.Add(Link.Create(
            href: $"{basePath}/coverage",
            rel: RelationTypes.Coverage,
            type: CoverageJsonContentType,
            title: "Coverage data as CoverageJSON"));

        var primary = ResolvePrimaryArray(metadata);
        var georeferenced = metadata.Srid > 0;
        var domainSet = georeferenced ? CreateGeoTiffDomainSet(metadata, primary) : null;
        AddGeoTiffCoverageLink(links, basePath, domainSet);
        var epsgUri = CreateEpsgUri(metadata.Srid);

        var crs = ImmutableArray.CreateBuilder<string>();
        if (georeferenced)
        {
            if (metadata.Srid == 4326)
            {
                crs.Add(OgcFeaturesUtilities.Crs84Uri);
            }
            crs.Add(epsgUri);
        }

        return new OgcCoverageCollection
        {
            Id = collectionId,
            Title = displayName,
            Description = resource.Metadata.Description ?? registration.Description,
            ItemType = "coverage",
            Extent = georeferenced ? CreateExtent(metadata, epsgUri) : null,
            Links = links.ToImmutable(),
            Crs = crs.ToImmutable(),
            StorageCrs = georeferenced ? epsgUri : null,
            Grid = CreateGrid(metadata, primary, georeferenced),
            Domain = CreateDomain(metadata, primary, georeferenced),
            DomainSet = domainSet,
            RangeType = domainSet is null ? null : CreateGeoTiffRangeType(primary),
            DefaultFields = metadata.Arrays.Select(static array => array.Name).ToImmutableArray()
        };
    }

    public static CoverageSchema CreateSchema(MetadataV2Object resource, int storageLayerId, ZarrRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(registration);
        var metadata = RequireMetadata(registration);

        var properties = ImmutableDictionary.CreateBuilder<string, CoverageSchemaProperty>(StringComparer.Ordinal);
        for (var i = 0; i < metadata.Arrays.Length; i++)
        {
            var array = metadata.Arrays[i];
            properties[array.Name] = new CoverageSchemaProperty
            {
                Type = IsFloatingPoint(array.DataType) ? "number" : "integer",
                Title = array.Name,
                Description = "Zarr variable over dimensions [" + string.Join(", ", array.DimensionNames) + "]",
                PropertySequence = i + 1,
                PixelType = array.DataType,
                // JSON has no representation for NaN or infinity. Keep those
                // sentinels in the binary reader, but do not make the schema
                // endpoint fail serialization for a valid Zarr fill value.
                NoDataValue = ZarrValueDecoder.TryGetFiniteFillValue(array.FillValue, out var fill)
                    ? fill
                    : null
            };
        }

        var displayName = resource.Metadata.Title ?? resource.Metadata.Name;
        return new CoverageSchema
        {
            Title = "Coverage schema for " + displayName,
            Description = "Field selection schema for OGC API Coverages collection " +
                storageLayerId.ToString(CultureInfo.InvariantCulture),
            Properties = properties.ToImmutable()
        };
    }

    public async Task<IResult> GetCoverageAsync(
        HttpContext context,
        string collectionId,
        ZarrRegistration registration,
        string? f,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registration);
        var metadata = RequireMetadata(registration);

        var format = NegotiateFormat(context, f);
        if (format.Error is not null)
        {
            return format.Error;
        }

        if (format.Format == ZarrCoverageFormat.GeoTiff)
        {
            return await GetGeoTiffCoverageAsync(context, new ZarrCoverageTarget(collectionId, registration), cancellationToken)
                .ConfigureAwait(false);
        }

        var unsupportedParameter = context.Request.Query.Keys.FirstOrDefault(parameter => !SupportedQueryParameters.Contains(parameter));
        if (unsupportedParameter is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Parameter '{unsupportedParameter}' is not supported for Zarr-backed coverage collections. Use properties and subset=dim(low:high) grid-index subsetting.");
        }

        if (!TryResolveVariable(context, out var variable, out var variableError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, variableError!);
        }

        if (!TryParseSubsets(context.Request.Query["subset"], metadata, out var subsets, out var subsetError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, subsetError!);
        }

        if (!TryApplyTemporalSubset(context, metadata, subsets, out var temporalError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, temporalError!);
        }

        if (!ZarrCoverageSubsetPlanner.TryPlan(metadata, variable, subsets, MaxCoverageOutputBytes, out var plan, out var planError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, planError!);
        }

        var read = await ReadSubsetAsync(context, (new ZarrCoverageTarget(collectionId, registration), plan!.Request), cancellationToken)
            .ConfigureAwait(false);
        if (read.Error is not null)
        {
            return read.Error;
        }

        var result = read.Result!;
        var (axes, xAxis, yAxis) = BuildAxes(metadata, plan!);
        byte[] payload;
        try
        {
            payload = CoverageJsonWriter.Write(result, axes, xAxis, yAxis, metadata.Srid);
        }
        catch (InvalidOperationException ex)
        {
            ZarrCoverageLog.SubsetRejected(_logger, collectionId, ex.Message);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }

        WriteCoverageHeaders(context, metadata, plan!);
        ZarrCoverageLog.CoverageServed(_logger, collectionId, result.Variable, payload.Length);
        return Results.Bytes(payload, CoverageJsonContentType);
    }

    private async Task<ZarrSubsetRead> ReadSubsetAsync(
        HttpContext context,
        (ZarrCoverageTarget Target, ZarrSubsetRequest Request) read,
        CancellationToken cancellationToken)
    {
        var (target, request) = read;
        var registration = target.Registration;
        var rangeReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == registration.Provider);
        if (rangeReader is null)
        {
            ZarrCoverageLog.RangeReaderMissing(_logger, target.CollectionId, registration.Provider.ToString());
            return new ZarrSubsetRead(null, StandardErrorHelpers.CreateInternalServerError(
                context,
                "The storage backend for this coverage collection is not configured."));
        }

        try
        {
            var result = await _subsetReader
                .ReadSubsetAsync(rangeReader, registration.Bucket, registration.RootPath, RequireMetadata(registration), request, cancellationToken)
                .ConfigureAwait(false);
            return new ZarrSubsetRead(result, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return new ZarrSubsetRead(null, MapSubsetReadFailure(context, target.CollectionId, ex));
        }
    }

    private IResult MapSubsetReadFailure(HttpContext context, string collectionId, Exception exception)
    {
        if (exception is InvalidDataException)
        {
            ZarrCoverageLog.SubsetReadFailed(_logger, exception, collectionId);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "The Zarr store backing this coverage collection returned invalid data.");
        }

        // Subset reader validation messages are client-safe (codec, dtype, bounds, caps).
        ZarrCoverageLog.SubsetRejected(_logger, collectionId, exception.Message);
        return StandardErrorHelpers.CreateBadRequest(context, exception.Message);
    }

    private static ZarrStoreMetadata RequireMetadata(ZarrRegistration registration)
        => registration.Metadata ?? throw new InvalidOperationException(
            "Zarr registration has no scanned metadata; callers must resolve servable registrations first.");

    private static ZarrArrayMetadata ResolvePrimaryArray(ZarrStoreMetadata metadata)
    {
        if (metadata.PrimaryVariable is { } primary)
        {
            var match = metadata.Arrays.FirstOrDefault(array => string.Equals(array.Name, primary, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }
        return metadata.Arrays[0];
    }

    private static Extent CreateExtent(ZarrStoreMetadata metadata, string epsgUri)
    {
        var extent = metadata.Extent;
        var bbox = ImmutableArray.Create(ImmutableArray.Create(extent.XMin, extent.YMin, extent.XMax, extent.YMax));
        return new Extent
        {
            Spatial = new SpatialExtent
            {
                BoundingBox = bbox,
                StorageCrsBoundingBox = bbox,
                Crs = metadata.Srid == 4326 ? OgcFeaturesUtilities.Crs84Uri : epsgUri
            }
        };
    }

    private static CoverageGrid CreateGrid(ZarrStoreMetadata metadata, ZarrArrayMetadata primary, bool georeferenced)
    {
        var rank = primary.Shape.Length;
        var width = primary.Shape[rank - 1];
        var height = rank >= 2 ? primary.Shape[rank - 2] : 1;

        ImmutableArray<double>? origin = null;
        ImmutableArray<double>? resolution = null;
        if (georeferenced && width > 0 && height > 0)
        {
            var extent = metadata.Extent;
            origin = ImmutableArray.Create(
                extent.XMin,
                metadata.YAxisAscending ? extent.YMin : extent.YMax);
            resolution = ImmutableArray.Create(
                (extent.XMax - extent.XMin) / width,
                metadata.YAxisAscending
                    ? (extent.YMax - extent.YMin) / height
                    : (extent.YMin - extent.YMax) / height);
        }

        return new CoverageGrid
        {
            AxisLabels = primary.DimensionNames.ToImmutableArray(),
            Width = width,
            Height = height,
            Origin = origin,
            Resolution = resolution
        };
    }

    private static CoverageDomain CreateDomain(ZarrStoreMetadata metadata, ZarrArrayMetadata primary, bool georeferenced)
    {
        var fullPlan = new ZarrCoverageSubsetPlan(primary, new ZarrSubsetRequest
        {
            Variable = primary.Name,
            Start = new int[primary.Shape.Length],
            Stop = (int[])primary.Shape.Clone()
        });

        var (axes, _, _) = BuildAxes(metadata, fullPlan);
        var builder = ImmutableDictionary.CreateBuilder<string, CoverageAxis>(StringComparer.Ordinal);
        foreach (var axis in axes)
        {
            builder[axis.Name] = new CoverageAxis
            {
                Start = axis.Start,
                Stop = axis.Stop,
                Count = axis.Count,
                Resolution = georeferenced && axis.Count > 1
                    ? Math.Abs(axis.Stop - axis.Start) / (axis.Count - 1)
                    : null
            };
        }

        return new CoverageDomain { Axes = builder.ToImmutable() };
    }

    private static ZarrFormatChoice NegotiateFormat(HttpContext context, string? f)
    {
        if (!string.IsNullOrWhiteSpace(f))
        {
            return f.Trim().ToLowerInvariant() switch
            {
                "covjson" or "coveragejson" or "json" or CoverageJsonContentType => ZarrFormatChoice.CoverageJson,
                "geotiff" or "tiff" or "tif" or GeoTiffContentType or "image/tiff; application=geotiff" or "image/tiff;application=geotiff"
                    => ZarrFormatChoice.GeoTiff,
                _ => new ZarrFormatChoice(ZarrCoverageFormat.CoverageJson, StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Unsupported coverage format '{f}' for a Zarr-backed collection. Use f=covjson or f=geotiff."))
            };
        }

        var acceptHeader = context.Request.Headers.Accept;
        if (StringValues.IsNullOrEmpty(acceptHeader))
        {
            return ZarrFormatChoice.CoverageJson;
        }

        if (!ContentNegotiationHelpers.TrySelectBestMediaType(AcceptableMediaTypes, acceptHeader, out var selected))
        {
            return new ZarrFormatChoice(ZarrCoverageFormat.CoverageJson, StandardErrorHelpers.CreateNotAcceptable(
                context,
                "Zarr-backed coverage collections serve application/prs.coverage+json or image/tiff; application=geotiff."));
        }

        return string.Equals(selected, GeoTiffContentType, StringComparison.OrdinalIgnoreCase)
            ? ZarrFormatChoice.GeoTiff
            : ZarrFormatChoice.CoverageJson;
    }

    private static bool TryResolveVariable(HttpContext context, out string? variable, out string? error)
    {
        variable = null;
        error = null;
        var properties = OgcCommonUtilities.GetQueryValue(context.Request, "properties");
        if (string.IsNullOrWhiteSpace(properties))
        {
            return true;
        }

        var tokens = properties.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 1)
        {
            error = "properties must select exactly one variable per request for Zarr-backed collections.";
            return false;
        }

        variable = tokens[0];
        return true;
    }

    private static bool TryApplyTemporalSubset(
        HttpContext context,
        ZarrStoreMetadata metadata,
        List<ZarrCoverageDimensionSubset> subsets,
        out string? error)
    {
        error = null;
        var datetime = OgcCommonUtilities.GetQueryValue(context.Request, "datetime");
        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        if (metadata.TemporalDimension is not { } timeDimension || metadata.Temporal is not { } temporal)
        {
            error = "The datetime parameter is not supported for this coverage collection because it does not declare a time axis.";
            return false;
        }

        if (subsets.Any(subset => string.Equals(subset.Dimension, timeDimension, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Specify the time dimension '{timeDimension}' with either datetime or subset, not both.";
            return false;
        }

        if (!OgcTemporalFilterParser.TryParseRange(datetime, out var reqStart, out var reqEnd, out var parseError))
        {
            error = parseError ?? "Invalid datetime parameter.";
            return false;
        }

        if (reqStart is null && reqEnd is null)
        {
            return true;
        }

        if (!CfTimeAxisIndexer.TryResolveTimeIndexRange(
                temporal.Start,
                temporal.End,
                temporal.StepCount,
                reqStart,
                reqEnd,
                out var low,
                out var high,
                out var indexError))
        {
            error = indexError ?? "The requested datetime could not be resolved against the coverage time axis.";
            return false;
        }

        // OGC subset bounds are closed intervals; the planner expects an exclusive stop.
        subsets.Add(new ZarrCoverageDimensionSubset(timeDimension, low, high + 1));
        return true;
    }

    private static bool TryParseSubsets(
        StringValues raw,
        ZarrStoreMetadata metadata,
        out List<ZarrCoverageDimensionSubset> subsets,
        out string? error)
    {
        subsets = [];
        error = null;

        foreach (var value in raw)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var segment in SplitTopLevel(value))
            {
                // A subset targeting a declared additional coordinate axis
                // (vertical/elevation/named) is resolved from coordinate values to a
                // grid-index slice through the shared coordinate-axis indexer.
                if (TryMatchCoordinateAxis(metadata, segment, out var axis))
                {
                    if (!TryResolveCoordinateSubset(axis!, segment, out var resolved, out error))
                    {
                        return false;
                    }
                    subsets.Add(resolved!);
                    continue;
                }

                if (!TryParseSubsetSegment(segment, out var subset, out error))
                {
                    return false;
                }
                subsets.Add(subset!);
            }
        }

        return true;
    }

    private static bool TryMatchCoordinateAxis(ZarrStoreMetadata metadata, string segment, out ZarrAxis? axis)
    {
        axis = null;
        if (metadata.Axes.Length == 0)
        {
            return false;
        }

        var trimmed = segment.Trim();
        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0)
        {
            return false;
        }

        var dimension = trimmed[..open].Trim();
        axis = metadata.Axes.FirstOrDefault(candidate => string.Equals(candidate.Name, dimension, StringComparison.OrdinalIgnoreCase));
        return axis is not null;
    }

    private static bool TryResolveCoordinateSubset(
        ZarrAxis axis,
        string segment,
        out ZarrCoverageDimensionSubset? subset,
        out string? error)
    {
        subset = null;
        error = null;
        var trimmed = segment.Trim();

        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            error = $"Invalid subset expression '{trimmed}'. Use subset={axis.Name}(value) or subset={axis.Name}(low:high).";
            return false;
        }

        var spec = trimmed[(open + 1)..^1].Trim();
        if (spec.Length == 0)
        {
            error = $"Invalid subset expression '{trimmed}'. Use subset={axis.Name}(value) or subset={axis.Name}(low:high).";
            return false;
        }

        double? low;
        double? high;
        var separator = spec.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            if (!TryParseCoordinate(spec, out var instant))
            {
                error = CreateCoordinateError(trimmed, axis.Name);
                return false;
            }
            low = instant;
            high = instant;
        }
        else
        {
            var lowText = spec[..separator].Trim();
            var highText = spec[(separator + 1)..].Trim();

            // Open-ended bounds: `axis(*:high)` / `axis(low:*)` per OGC subsetting.
            low = ParseOpenBound(lowText, out var lowOk);
            high = ParseOpenBound(highText, out var highOk);
            if (!lowOk || !highOk)
            {
                error = CreateCoordinateError(trimmed, axis.Name);
                return false;
            }
        }

        if (!CfCoordinateAxisIndexer.TryResolveIndexRange(axis, low, high, out var lowIndex, out var highIndex, out var indexError))
        {
            error = indexError ?? $"The requested subset on axis '{axis.Name}' could not be resolved.";
            return false;
        }

        // OGC subset bounds are closed intervals; the planner expects an exclusive stop.
        subset = new ZarrCoverageDimensionSubset(axis.Name, lowIndex, highIndex + 1);
        return true;
    }

    private static double? ParseOpenBound(string text, out bool ok)
    {
        if (text == "*" || text.Length == 0)
        {
            ok = true;
            return null;
        }
        ok = TryParseCoordinate(text, out var value);
        return ok ? value : null;
    }

    private static bool TryParseCoordinate(string value, out double coordinate)
        => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out coordinate)
           && !double.IsNaN(coordinate) && !double.IsInfinity(coordinate);

    private static string CreateCoordinateError(string segment, string axisName)
        => $"Subset bounds in '{segment}' must be coordinate values for axis '{axisName}' (e.g. {axisName}(850) or {axisName}(500:1000)).";

    private static List<string> SplitTopLevel(string value)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    segments.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }
        segments.Add(value[start..]);
        return segments;
    }

    private static bool TryParseSubsetSegment(
        string segment,
        out ZarrCoverageDimensionSubset? subset,
        out string? error)
    {
        subset = null;
        error = null;
        var trimmed = segment.Trim();

        var open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            error = $"Invalid subset expression '{trimmed}'. Use subset=dimension(low:high) with grid indices.";
            return false;
        }

        var dimension = trimmed[..open].Trim();
        var spec = trimmed[(open + 1)..^1].Trim();
        if (dimension.Length == 0 || spec.Length == 0)
        {
            error = $"Invalid subset expression '{trimmed}'. Use subset=dimension(low:high) with grid indices.";
            return false;
        }

        int low;
        int high;
        var separator = spec.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            if (!TryParseIndex(spec, out low))
            {
                error = CreateIndexError(trimmed);
                return false;
            }
            high = low;
        }
        else
        {
            if (!TryParseIndex(spec[..separator].Trim(), out low) ||
                !TryParseIndex(spec[(separator + 1)..].Trim(), out high))
            {
                error = CreateIndexError(trimmed);
                return false;
            }
        }

        if (high < low)
        {
            error = $"Subset bounds for '{dimension}' must satisfy low <= high.";
            return false;
        }

        // OGC subset bounds are closed intervals; convert to exclusive stop.
        subset = new ZarrCoverageDimensionSubset(dimension, low, high + 1);
        return true;
    }

    private static string CreateIndexError(string segment)
        => $"Subset bounds in '{segment}' must be non-negative integer grid indices. Coordinate-based subsetting is not yet supported for Zarr-backed collections.";

    private static bool TryParseIndex(string value, out int index)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0;

    private static (List<CoverageJsonAxis> Axes, string? XAxis, string? YAxis) BuildAxes(
        ZarrStoreMetadata metadata,
        ZarrCoverageSubsetPlan plan)
    {
        var array = plan.Array;
        var request = plan.Request;
        var georeferenced = metadata.Srid > 0;
        var extent = metadata.Extent;

        string? xAxis = null;
        string? yAxis = null;
        var axes = new List<CoverageJsonAxis>(array.Shape.Length);
        for (var i = 0; i < array.Shape.Length; i++)
        {
            var name = array.DimensionNames[i];
            var count = request.Stop[i] - request.Start[i];

            if (georeferenced && IsXDimension(metadata, name) && array.Shape[i] > 0)
            {
                xAxis = name;
                var cellWidth = (extent.XMax - extent.XMin) / array.Shape[i];
                axes.Add(new CoverageJsonAxis(
                    name,
                    extent.XMin + ((request.Start[i] + 0.5) * cellWidth),
                    extent.XMin + ((request.Stop[i] - 0.5) * cellWidth),
                    count));
                continue;
            }

            if (georeferenced && IsYDimension(metadata, name) && array.Shape[i] > 0)
            {
                yAxis = name;
                var cellHeight = (extent.YMax - extent.YMin) / array.Shape[i];
                if (metadata.YAxisAscending)
                {
                    axes.Add(new CoverageJsonAxis(
                        name,
                        extent.YMin + ((request.Start[i] + 0.5) * cellHeight),
                        extent.YMin + ((request.Stop[i] - 0.5) * cellHeight),
                        count));
                }
                else
                {
                    axes.Add(new CoverageJsonAxis(
                        name,
                        extent.YMax - ((request.Start[i] + 0.5) * cellHeight),
                        extent.YMax - ((request.Stop[i] - 0.5) * cellHeight),
                        count));
                }
                continue;
            }

            axes.Add(new CoverageJsonAxis(name, request.Start[i], request.Stop[i] - 1, count));
        }

        return (axes, xAxis, yAxis);
    }

    private static bool IsXDimension(ZarrStoreMetadata metadata, string name)
        => metadata.SpatialXDimension is { } declared
            ? string.Equals(declared, name, StringComparison.OrdinalIgnoreCase)
            : name.ToLowerInvariant() is "x" or "lon" or "longitude";

    private static bool IsYDimension(ZarrStoreMetadata metadata, string name)
        => metadata.SpatialYDimension is { } declared
            ? string.Equals(declared, name, StringComparison.OrdinalIgnoreCase)
            : name.ToLowerInvariant() is "y" or "lat" or "latitude";

    private static void WriteCoverageHeaders(HttpContext context, ZarrStoreMetadata metadata, ZarrCoverageSubsetPlan plan)
    {
        if (metadata.Srid <= 0)
        {
            return;
        }

        var array = plan.Array;
        var request = plan.Request;
        var extent = metadata.Extent;
        double? xMin = null, xMax = null, yMin = null, yMax = null;
        for (var i = 0; i < array.Shape.Length; i++)
        {
            if (array.Shape[i] <= 0)
            {
                continue;
            }

            var name = array.DimensionNames[i];
            if (IsXDimension(metadata, name))
            {
                var cellWidth = (extent.XMax - extent.XMin) / array.Shape[i];
                xMin = extent.XMin + (request.Start[i] * cellWidth);
                xMax = extent.XMin + (request.Stop[i] * cellWidth);
            }
            else if (IsYDimension(metadata, name))
            {
                var cellHeight = (extent.YMax - extent.YMin) / array.Shape[i];
                if (metadata.YAxisAscending)
                {
                    yMin = extent.YMin + (request.Start[i] * cellHeight);
                    yMax = extent.YMin + (request.Stop[i] * cellHeight);
                }
                else
                {
                    yMax = extent.YMax - (request.Start[i] * cellHeight);
                    yMin = extent.YMax - (request.Stop[i] * cellHeight);
                }
            }
        }

        if (xMin.HasValue && xMax.HasValue && yMin.HasValue && yMax.HasValue)
        {
            context.Response.Headers.Append(
                "Content-Bbox",
                string.Join(
                    ",",
                    FormatDouble(xMin.Value),
                    FormatDouble(yMin.Value),
                    FormatDouble(xMax.Value),
                    FormatDouble(yMax.Value)));
        }

        var contentCrs = metadata.Srid == 4326
            ? OgcFeaturesUtilities.Crs84Uri
            : CreateEpsgUri(metadata.Srid);
        context.Response.Headers.Append("Content-Crs", $"<{contentCrs}>");
    }

    private static bool IsFloatingPoint(string dtype)
        => dtype.Length >= 2 && dtype[0] is '<' or '|' or '=' ? dtype[1] == 'f' : dtype.StartsWith('f');

    private static string CreateEpsgUri(int srid)
        => FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{srid}");

    private static string FormatDouble(double value)
        => value.ToString("G17", CultureInfo.InvariantCulture);
}
