// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Edr.Models;
using Honua.Protocols.Ogc.Common;

namespace Honua.Protocols.Ogc.Api.Edr;

/// <summary>
/// OGC API - Environmental Data Retrieval (EDR) handler (#1757). Adapts EDR
/// <c>position</c> (point time-series) and <c>cube</c> (area/subset) queries onto the
/// registered coverage/datacube catalog: collections are the same storage layers exposed
/// through OGC API - Coverages, point sampling rides <see cref="IRasterStore.IdentifyAsync"/>,
/// and cube subsetting samples the canonical raster read pipeline on a bounded grid. Results
/// are returned as CoverageJSON.
/// </summary>
internal sealed class EdrHandler
{
    private const string CoveragesProtocol = "OGC-API-Coverages";
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string CoverageJsonMediaType = "application/prs.coveragejson+json";
    private const string CoverageJsonFormat = "CoverageJSON";
    private const string Crs84Code = "CRS84";

    // Every output format and CRS a collection advertises (output_formats / crs). The position
    // and cube queries bind `f` and `crs` to these lists, so an unadvertised value is a 400
    // rather than a silently CoverageJSON/CRS84 answer (#4153).
    private static readonly ImmutableArray<string> OutputFormats = ImmutableArray.Create(CoverageJsonFormat);
    private static readonly ImmutableArray<string> SupportedCrs = ImmutableArray.Create(Crs84Code);

    // Bound the cube sampling grid so an unbounded bbox cannot fan out into a huge
    // number of point-sample reads; EDR cube here is a sampled subset, not a full export.
    private const int MaxCubeAxisSamples = 50;
    private const int DefaultCubeAxisSamples = 8;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ICoordinateTransformService _coordinateTransformService;

    public EdrHandler(EdrDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _graphProvider = dependencies.GraphProvider;
        _rasterStore = dependencies.RasterStore;
        _coordinateTransformService = dependencies.CoordinateTransformService;
    }

    public static IResult GetLandingPage(HttpContext context)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/edr";
        var links = ImmutableArray.Create(
            Link.Create(basePath, RelationTypes.Self, MediaTypes.Json, "EDR landing page"),
            Link.Create($"{basePath}/conformance", RelationTypes.Conformance, MediaTypes.Json, "Conformance"),
            Link.Create($"{basePath}/collections", RelationTypes.Data, MediaTypes.Json, "EDR collections"));
        return Results.Json(new EdrLandingPage { Links = links }, EdrJsonContext.Default.EdrLandingPage);
    }

    public static IResult GetConformance()
    {
        var conformsTo = ImmutableArray.Create(
            "http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/core",
            "http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/position",
            "http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/cube",
            "http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/json",
            "http://www.opengis.net/spec/ogcapi-edr-1/1.1/conf/covjson");
        return Results.Json(new EdrConformance { ConformsTo = conformsTo }, EdrJsonContext.Default.EdrConformance);
    }

    public async Task<IResult> GetCollectionsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var byResource = new Dictionary<string, (MetadataV2Publication Publication, MetadataV2Service Service, MetadataV2Resource Resource)>(StringComparer.Ordinal);
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !IsProtocolEnabled(service, CoveragesProtocol))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (!snapshot.IsRoutable(publication) ||
                !AccessPolicyHelpers.IsResourceAccessible(context, resource!, service))
            {
                continue;
            }

            if (!byResource.TryGetValue(resource!.Metadata.Id, out var existing) ||
                (publication.IsPrimary && !existing.Publication.IsPrimary))
            {
                byResource[resource.Metadata.Id] = (publication, service, resource!);
            }
        }

        var collections = ImmutableArray.CreateBuilder<EdrCollection>();
        foreach (var entry in byResource.Values.OrderBy(t => snapshot.ResolveStorageLayerId(t.Publication) ?? int.MaxValue))
        {
            var storageLayerId = snapshot.ResolveStorageLayerId(entry.Publication);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            var raster = await GetRasterAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
            if (raster is null)
            {
                continue;
            }

            collections.Add(await BuildCollectionAsync(entry.Resource, storageLayerId.Value, raster.Value, baseUrl, cancellationToken)
                .ConfigureAwait(false));
        }

        var links = ImmutableArray.Create(
            Link.Create($"{baseUrl}/edr/collections", RelationTypes.Self, MediaTypes.Json, "EDR collections"),
            Link.Create($"{baseUrl}/edr", "parent", MediaTypes.Json, "Landing page"));

        return Results.Json(
            new EdrCollections { Collections = collections.ToImmutable(), Links = links },
            EdrJsonContext.Default.EdrCollections);
    }

    public async Task<IResult> GetCollectionAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var collection = await BuildCollectionAsync(resolution.Resource!, resolution.StorageLayerId, resolution.Raster, baseUrl, cancellationToken)
            .ConfigureAwait(false);
        return Results.Json(collection, EdrJsonContext.Default.EdrCollection);
    }

    public async Task<IResult> GetPositionAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var queryError = ValidateOutputFormatAndCrs(context);
        if (queryError is not null)
        {
            return queryError;
        }

        var coords = context.Request.Query["coords"].ToString();
        if (!TryParsePositions(coords, out var positions, out var isMultiPoint))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Query parameter 'coords' must be a WKT POINT or MULTIPOINT, e.g. coords=POINT(-122.4 37.8) or coords=MULTIPOINT((-122.4 37.8),(-122.3 37.7)).");
        }

        // coords are CRS84 lon/lat while RasterInfo.Extent is in the raster's storage CRS, so the
        // gate compares against the CRS84-transformed extent (#4149). Every MULTIPOINT member is
        // gated: a partial answer would be indistinguishable from a full one.
        var raster = resolution.Raster;
        var crs84Extent = await ResolveCrs84ExtentAsync(raster, cancellationToken).ConfigureAwait(false);
        if (positions.Any(position => !WithinExtent(crs84Extent, position.Lon, position.Lat)))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Requested position is outside the collection spatial extent.");
        }

        var instant = ResolveInstant(context.Request.Query["datetime"].ToString(), raster);
        var parameterError = ValidateRequestedParameters(context, raster, out var requestedParameters);
        if (parameterError is not null)
        {
            return parameterError;
        }

        var parameters = BuildParameters(raster, requestedParameters);
        if (!isMultiPoint)
        {
            var (lon, lat) = positions[0];
            var coverage = await SamplePointSeriesAsync(resolution, parameters, lon, lat, cancellationToken).ConfigureAwait(false);

            // The single time instant is carried on the domain via a t-axis serialized as a
            // CoverageJSON time axis (ISO-8601 string values) alongside the numeric x/y axes.
            return CoverageJsonResult(ToCoverageNode(coverage, instant));
        }

        // EDR 1.1 position: a MULTIPOINT answers with a CoverageJSON CoverageCollection holding one
        // PointSeries coverage per requested position, in request order.
        var coverages = new System.Text.Json.Nodes.JsonArray();
        foreach (var (lon, lat) in positions)
        {
            var coverage = await SamplePointSeriesAsync(resolution, parameters, lon, lat, cancellationToken).ConfigureAwait(false);
            coverages.Add(ToCoverageNode(coverage, instant));
        }

        var parametersNode = coverages[0]!["parameters"]!.DeepClone();
        var collection = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "CoverageCollection",
            ["domainType"] = "PointSeries",
            ["parameters"] = parametersNode,
            ["coverages"] = coverages
        };
        return CoverageJsonResult(collection);
    }

    private async Task<CoverageJson> SamplePointSeriesAsync(
        EdrResolution resolution,
        ImmutableDictionary<string, EdrParameter> parameters,
        double lon,
        double lat,
        CancellationToken cancellationToken)
    {
        var pixel = await _rasterStore
            .IdentifyAsync(resolution.StorageLayerId, resolution.Raster.Id, lon, lat, srid: 4326, rendering: null, cancellationToken)
            .ConfigureAwait(false);

        var ranges = ImmutableDictionary.CreateBuilder<string, CoverageJsonRange>();
        foreach (var parameterName in parameters.Keys)
        {
            var band = BandIndex(parameterName);
            var value = pixel.BandValues.TryGetValue(band, out var raw) ? ToDouble(raw) : null;
            ranges[parameterName] = new CoverageJsonRange
            {
                AxisNames = ImmutableArray.Create("t"),
                Shape = ImmutableArray.Create(1),
                Values = ImmutableArray.Create(value)
            };
        }

        var domain = new CoverageJsonDomain
        {
            DomainType = "PointSeries",
            Axes = ImmutableDictionary<string, CoverageJsonAxis>.Empty
                .Add("x", new CoverageJsonAxis { Values = ImmutableArray.Create(lon) })
                .Add("y", new CoverageJsonAxis { Values = ImmutableArray.Create(lat) }),
            Referencing = BuildReferencing()
        };

        return new CoverageJson
        {
            Domain = domain,
            Parameters = parameters,
            Ranges = ranges.ToImmutable()
        };
    }

    public async Task<IResult> GetCubeAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var queryError = ValidateOutputFormatAndCrs(context);
        if (queryError is not null)
        {
            return queryError;
        }

        if (!TryParseBbox(context.Request.Query["bbox"].ToString(), out var minX, out var minY, out var maxX, out var maxY))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Query parameter 'bbox' must be 'minLon,minLat,maxLon,maxLat'.");
        }

        var resolutionCount = ResolveCubeSampleCount(context.Request.Query["resolution-x"].ToString());
        var raster = resolution.Raster;
        var instant = ResolveInstant(context.Request.Query["datetime"].ToString(), raster);
        var parameterError = ValidateRequestedParameters(context, raster, out var requestedParameters);
        if (parameterError is not null)
        {
            return parameterError;
        }

        var parameters = BuildParameters(raster, requestedParameters);

        // Sample a bounded regular grid of cell centres inside the bbox using the canonical
        // point-sample pipeline; this returns genuine cube values without decoding raster bytes.
        var xValues = AxisValues(minX, maxX, resolutionCount);
        var yValues = AxisValues(minY, maxY, resolutionCount);

        var samples = new Dictionary<string, double?[]>(StringComparer.Ordinal);
        foreach (var parameterName in parameters.Keys)
        {
            samples[parameterName] = new double?[xValues.Length * yValues.Length];
        }

        var index = 0;
        for (var yi = 0; yi < yValues.Length; yi++)
        {
            for (var xi = 0; xi < xValues.Length; xi++)
            {
                var pixel = await _rasterStore
                    .IdentifyAsync(resolution.StorageLayerId, raster.Id, xValues[xi], yValues[yi], srid: 4326, rendering: null, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var parameterName in parameters.Keys)
                {
                    var band = BandIndex(parameterName);
                    samples[parameterName][index] = pixel.BandValues.TryGetValue(band, out var raw) ? ToDouble(raw) : null;
                }

                index++;
            }
        }

        var ranges = ImmutableDictionary.CreateBuilder<string, CoverageJsonRange>();
        foreach (var (parameterName, values) in samples)
        {
            ranges[parameterName] = new CoverageJsonRange
            {
                AxisNames = ImmutableArray.Create("y", "x"),
                Shape = ImmutableArray.Create(yValues.Length, xValues.Length),
                Values = values.ToImmutableArray()
            };
        }

        var domain = new CoverageJsonDomain
        {
            DomainType = "Grid",
            Axes = ImmutableDictionary<string, CoverageJsonAxis>.Empty
                .Add("x", new CoverageJsonAxis { Values = xValues.ToImmutableArray() })
                .Add("y", new CoverageJsonAxis { Values = yValues.ToImmutableArray() }),
            Referencing = BuildReferencing()
        };

        var coverage = new CoverageJson
        {
            Domain = domain,
            Parameters = parameters,
            Ranges = ranges.ToImmutable()
        };

        return CoverageJsonResult(ToCoverageNode(coverage, instant));
    }

    // ---- resolution ----

    private readonly record struct EdrResolution(
        MetadataV2Resource? Resource,
        int StorageLayerId,
        RasterInfo Raster,
        IResult? Error);

    private async Task<EdrResolution> ResolveAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context, collectionId, requiredProtocol: CoveragesProtocol, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new EdrResolution(null, 0, default, validation.ErrorResult);
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = snapshot.ResolveStorageLayerId(validation.Publication!);
        if (!storageLayerId.HasValue)
        {
            return new EdrResolution(null, 0, default,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' does not expose a coverage."));
        }

        var raster = await GetRasterAsync(storageLayerId.Value, cancellationToken).ConfigureAwait(false);
        if (raster is null)
        {
            return new EdrResolution(null, 0, default,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' does not expose a coverage."));
        }

        return new EdrResolution(validation.Resource!, storageLayerId.Value, raster.Value, null);
    }

    private async Task<RasterInfo?> GetRasterAsync(int layerId, CancellationToken cancellationToken)
    {
        var raster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (raster is null)
        {
            return null;
        }

        if (raster.Value.Extent is null)
        {
            var extent = await _rasterStore.GetExtentAsync(layerId, raster.Value.Id, cancellationToken).ConfigureAwait(false);
            if (extent.HasValue)
            {
                raster = raster.Value with { Extent = extent };
            }
        }

        return raster;
    }

    // ---- metadata building ----

    private async Task<EdrCollection> BuildCollectionAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        RasterInfo raster,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var collectionId = storageLayerId.ToString(CultureInfo.InvariantCulture);
        var basePath = $"{baseUrl}/edr/collections/{Uri.EscapeDataString(collectionId)}";
        var outputFormats = OutputFormats;

        Extent? extent = null;
        if (raster.Extent is { } e)
        {
            // The storage-CRS extent is advertised in CRS84 (#4149). If the storage CRS cannot be
            // transformed, the native bbox is labelled with its own EPSG CRS instead of CRS84, as
            // OGC API - Coverages does for the same layer.
            var crs84Extent = await ResolveCrs84ExtentAsync(raster, cancellationToken).ConfigureAwait(false);
            extent = new Extent
            {
                Spatial = crs84Extent is { } bbox
                    ? new SpatialExtent
                    {
                        BoundingBox = ImmutableArray.Create(
                            ImmutableArray.Create(bbox.MinLon, bbox.MinLat, bbox.MaxLon, bbox.MaxLat)),
                        Crs = Crs84
                    }
                    : new SpatialExtent
                    {
                        BoundingBox = ImmutableArray.Create(
                            ImmutableArray.Create(e.XMin, e.YMin, e.XMax, e.YMax)),
                        Crs = string.Create(
                            CultureInfo.InvariantCulture,
                            $"http://www.opengis.net/def/crs/EPSG/0/{ResolveStorageSrid(raster)}")
                    }
            };
        }

        var links = ImmutableArray.Create(
            Link.Create(basePath, RelationTypes.Self, MediaTypes.Json, resource.Metadata.Title ?? resource.Metadata.Name),
            Link.Create($"{baseUrl}/edr/collections", "parent", MediaTypes.Json, "EDR collections"),
            Link.Create($"{basePath}/position", "data", CoverageJsonMediaType, "Position query"),
            Link.Create($"{basePath}/cube", "data", CoverageJsonMediaType, "Cube query"));

        var variables = new EdrQueryVariables { QueryType = "position", OutputFormats = outputFormats };
        var dataQueries = new EdrDataQueries
        {
            Position = new EdrDataQuery
            {
                Link = new EdrQueryLink { Href = $"{basePath}/position", Variables = variables }
            },
            Cube = new EdrDataQuery
            {
                Link = new EdrQueryLink
                {
                    Href = $"{basePath}/cube",
                    Variables = variables with { QueryType = "cube" }
                }
            }
        };

        return new EdrCollection
        {
            Id = collectionId,
            Title = resource.Metadata.Title ?? resource.Metadata.Name,
            Description = resource.Metadata.Description,
            Links = links,
            Extent = extent,
            DataQueries = dataQueries,
            Crs = SupportedCrs,
            OutputFormats = outputFormats,
            ParameterNames = BuildParameters(raster, requested: null)
        };
    }

    private static ImmutableDictionary<string, EdrParameter> BuildParameters(RasterInfo raster, IReadOnlyCollection<string>? requested)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, EdrParameter>();
        var available = AvailableParameterNames(raster);
        for (var index = 0; index < available.Length; index++)
        {
            var name = available[index];
            if (requested is { Count: > 0 } && !requested.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            builder[name] = new EdrParameter
            {
                Description = $"Coverage band {(index + 1).ToString(CultureInfo.InvariantCulture)}",
                ObservedProperty = new EdrObservedProperty { Label = name }
            };
        }

        // No empty-result fallback: query paths validate 'parameter-name' against
        // AvailableParameterNames first (see ValidateRequestedParameters), so a
        // surviving request always selects at least one offered parameter. Substituting
        // band_1 for an unmatched selection would return a different physical quantity
        // than the client asked for (#3184).
        return builder.ToImmutable();
    }

    /// <summary>
    /// The enumerated <c>parameter-name</c> options this collection offers, matching the
    /// <c>parameter_names</c> keys advertised in the collection metadata response.
    /// </summary>
    private static ImmutableArray<string> AvailableParameterNames(RasterInfo raster)
    {
        var bandCount = Math.Max(1, raster.BandCount);
        var names = ImmutableArray.CreateBuilder<string>(bandCount);
        for (var band = 1; band <= bandCount; band++)
        {
            names.Add($"band_{band.ToString(CultureInfo.InvariantCulture)}");
        }

        return names.MoveToImmutable();
    }

    /// <summary>
    /// Validates the EDR <c>parameter-name</c> query parameter for the position and cube
    /// query paths and yields the requested names when they are all offered by the collection.
    /// </summary>
    /// <remarks>
    /// OGC API - EDR <c>/req/edr/parameter-name-response</c> requires (A) that only the listed
    /// parameters be returned and (B) that the value be drawn from the enumerated option list in
    /// the collection metadata. A name outside that list is therefore an invalid query-parameter
    /// value, which OGC API - Common Part 1 (8.1.3) maps to <c>400</c>. Honua rejects strictly:
    /// a request carrying any unknown name fails even when other names in the same list are
    /// valid, so a client can never receive a silently narrowed selection that is
    /// indistinguishable from a full result (#3184).
    /// </remarks>
    private static IResult? ValidateRequestedParameters(HttpContext context, RasterInfo raster, out string[] requested)
    {
        requested = ParseParameterNames(context.Request.Query["parameter-name"].ToString());
        if (requested.Length == 0)
        {
            return null;
        }

        var available = AvailableParameterNames(raster);
        var unknown = requested
            .Where(name => !available.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length == 0)
        {
            return null;
        }

        return StandardErrorHelpers.CreateBadRequest(
            context,
            $"Query parameter 'parameter-name' requests parameters this collection does not offer: {string.Join(", ", unknown)}.",
            [$"Available parameter names: {string.Join(", ", available)}."]);
    }

    /// <summary>
    /// Binds the position/cube <c>f</c> and <c>crs</c> query parameters to the collection's
    /// advertised <c>output_formats</c> and <c>crs</c> lists.
    /// </summary>
    /// <remarks>
    /// OGC API - Common Part 1 (8.1.3) maps an unsupported query-parameter value to <c>400</c>.
    /// Before #4153 the handler never read either parameter, so <c>f=csv</c> returned CoverageJSON
    /// and <c>crs=EPSG:3857</c> reinterpreted projected metres as CRS84 degrees.
    /// </remarks>
    private static IResult? ValidateOutputFormatAndCrs(HttpContext context)
    {
        var format = context.Request.Query["f"].ToString();
        if (!string.IsNullOrWhiteSpace(format) && !IsCoverageJsonFormat(format.Trim()))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Query parameter 'f' requests an output format this collection does not offer: {format}.",
                [$"Available output formats: {string.Join(", ", OutputFormats)}."]);
        }

        var crs = context.Request.Query["crs"].ToString();
        if (!string.IsNullOrWhiteSpace(crs) && !IsCrs84(crs.Trim()))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Query parameter 'crs' requests a coordinate reference system this collection does not offer: {crs}.",
                [$"Available crs: {string.Join(", ", SupportedCrs)}."]);
        }

        return null;
    }

    private static bool IsCoverageJsonFormat(string format) =>
        string.Equals(format, CoverageJsonFormat, StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, "covjson", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, CoverageJsonMediaType, StringComparison.OrdinalIgnoreCase);

    private static bool IsCrs84(string crs) =>
        string.Equals(crs, Crs84Code, StringComparison.OrdinalIgnoreCase)
        || string.Equals(crs, "OGC:CRS84", StringComparison.OrdinalIgnoreCase)
        || string.Equals(crs, "[OGC:CRS84]", StringComparison.OrdinalIgnoreCase)
        || string.Equals(crs, Crs84, StringComparison.OrdinalIgnoreCase)
        || string.Equals(crs, "https://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase);

    private static ImmutableArray<CoverageJsonReferencing> BuildReferencing() =>
        ImmutableArray.Create(
            new CoverageJsonReferencing
            {
                Coordinates = ImmutableArray.Create("x", "y"),
                System = new CoverageJsonReferenceSystem
                {
                    Type = "GeographicCRS",
                    Id = "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
                }
            },
            new CoverageJsonReferencing
            {
                Coordinates = ImmutableArray.Create("t"),
                System = new CoverageJsonReferenceSystem { Type = "TemporalRS", Id = "http://www.opengis.net/def/uom/ISO-8601/0/Gregorian" }
            });

    // ---- parsing helpers ----

    /// <summary>
    /// Parses the EDR position <c>coords</c> WKT: a <c>POINT</c>, or a <c>MULTIPOINT</c> in either
    /// the parenthesised member form <c>MULTIPOINT((x y),(x y))</c> shown in the EDR 1.1 examples or
    /// the bare form <c>MULTIPOINT(x y, x y)</c>.
    /// </summary>
    private static bool TryParsePositions(string coords, out (double Lon, double Lat)[] positions, out bool isMultiPoint)
    {
        positions = [];
        isMultiPoint = false;
        if (string.IsNullOrWhiteSpace(coords))
        {
            return false;
        }

        var trimmed = coords.Trim();
        if (!trimmed.StartsWith("MULTIPOINT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePoint(trimmed, out var lon, out var lat))
            {
                return false;
            }

            positions = [(lon, lat)];
            return true;
        }

        isMultiPoint = true;
        var body = trimmed["MULTIPOINT".Length..].Trim();
        if (body.Length < 2 || body[0] != '(' || body[^1] != ')')
        {
            return false;
        }

        var members = body[1..^1].Split(',', StringSplitOptions.TrimEntries);
        var parsed = new List<(double Lon, double Lat)>(members.Length);
        foreach (var member in members)
        {
            var inner = member;
            if (inner.StartsWith('(') && inner.EndsWith(')'))
            {
                inner = inner[1..^1].Trim();
            }

            var ordinates = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (ordinates.Length is < 2 or > 3
                || inner.Contains('(') || inner.Contains(')')
                || !double.TryParse(ordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || !double.TryParse(ordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                return false;
            }

            parsed.Add((lon, lat));
        }

        positions = [.. parsed];
        return positions.Length > 0;
    }

    private static bool TryParsePoint(string coords, out double lon, out double lat)
    {
        lon = 0;
        lat = 0;
        if (string.IsNullOrWhiteSpace(coords))
        {
            return false;
        }

        var trimmed = coords.Trim();
        var open = trimmed.IndexOf('(');
        var close = trimmed.IndexOf(')');
        if (!trimmed.StartsWith("POINT", StringComparison.OrdinalIgnoreCase) || open < 0 || close <= open)
        {
            return false;
        }

        var inner = trimmed[(open + 1)..close];
        var parts = inner.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
    }

    private static bool TryParseBbox(string bbox, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = maxX = maxY = 0;
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return false;
        }

        var parts = bbox.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out maxX) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out maxY))
        {
            return false;
        }

        return maxX > minX && maxY > minY;
    }

    private static string[] ParseParameterNames(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ResolveInstant(string datetime, RasterInfo raster)
    {
        if (!string.IsNullOrWhiteSpace(datetime) &&
            DateTimeOffset.TryParse(
                datetime.Split('/')[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return Iso(parsed);
        }

        return Iso(raster.AcquisitionDate ?? raster.CreatedAt);
    }

    private static int ResolveCubeSampleCount(string resolutionX)
    {
        if (int.TryParse(resolutionX, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            return Math.Min(count, MaxCubeAxisSamples);
        }

        return DefaultCubeAxisSamples;
    }

    private static double[] AxisValues(double min, double max, int count)
    {
        if (count <= 1)
        {
            return [(min + max) / 2.0];
        }

        var step = (max - min) / count;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            // Cell-centre sampling.
            values[i] = min + step * (i + 0.5);
        }

        return values;
    }

    // RasterInfo.Extent is populated in the raster's storage SRID, not CRS84 (#4149).
    private async Task<(double MinLon, double MinLat, double MaxLon, double MaxLat)?> ResolveCrs84ExtentAsync(
        RasterInfo raster,
        CancellationToken cancellationToken)
    {
        if (raster.Extent is not { } e)
        {
            return null;
        }

        return await OgcExtentTransformer
            .TryTransformExtentToCrs84Async(
                e.XMin,
                e.YMin,
                e.XMax,
                e.YMax,
                ResolveStorageSrid(raster),
                _coordinateTransformService,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static int ResolveStorageSrid(RasterInfo raster)
        => raster.Extent?.Srid ?? raster.Srid ?? 4326;

    private static bool WithinExtent(
        (double MinLon, double MinLat, double MaxLon, double MaxLat)? extent,
        double lon,
        double lat)
    {
        if (extent is not { } e)
        {
            return true; // No declared or transformable extent: accept and let the store decide.
        }

        return lon >= e.MinLon && lon <= e.MaxLon && lat >= e.MinLat && lat <= e.MaxLat;
    }

    private static int BandIndex(string parameterName)
    {
        var underscore = parameterName.LastIndexOf('_');
        return underscore >= 0
            && int.TryParse(parameterName[(underscore + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var band)
            && band > 0
                ? band
                : 1;
    }

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        decimal m => (double)m,
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => null
    };

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool IsProtocolEnabled(MetadataV2Service? service, string protocol) =>
        service?.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase)) == true;

    // CoverageJSON puts the t-axis values (ISO strings) on the domain alongside numeric
    // x/y axes; the source-gen NdArray axis dictionary is numeric-only, so the time axis is
    // merged into the serialized document via a post-step that adds the "t" string axis.
    private static System.Text.Json.Nodes.JsonNode ToCoverageNode(CoverageJson coverage, string instant)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(coverage, EdrJsonContext.Default.CoverageJson)!;
        var axes = node["domain"]!["axes"]!.AsObject();
        var timeAxis = System.Text.Json.JsonSerializer.SerializeToNode(
            new CoverageJsonTimeAxis { Values = ImmutableArray.Create(instant) },
            EdrJsonContext.Default.CoverageJsonTimeAxis)!;
        axes["t"] = timeAxis;
        return node;
    }

    private static IResult CoverageJsonResult(System.Text.Json.Nodes.JsonNode document)
        => Results.Text(document.ToJsonString(), CoverageJsonMediaType);
}
