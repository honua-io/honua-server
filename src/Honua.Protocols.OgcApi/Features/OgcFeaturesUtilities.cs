// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Api.Features;

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
    /// Builds supported CRS definitions for a Metadata V2 collection.
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
    /// Returns supported CRS URIs for a Metadata V2 collection.
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
            // BH7-008: Use TryGetValue to guard against a degraded CRS registry where
            // CRS84 was not successfully resolved at startup (PROJ misconfiguration,
            // transient registry error). The previous indexer access threw
            // KeyNotFoundException which propagated as a misleading HTTP 400.
            if (!supportedCrs.TryGetValue(Crs84Uri, out definition))
            {
                error = "Default CRS (CRS84) is not available for this collection.";
                return false;
            }

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
    /// Determines whether a Metadata V2 field is queryable for simple parameter filtering.
    /// </summary>
    public static bool IsSimpleQueryableField(MetadataV2Field field)
        => OgcQueryablesUtilities.IsSimpleQueryableField(field);

    /// <summary>
    /// Validates items query parameters against the Metadata V2 queryable field set.
    /// </summary>
    public static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resource);

        var allowed = new HashSet<string>(AllowedQueryParameters.Items, StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(resource.SchemaFields.Where(IsSimpleQueryableField).Select(field => field.Name));

        var validator = request.HttpContext.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var error = QueryParameterValidationHelpers.GetValidationError(
            validator,
            request.Query.Keys.ToArray(),
            allowed);
        return error == null ? null : TypedResults.BadRequest(error);
    }

    /// <summary>
    /// Builds a temporal extent from the Metadata V2 temporal declaration and feature store values.
    /// </summary>
    /// <param name="resource">V2 resource carrying the temporal field declaration.</param>
    /// <param name="layerIndex">Service-local integer layer id used by the feature reader.</param>
    /// <param name="featureReader">Feature reader providing the extent probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<TemporalExtent?> BuildTemporalExtentAsync(
        MetadataV2Resource resource,
        int layerIndex,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
        => OgcQueryablesUtilities.BuildTemporalExtentAsync(resource, layerIndex, featureReader, cancellationToken);

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
        out TemporalPropertyType startPropertyType,
        out string? endFieldName,
        out TemporalPropertyType endPropertyType)
        => OgcQueryablesUtilities.TryResolveTemporalFieldsV2(
            resource, out startFieldName, out startPropertyType, out endFieldName, out endPropertyType);

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
