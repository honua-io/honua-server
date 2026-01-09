// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.OgcFeatures;

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
    }

    /// <summary>
    /// CRS definition with URI, SRID, and axis order
    /// </summary>
    public readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder, bool IsGeographic);

    /// <summary>
    /// Axis order enumeration
    /// </summary>
    public enum AxisOrder
    {
        EastNorth,
        NorthEast
    }

    /// <summary>
    /// Supported feature formats
    /// </summary>
    public static readonly ImmutableArray<FormatOption> FeatureFormats = ImmutableArray.Create(
        new FormatOption("geojson", MediaTypes.GeoJson, "GeoJSON"),
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("gml", MediaTypes.Gml, "GML"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    // CRS constants
    public const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    public const string Epsg4326Uri = "http://www.opengis.net/def/crs/EPSG/0/4326";
    public const string WfsNamespace = "http://www.opengis.net/wfs/2.0";
    public const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// Returns the supported CRS URIs for a collection.
    /// </summary>
    public static ImmutableArray<string> GetSupportedCrsUris(LayerDefinition layer)
    {
        var supported = new List<string>
        {
            Crs84Uri,
            Epsg4326Uri
        };

        var storageCrs = layer.SpatialReference.ToOgcCrs();
        if (!supported.Contains(storageCrs, StringComparer.OrdinalIgnoreCase))
        {
            supported.Add(storageCrs);
        }

        return supported.ToImmutableArray();
    }

    /// <summary>
    /// Builds CRS definitions for a collection.
    /// </summary>
    public static IReadOnlyDictionary<string, CrsDefinition> GetSupportedCrsDefinitions(LayerDefinition layer)
    {
        var definitions = new Dictionary<string, CrsDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var crsUri in GetSupportedCrsUris(layer))
        {
            var definition = CreateCrsDefinition(crsUri);
            definitions[definition.Uri] = definition;
        }

        return definitions;
    }

    /// <summary>
    /// Normalizes CRS inputs to canonical URI forms (e.g. EPSG:4326 -> http://www.opengis.net/def/crs/EPSG/0/4326).
    /// </summary>
    public static string NormalizeCrsUri(string crs)
    {
        var trimmed = crs.Trim();

        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed[5..];
            if (int.TryParse(code, out var srid))
            {
                return $"http://www.opengis.net/def/crs/EPSG/0/{srid}";
            }
        }

        const string urnPrefix = "urn:ogc:def:crs:EPSG::";
        if (trimmed.StartsWith(urnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed[urnPrefix.Length..];
            if (int.TryParse(code, out var srid))
            {
                return $"http://www.opengis.net/def/crs/EPSG/0/{srid}";
            }
        }

        return trimmed;
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

    private static CrsDefinition CreateCrsDefinition(string crsUri)
    {
        var normalized = NormalizeCrsUri(crsUri);
        var srid = ExtentExtensions.ExtractSridFromCrs(normalized);
        var axisOrder = string.Equals(normalized, Epsg4326Uri, StringComparison.OrdinalIgnoreCase)
            ? AxisOrder.NorthEast
            : AxisOrder.EastNorth;
        var isGeographic = string.Equals(normalized, Crs84Uri, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(normalized, Epsg4326Uri, StringComparison.OrdinalIgnoreCase);

        return new CrsDefinition(normalized, srid, axisOrder, isGeographic);
    }

    /// <summary>
    /// Validates query parameters for items requests including queryable fields
    /// </summary>
    public static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        LayerDefinition layer)
    {
        var allowed = new HashSet<string>(AllowedQueryParameters.Items, StringComparer.OrdinalIgnoreCase);

        foreach (var field in layer.AttributeFields)
        {
            if (IsSimpleQueryableField(field))
            {
                allowed.Add(field.Name);
            }
        }

        var validator = request.HttpContext.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var validationResult = validator.ValidateAllowedParameters(request.Query.Keys.ToArray(), allowed);
        return validationResult.IsValid
            ? null
            : TypedResults.BadRequest(validationResult.ErrorMessage ?? "Invalid query parameter.");
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
}
