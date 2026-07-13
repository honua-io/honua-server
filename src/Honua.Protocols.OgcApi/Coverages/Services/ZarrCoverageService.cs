// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Serves registered Zarr stores through the OGC API Coverages surface
/// (read-only, chunked-read access pattern, #1590). Adapts coverage collection
/// metadata, field schemas, and grid-index subset requests onto the shared
/// Zarr read pipeline (<see cref="ZarrCoverageSubsetPlanner"/> +
/// <see cref="IZarrSubsetReader"/>); it never builds a parallel data path.
/// </summary>
internal sealed class ZarrCoverageService
{
    /// <summary>
    /// CoverageJSON media type served for Zarr-backed collections.
    /// </summary>
    internal const string CoverageJsonContentType = "application/prs.coverage+json";

    /// <summary>
    /// Raw decoded payload cap. CoverageJSON expands roughly 3-4x over the
    /// binary buffer, so this keeps responses comfortably bounded.
    /// </summary>
    private const long MaxCoverageOutputBytes = 16L * 1024L * 1024L;

    private static readonly ImmutableHashSet<string> SupportedQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f", "subset", "properties", "datetime");

    private static readonly string[] AcceptableMediaTypes = [CoverageJsonContentType, MediaTypes.Json];

    private readonly IZarrStore _zarrStore;
    private readonly IZarrSubsetReader _subsetReader;
    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ILogger<ZarrCoverageService> _logger;

    public ZarrCoverageService(
        IZarrStore zarrStore,
        IZarrSubsetReader subsetReader,
        IEnumerable<ICloudRangeReader> rangeReaders,
        ILogger<ZarrCoverageService> logger)
    {
        _zarrStore = zarrStore ?? throw new ArgumentNullException(nameof(zarrStore));
        _subsetReader = subsetReader ?? throw new ArgumentNullException(nameof(subsetReader));
        _rangeReaders = rangeReaders ?? throw new ArgumentNullException(nameof(rangeReaders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the first registered Zarr store for the storage layer whose
    /// metadata scan has completed, or null when the layer has no servable store.
    /// </summary>
    public async Task<ZarrRegistration?> FindServableRegistrationAsync(int storageLayerId, CancellationToken cancellationToken)
    {
        var registrations = await _zarrStore.ListByLayerAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        return registrations.FirstOrDefault(registration => registration.Metadata is not null);
    }

    /// <summary>
    /// Builds the OGC API Coverages collection document for a Zarr-backed layer.
    /// </summary>
    public static OgcCoverageCollection CreateCollection(
        MetadataV2Resource resource,
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
            DefaultFields = metadata.Arrays.Select(static array => array.Name).ToImmutableArray()
        };
    }

    /// <summary>
    /// Builds the field-selection schema for a Zarr-backed collection: one
    /// selectable property per discovered variable.
    /// </summary>
    public static CoverageSchema CreateSchema(MetadataV2Resource resource, int storageLayerId, ZarrRegistration registration)
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
                NoDataValue = array.FillValue as double?
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

    /// <summary>
    /// Serves a CoverageJSON subset of a Zarr-backed collection. Supports
    /// <c>properties</c> single-variable selection and OGC-style
    /// <c>subset=dim(low:high)</c> parameters expressed as inclusive grid indices.
    /// </summary>
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

        var unsupportedParameter = context.Request.Query.Keys.FirstOrDefault(parameter => !SupportedQueryParameters.Contains(parameter));
        if (unsupportedParameter is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Parameter '{unsupportedParameter}' is not supported for Zarr-backed coverage collections. Use properties and subset=dim(low:high) grid-index subsetting.");
        }

        if (!TryNegotiateFormat(context, f, out var formatError, out var notAcceptable))
        {
            return notAcceptable
                ? StandardErrorHelpers.CreateNotAcceptable(context, formatError!)
                : StandardErrorHelpers.CreateBadRequest(context, formatError!);
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

        var rangeReader = _rangeReaders.FirstOrDefault(reader => reader.Provider == registration.Provider);
        if (rangeReader is null)
        {
            ZarrCoverageLog.RangeReaderMissing(_logger, collectionId, registration.Provider.ToString());
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "The storage backend for this coverage collection is not configured.");
        }

        ZarrSubsetResult result;
        try
        {
            result = await _subsetReader.ReadSubsetAsync(
                    rangeReader,
                    registration.Bucket,
                    registration.RootPath,
                    metadata,
                    plan!.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Subset reader validation messages are client-safe (codec, dtype, bounds, caps).
            ZarrCoverageLog.SubsetRejected(_logger, collectionId, ex.Message);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            ZarrCoverageLog.SubsetReadFailed(_logger, ex, collectionId);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "The Zarr store backing this coverage collection returned invalid data.");
        }

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
            origin = ImmutableArray.Create(extent.XMin, extent.YMax);
            resolution = ImmutableArray.Create(
                (extent.XMax - extent.XMin) / width,
                (extent.YMin - extent.YMax) / height);
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

    private static bool TryNegotiateFormat(HttpContext context, string? f, out string? error, out bool notAcceptable)
    {
        error = null;
        notAcceptable = false;

        if (!string.IsNullOrWhiteSpace(f))
        {
            switch (f.Trim().ToLowerInvariant())
            {
                case "covjson":
                case "coveragejson":
                case "json":
                case CoverageJsonContentType:
                    return true;
                default:
                    error = $"Unsupported coverage format '{f}' for a Zarr-backed collection. Zarr-backed collections currently serve CoverageJSON; use f=covjson.";
                    return false;
            }
        }

        var acceptHeader = context.Request.Headers.Accept;
        if (!StringValues.IsNullOrEmpty(acceptHeader) &&
            !ContentNegotiationHelpers.TrySelectBestMediaType(AcceptableMediaTypes, acceptHeader, out _))
        {
            error = "Zarr-backed coverage collections serve application/prs.coverage+json.";
            notAcceptable = true;
            return false;
        }

        return true;
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

    /// <summary>
    /// Translates an OGC <c>datetime</c> query parameter into a synthetic
    /// grid-index subset over the store's time dimension and appends it to
    /// <paramref name="subsets"/>. No-ops when <c>datetime</c> is absent. Requires
    /// the store to declare a resolvable time axis, and rejects a request that
    /// also subsets the time dimension explicitly via <c>subset=</c>.
    /// </summary>
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
                // grid-index slice through the shared coordinate-axis indexer (#1872).
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

    /// <summary>
    /// Returns true when <paramref name="segment"/> targets a declared additional
    /// coordinate axis (not a spatial or temporal axis), yielding the matched axis.
    /// </summary>
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

    /// <summary>
    /// Resolves a coordinate-valued subset (<c>axis(value)</c> or
    /// <c>axis(low:high)</c>) on a declared additional axis to an inclusive
    /// grid-index slice via <see cref="CfCoordinateAxisIndexer"/>.
    /// </summary>
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
                // Row 0 is the northernmost row (north-up convention).
                axes.Add(new CoverageJsonAxis(
                    name,
                    extent.YMax - ((request.Start[i] + 0.5) * cellHeight),
                    extent.YMax - ((request.Stop[i] - 0.5) * cellHeight),
                    count));
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
                yMax = extent.YMax - (request.Start[i] * cellHeight);
                yMin = extent.YMax - (request.Stop[i] * cellHeight);
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

        if (metadata.Srid != 4326)
        {
            context.Response.Headers.Append(
                "Content-Crs",
                FormattableString.Invariant($"<https://www.opengis.net/def/crs/EPSG/0/{metadata.Srid}>"));
        }
    }

    private static bool IsFloatingPoint(string dtype)
        => dtype.Length >= 2 && dtype[0] is '<' or '|' or '=' ? dtype[1] == 'f' : dtype.StartsWith('f');

    private static string CreateEpsgUri(int srid)
        => FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{srid}");

    private static string FormatDouble(double value)
        => value.ToString("G17", CultureInfo.InvariantCulture);
}
