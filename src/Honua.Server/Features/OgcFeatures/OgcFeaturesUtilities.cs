// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
    public const string Epsg3857Uri = "http://www.opengis.net/def/crs/EPSG/0/3857";
    public const string WfsNamespace = "http://www.opengis.net/wfs/2.0";
    public const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";

    private static readonly ImmutableArray<string> _defaultCrsIdentifiers =
        ImmutableArray.Create(Crs84Uri, Epsg4326Uri, Epsg3857Uri);

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

    public static async Task<TemporalExtent?> BuildTemporalExtentAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        if (!TryResolveTemporalFields(layer, out var startField, out var endField))
        {
            return null;
        }

        TemporalExtentResult? startExtent = await featureReader.GetTemporalExtentAsync(
            layer.Id,
            startField!.Name,
            startField.Type,
            cancellationToken).ConfigureAwait(false);

        TemporalExtentResult? endExtent = null;
        if (endField != null && !endField.Name.Equals(startField.Name, StringComparison.OrdinalIgnoreCase))
        {
            endExtent = await featureReader.GetTemporalExtentAsync(
                layer.Id,
                endField.Name,
                endField.Type,
                cancellationToken).ConfigureAwait(false);
        }

        if (startExtent == null)
        {
            return null;
        }

        var min = startExtent?.Start;
        var max = endField == null
            ? startExtent?.End
            : endExtent?.End ?? endExtent?.Start;

        return new TemporalExtent
        {
            Interval = ImmutableArray.Create(ImmutableArray.Create(
                FormatTemporalValue(min),
                FormatTemporalValue(max)))
        };
    }

    private static bool TryResolveTemporalFields(
        LayerDefinition layer,
        out FieldDefinition? startField,
        out FieldDefinition? endField)
    {
        startField = null;
        endField = null;

        var timeInfo = layer.Metadata?.TimeInfo;
        if (timeInfo != null)
        {
            if (string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
            {
                return false;
            }

            startField = FindTemporalField(layer, timeInfo.StartTimeField);
            if (startField == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(timeInfo.EndTimeField))
            {
                endField = FindTemporalField(layer, timeInfo.EndTimeField);
                if (endField == null)
                {
                    return false;
                }
            }
        }
        else
        {
            startField = layer.AttributeFields.FirstOrDefault(field => field.Type is FieldType.DateTime or FieldType.Date);
        }

        if (startField == null)
        {
            return false;
        }

        if (endField != null && endField.Type != startField.Type)
        {
            return false;
        }

        return true;
    }

    private static FieldDefinition? FindTemporalField(LayerDefinition layer, string fieldName)
    {
        return layer.AttributeFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is FieldType.DateTime or FieldType.Date);
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
}
