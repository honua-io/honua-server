// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
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

    // Bound the cube sampling grid so an unbounded bbox cannot fan out into a huge
    // number of point-sample reads; EDR cube here is a sampled subset, not a full export.
    private const int MaxCubeAxisSamples = 50;
    private const int DefaultCubeAxisSamples = 8;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;

    public EdrHandler(EdrDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _graphProvider = dependencies.GraphProvider;
        _rasterStore = dependencies.RasterStore;
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
            if (resource is null || !AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                continue;
            }

            if (!byResource.TryGetValue(resource.Metadata.Id, out var existing) ||
                (publication.IsPrimary && !existing.Publication.IsPrimary))
            {
                byResource[resource.Metadata.Id] = (publication, service, resource);
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

            collections.Add(BuildCollection(entry.Resource, storageLayerId.Value, raster.Value, baseUrl));
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
        var collection = BuildCollection(resolution.Resource!, resolution.StorageLayerId, resolution.Raster, baseUrl);
        return Results.Json(collection, EdrJsonContext.Default.EdrCollection);
    }

    public async Task<IResult> GetPositionAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        var coords = context.Request.Query["coords"].ToString();
        if (!TryParsePoint(coords, out var lon, out var lat))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Query parameter 'coords' must be a WKT POINT, e.g. coords=POINT(-122.4 37.8).");
        }

        var raster = resolution.Raster;
        if (!WithinExtent(raster.Extent, lon, lat))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Requested position is outside the collection spatial extent.");
        }

        var instant = ResolveInstant(context.Request.Query["datetime"].ToString(), raster);
        var requestedParameters = ParseParameterNames(context.Request.Query["parameter-name"].ToString());

        var pixel = await _rasterStore
            .IdentifyAsync(resolution.StorageLayerId, raster.Id, lon, lat, srid: 4326, rendering: null, cancellationToken)
            .ConfigureAwait(false);

        var parameters = BuildParameters(raster, requestedParameters);
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

        var coverage = new CoverageJson
        {
            Domain = domain,
            Parameters = parameters,
            Ranges = ranges.ToImmutable()
        };

        // The single time instant is carried on the domain via a t-axis serialized as a
        // CoverageJSON time axis (ISO-8601 string values) alongside the numeric x/y axes.
        return JsonWithTimeAxis(coverage, instant);
    }

    public async Task<IResult> GetCubeAsync(HttpContext context, string collectionId, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryParseBbox(context.Request.Query["bbox"].ToString(), out var minX, out var minY, out var maxX, out var maxY))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Query parameter 'bbox' must be 'minLon,minLat,maxLon,maxLat'.");
        }

        var resolutionCount = ResolveCubeSampleCount(context.Request.Query["resolution-x"].ToString());
        var raster = resolution.Raster;
        var instant = ResolveInstant(context.Request.Query["datetime"].ToString(), raster);
        var requestedParameters = ParseParameterNames(context.Request.Query["parameter-name"].ToString());
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

        return JsonWithTimeAxis(coverage, instant);
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

    private static EdrCollection BuildCollection(MetadataV2Resource resource, int storageLayerId, RasterInfo raster, string baseUrl)
    {
        var collectionId = storageLayerId.ToString(CultureInfo.InvariantCulture);
        var basePath = $"{baseUrl}/edr/collections/{Uri.EscapeDataString(collectionId)}";
        var outputFormats = ImmutableArray.Create("CoverageJSON");

        Extent? extent = null;
        if (raster.Extent is { } e)
        {
            extent = new Extent
            {
                Spatial = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(
                        ImmutableArray.Create(e.XMin, e.YMin, e.XMax, e.YMax)),
                    Crs = Crs84
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
            Crs = ImmutableArray.Create("CRS84"),
            OutputFormats = outputFormats,
            ParameterNames = BuildParameters(raster, requested: null)
        };
    }

    private static ImmutableDictionary<string, EdrParameter> BuildParameters(RasterInfo raster, IReadOnlyCollection<string>? requested)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, EdrParameter>();
        var bandCount = Math.Max(1, raster.BandCount);
        for (var band = 1; band <= bandCount; band++)
        {
            var name = $"band_{band.ToString(CultureInfo.InvariantCulture)}";
            if (requested is { Count: > 0 } && !requested.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            builder[name] = new EdrParameter
            {
                Description = $"Coverage band {band.ToString(CultureInfo.InvariantCulture)}",
                ObservedProperty = new EdrObservedProperty { Label = name }
            };
        }

        if (builder.Count == 0)
        {
            builder["band_1"] = new EdrParameter
            {
                Description = "Coverage band 1",
                ObservedProperty = new EdrObservedProperty { Label = "band_1" }
            };
        }

        return builder.ToImmutable();
    }

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

    private static bool WithinExtent(RasterExtent? extent, double lon, double lat)
    {
        if (extent is not { } e)
        {
            return true; // No declared extent: accept and let the store decide.
        }

        return lon >= e.XMin && lon <= e.XMax && lat >= e.YMin && lat <= e.YMax;
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
    private static IResult JsonWithTimeAxis(CoverageJson coverage, string instant)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(coverage, EdrJsonContext.Default.CoverageJson)!;
        var axes = node["domain"]!["axes"]!.AsObject();
        var timeAxis = System.Text.Json.JsonSerializer.SerializeToNode(
            new CoverageJsonTimeAxis { Values = ImmutableArray.Create(instant) },
            EdrJsonContext.Default.CoverageJsonTimeAxis)!;
        axes["t"] = timeAxis;
        return Results.Text(node.ToJsonString(), CoverageJsonMediaType);
    }
}
