// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Shared utilities and constants for OGC API Features endpoints
/// </summary>
internal static class OgcFeaturesUtilities
{
    /// <summary>
    /// Allowed query parameters by operation type
    /// </summary>
    public static class AllowedQueryParameters
    {
        public static readonly FrozenSet<string> Metadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Items = new[]
            {
                "f",
                "bbox",
                "bbox-crs",
                "crs",
                "datetime",
                "limit",
                "offset",
                "ids",
                "properties",
                "sortby",
                "filter",
                "filter-lang",
                "filter-crs"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Item = new[]
            {
                "f",
                "crs"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> OpenApi =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> Transactions =
            Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static readonly FrozenSet<string> H3 = new[]
            {
                "resolution"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Supported feature formats
    /// </summary>
    public static readonly ImmutableArray<FormatOption> FeatureFormats = ImmutableArray.Create(
        new FormatOption("geojson", MediaTypes.GeoJson, "GeoJSON"),
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("gml", MediaTypes.Gml, "GML"),
        new FormatOption("csv", MediaTypes.Csv, "CSV"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    // CRS constants
    public const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    public const string Epsg4326Uri = "http://www.opengis.net/def/crs/EPSG/0/4326";
    public const string Epsg3857Uri = "http://www.opengis.net/def/crs/EPSG/0/3857";
    public const string WfsNamespace = "http://www.opengis.net/wfs/2.0";
    public const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    public const string AppNamespace = "https://honua.io/gml/ogcapi-features/1.0";
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";
    public const string GmlApplicationSchemaPath = "/ogc/features/schemas/honua-ogcapi-features.xsd";

    private static readonly ImmutableArray<string> _defaultCrsIdentifiers =
        ImmutableArray.Create(Crs84Uri, Epsg4326Uri, Epsg3857Uri);

    public static string BuildGmlApplicationSchemaUrl(string baseUrl)
        => string.Concat(baseUrl, GmlApplicationSchemaPath);

    /// <summary>
    /// Returns the supported CRS URIs for a collection.
    /// </summary>
    public static async Task<ImmutableArray<string>> GetSupportedCrsUrisAsync(
        LayerDefinition layer,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        var definitions = await GetSupportedCrsDefinitionsAsync(layer, crsRegistry, cancellationToken).ConfigureAwait(false);
        return definitions.Keys
            .OrderBy(static uri => uri, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    /// <summary>
    /// Builds CRS definitions for a collection.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CrsDefinition>> GetSupportedCrsDefinitionsAsync(
        LayerDefinition layer,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var crsIdentifier in _defaultCrsIdentifiers)
        {
            var definition = await crsRegistry.ResolveAsync(crsIdentifier, cancellationToken).ConfigureAwait(false);
            if (definition.HasValue)
            {
                definitions[definition.Value.Uri] = definition.Value;
            }
        }

        var storageCrs = layer.SpatialReference.ToOgcCrs();
        var storageDefinition = await crsRegistry.ResolveAsync(storageCrs, cancellationToken).ConfigureAwait(false);
        if (storageDefinition.HasValue)
        {
            definitions[storageDefinition.Value.Uri] = storageDefinition.Value;
        }

        return definitions;
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="GetSupportedCrsDefinitionsAsync(LayerDefinition, ICrsRegistry, CancellationToken)"/>.
    /// Resolves the storage CRS from <see cref="MetadataV2SpatialExtensions.ReadSrid"/> on the
    /// V2 resource and unions it with the default CRS list.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CrsDefinition>> GetSupportedCrsDefinitionsAsync(
        MetadataV2Resource resource,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(crsRegistry);

        var definitions = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var crsIdentifier in _defaultCrsIdentifiers)
        {
            var definition = await crsRegistry.ResolveAsync(crsIdentifier, cancellationToken).ConfigureAwait(false);
            if (definition.HasValue)
            {
                definitions[definition.Value.Uri] = definition.Value;
            }
        }

        var srid = resource.ReadSrid();
        if (srid.HasValue)
        {
            var storageCrs = srid.Value.ToOgcCrs();
            var storageDefinition = await crsRegistry.ResolveAsync(storageCrs, cancellationToken).ConfigureAwait(false);
            if (storageDefinition.HasValue)
            {
                definitions[storageDefinition.Value.Uri] = storageDefinition.Value;
            }
        }

        return definitions;
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="GetSupportedCrsUrisAsync(LayerDefinition, ICrsRegistry, CancellationToken)"/>.
    /// </summary>
    public static async Task<ImmutableArray<string>> GetSupportedCrsUrisAsync(
        MetadataV2Resource resource,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        var definitions = await GetSupportedCrsDefinitionsAsync(resource, crsRegistry, cancellationToken).ConfigureAwait(false);
        return definitions.Keys
            .OrderBy(static uri => uri, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    /// <summary>
    /// Normalizes CRS inputs to canonical URI forms (e.g. EPSG:4326 -> http://www.opengis.net/def/crs/EPSG/0/4326).
    /// </summary>
    public static string NormalizeCrsUri(string crs)
    {
        var trimmed = crs.Trim();
        return SpatialReferenceHelpers.TryParseCrsDefinition(trimmed, out var definition)
            ? definition.Uri
            : trimmed;
    }

    /// <summary>
    /// Resolves CRS parameters against supported CRS definitions.
    /// </summary>
    public static bool TryResolveCrs(
        string? crs,
        IReadOnlyDictionary<string, CrsDefinition> supportedCrs,
        out CrsDefinition definition,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            definition = supportedCrs[Crs84Uri];
            error = null;
            return true;
        }

        var normalized = NormalizeCrsUri(crs);
        if (supportedCrs.TryGetValue(normalized, out definition))
        {
            error = null;
            return true;
        }

        definition = default;
        error = $"Unsupported CRS '{crs}'.";
        return false;
    }

    /// <summary>
    /// Resolves request CRS parameters against the CRS contract advertised by the collection metadata.
    /// </summary>
    public static async ValueTask<(bool IsSuccess, CrsDefinition Definition, string? Error)> TryResolveCrsAsync(
        string? crs,
        IReadOnlyDictionary<string, CrsDefinition> supportedCrs,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        _ = crsRegistry;
        _ = cancellationToken;

        if (TryResolveCrs(crs, supportedCrs, out var definition, out var error))
        {
            return (true, definition, null);
        }

        return (false, default, error ?? $"Unsupported CRS '{crs}'.");
    }

    /// <summary>
    /// Validates query parameters for items requests including queryable fields
    /// </summary>
    public static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        LayerDefinition layer)
    {
        var allowed = new HashSet<string>(AllowedQueryParameters.Items, StringComparer.OrdinalIgnoreCase);

        foreach (var field in layer.VisibleAttributeFields)
        {
            if (IsSimpleQueryableField(field))
            {
                allowed.Add(field.Name);
            }
        }

        var validator = request.HttpContext.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var error = QueryParameterValidationHelpers.GetValidationError(
            validator,
            request.Query.Keys.ToArray(),
            allowed);
        return error == null ? null : TypedResults.BadRequest(error);
    }

    /// <summary>
    /// Determines if a field is queryable for simple parameter filtering
    /// </summary>
    public static bool IsSimpleQueryableField(FieldDefinition field)
        => field.Type is FieldType.String
            or FieldType.Integer
            or FieldType.BigInteger
            or FieldType.Double
            or FieldType.Float
            or FieldType.Boolean
            or FieldType.DateTime
            or FieldType.Date
            or FieldType.Time
            or FieldType.Uuid;

    /// <summary>
    /// Metadata v2 overload of <see cref="IsSimpleQueryableField(FieldDefinition)"/>.
    /// </summary>
    public static bool IsSimpleQueryableField(MetadataV2Field field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Type is MetadataV2FieldType.String
            or MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float
            or MetadataV2FieldType.Boolean
            or MetadataV2FieldType.DateTime
            or MetadataV2FieldType.Date
            or MetadataV2FieldType.Time
            or MetadataV2FieldType.Uuid;
    }

    /// <summary>
    /// Metadata v2 overload of <see cref="ValidateItemsQueryParameters(HttpRequest, LayerDefinition)"/>.
    /// Builds the allowed query-parameter set from <see cref="MetadataV2Resource.SchemaFields"/>
    /// instead of the v1 <c>VisibleAttributeFields</c>.
    /// </summary>
    public static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resource);

        var allowed = new HashSet<string>(AllowedQueryParameters.Items, StringComparer.OrdinalIgnoreCase);

        foreach (var field in resource.SchemaFields)
        {
            if (IsSimpleQueryableField(field))
            {
                allowed.Add(field.Name);
            }
        }

        var validator = request.HttpContext.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var error = QueryParameterValidationHelpers.GetValidationError(
            validator,
            request.Query.Keys.ToArray(),
            allowed);
        return error == null ? null : TypedResults.BadRequest(error);
    }

    public static async Task<TemporalExtent?> BuildTemporalExtentAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        var temporalRange = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (temporalRange == null || !temporalRange.Value.HasExtent)
        {
            return null;
        }

        var range = temporalRange.Value;
        return new TemporalExtent
        {
            Interval = ImmutableArray.Create(ImmutableArray.Create(
                FormatTemporalValue(range.Min),
                FormatTemporalValue(range.Max)))
        };
    }

    /// <summary>
    /// Metadata v2 overload of <see cref="BuildTemporalExtentAsync(LayerDefinition, IFeatureReader, CancellationToken)"/>.
    /// Resolves temporal fields from the V2 <see cref="MetadataV2Resource.Temporal"/> extension
    /// and probes the underlying feature store keyed on <paramref name="layerIndex"/>. Returns
    /// <c>null</c> when the resource declares no usable temporal fields, or when the store
    /// reports no extent.
    /// </summary>
    /// <param name="resource">V2 resource carrying the temporal field declaration.</param>
    /// <param name="layerIndex">Service-local integer layer id used by the feature reader.</param>
    /// <param name="featureReader">Feature reader providing the extent probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<TemporalExtent?> BuildTemporalExtentAsync(
        MetadataV2Resource resource,
        int layerIndex,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(featureReader);

        if (!TryResolveTemporalFieldsV2(resource, out var startName, out var startType, out var endName, out var endType))
        {
            return null;
        }

        TemporalExtentResult? startExtent;
        try
        {
            startExtent = await featureReader.GetTemporalExtentAsync(
                layerIndex, startName!, startType, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        TemporalExtentResult? endExtent = null;
        if (!string.IsNullOrWhiteSpace(endName) &&
            !string.Equals(endName, startName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                endExtent = await featureReader.GetTemporalExtentAsync(
                    layerIndex, endName!, endType, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        var hasExtent = startExtent != null || endExtent != null;
        if (!hasExtent)
        {
            return null;
        }

        var min = startExtent?.Start;
        DateTimeOffset? max;
        if (string.IsNullOrWhiteSpace(endName))
        {
            max = startExtent?.End;
        }
        else
        {
            max = endExtent?.End ?? endExtent?.Start ?? startExtent?.End;
        }

        return new TemporalExtent
        {
            Interval = ImmutableArray.Create(ImmutableArray.Create(
                FormatTemporalValue(min),
                FormatTemporalValue(max)))
        };
    }

    /// <summary>
    /// Resolves the start/end temporal field names + canonical types from a V2 resource.
    /// Reads <see cref="MetadataV2Resource.Temporal"/> for the configured field names and
    /// looks up the field type in <see cref="MetadataV2Resource.SchemaFields"/>. Returns
    /// false when no start field is declared, the start field is not present in the schema,
    /// or the schema field type is not a recognized temporal type.
    /// </summary>
    public static bool TryResolveTemporalFieldsV2(
        MetadataV2Resource resource,
        out string? startFieldName,
        out FieldType startFieldType,
        out string? endFieldName,
        out FieldType endFieldType)
    {
        ArgumentNullException.ThrowIfNull(resource);
        startFieldName = null;
        endFieldName = null;
        startFieldType = FieldType.DateTime;
        endFieldType = FieldType.DateTime;

        var fields = resource.ReadTemporalFields();
        if (string.IsNullOrWhiteSpace(fields.StartTimeField))
        {
            return false;
        }

        if (!TryResolveSchemaTemporalType(resource, fields.StartTimeField, out var startType))
        {
            return false;
        }

        startFieldName = fields.StartTimeField;
        startFieldType = startType;

        if (string.IsNullOrWhiteSpace(fields.EndTimeField))
        {
            return true;
        }

        if (!TryResolveSchemaTemporalType(resource, fields.EndTimeField, out var endType))
        {
            // End field is configured but not in schema: behave like v1 — fail the whole resolution.
            startFieldName = null;
            startFieldType = FieldType.DateTime;
            return false;
        }

        endFieldName = fields.EndTimeField;
        endFieldType = endType;
        return true;
    }

    private static bool TryResolveSchemaTemporalType(
        MetadataV2Resource resource,
        string fieldName,
        out FieldType type)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (field.Type)
            {
                case MetadataV2FieldType.DateTime:
                    type = FieldType.DateTime;
                    return true;
                case MetadataV2FieldType.Date:
                    type = FieldType.Date;
                    return true;
            }
        }

        type = FieldType.DateTime;
        return false;
    }

    private static string? FormatTemporalValue(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var utc = value.Value.ToUniversalTime();
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-ddTHH:mm:ss'Z'"
            : "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Invalidates output cache for a layer after mutation operations.
    /// </summary>
    public static async Task InvalidateLayerCacheAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken);
        }
    }

    /// <summary>
    /// Builds standard OGC feature links (self, alternates, collection).
    /// </summary>
    public static ImmutableArray<Link> BuildFeatureLinks(
        HttpRequest request,
        string collectionId,
        string featureId,
        string outputFormat,
        string? responseCrsUri = null)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        var collectionSegment = Uri.EscapeDataString(collectionId);
        var featureSegment = Uri.EscapeDataString(featureId);
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionSegment}/items/{featureSegment}";
        var selfHref = BuildFeatureRepresentationUrl(request, basePath, outputFormat, responseCrsUri);

        var links = new List<Link>
        {
            Link.Create(
                href: selfHref,
                rel: RelationTypes.Self,
                type: outputFormat,
                title: "Feature")
        };

        foreach (var format in FeatureFormats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: BuildFeatureRepresentationUrl(request, basePath, format.MediaType, responseCrsUri),
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionSegment}",
            rel: RelationTypes.Collection,
            type: MediaTypes.Json,
            title: "Collection"));

        return links.ToImmutableArray();
    }

    /// <summary>
    /// Builds the self URL for a feature, used in Location headers for 201 Created responses.
    /// </summary>
    public static string BuildFeatureSelfUrl(
        HttpRequest request,
        string collectionId,
        string featureId)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(request);
        return $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(collectionId)}/items/{Uri.EscapeDataString(featureId)}";
    }

    private static string BuildFeatureRepresentationUrl(
        HttpRequest request,
        string basePath,
        string outputFormat,
        string? responseCrsUri)
    {
        var queryParameters = new List<string>();

        if (request.Query.TryGetValue("crs", out var crsValue) && !string.IsNullOrWhiteSpace(crsValue))
        {
            queryParameters.Add($"crs={Uri.EscapeDataString(crsValue.ToString())}");
        }
        else if (!string.IsNullOrWhiteSpace(responseCrsUri))
        {
            queryParameters.Add($"crs={Uri.EscapeDataString(responseCrsUri)}");
        }

        var formatQueryValue = FeatureFormats
            .FirstOrDefault(format => string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            .QueryValue;
        if (!string.IsNullOrWhiteSpace(formatQueryValue))
        {
            queryParameters.Add($"f={Uri.EscapeDataString(formatQueryValue)}");
        }

        return queryParameters.Count == 0
            ? basePath
            : $"{basePath}?{string.Join("&", queryParameters)}";
    }
}

/// <summary>
/// Transforms extent coordinates to CRS84 for OGC API spec compliance.
/// </summary>
internal static class OgcExtentTransformer
{
    private const double EarthRadius = SpatialConstants.EarthRadius;
    private const double MaxLatitude = SpatialConstants.WebMercatorMaxLatitude;

    /// <summary>
    /// Transforms a coordinate pair to CRS84 (lon/lat in degrees).
    /// Returns <c>false</c> when a reliable in-memory transform is not available.
    /// </summary>
    public static bool TryTransformToCrs84(double x, double y, int fromSrid, out (double Lon, double Lat) coordinate)
    {
        if (fromSrid == 4326)
        {
            coordinate = (x, y);
            return true;
        }

        if (IsWebMercatorSrid(fromSrid))
        {
            coordinate = WebMercatorToLonLat(x, y);
            return true;
        }

        coordinate = default;
        return false;
    }

    public static async Task<(double MinLon, double MinLat, double MaxLon, double MaxLat)?> TryTransformExtentToCrs84Async(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        ICoordinateTransformService? transformService,
        CancellationToken cancellationToken = default)
    {
        if (fromSrid == 4326)
        {
            return (minX, minY, maxX, maxY);
        }

        try
        {
            var transformed = CoordinateTransformer.TransformExtent(
                new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY),
                fromSrid,
                4326);
            return (transformed.MinX, transformed.MinY, transformed.MaxX, transformed.MaxY);
        }
        catch (NotSupportedException)
        {
            if (transformService == null)
            {
                return null;
            }

            var transformed = await transformService
                .TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, 4326, cancellationToken)
                .ConfigureAwait(false);
            return transformed.HasValue
                ? (transformed.Value.MinX, transformed.Value.MinY, transformed.Value.MaxX, transformed.Value.MaxY)
                : null;
        }
    }

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    private static (double Lon, double Lat) WebMercatorToLonLat(double x, double y)
    {
        y = Math.Clamp(y, -EarthRadius * Math.PI, EarthRadius * Math.PI);
        var lon = x / EarthRadius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        return (lon, lat);
    }
}
