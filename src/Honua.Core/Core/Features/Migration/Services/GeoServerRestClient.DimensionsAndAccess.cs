// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

internal sealed partial class GeoServerRestClient
{
    private enum DimensionKind
    {
        Time,
        Elevation
    }

    private sealed record LayerDimensionRequest
    {
        public required string BaseUrl { get; init; }
        public required string WorkspaceName { get; init; }
        public required JsonElement LayerElement { get; init; }
    }

    private sealed record LayerResourceReference
    {
        public required string BaseUrl { get; init; }
        public required string WorkspaceName { get; init; }
        public required string ResourceName { get; init; }
        public string? DataStoreName { get; init; }
        public string? CoverageStoreName { get; init; }
    }

    private readonly record struct LayerDimensions(
        GeoServerDimensionInfo? Time,
        GeoServerDimensionInfo? Elevation);

    private async Task<LayerDimensions> GetLayerDimensionsAsync(
        LayerDimensionRequest request,
        CancellationToken cancellationToken)
    {
        var fromLayer = ExtractDimensionsFromMetadata(request.LayerElement);
        if (fromLayer.Time != null || fromLayer.Elevation != null)
        {
            return fromLayer;
        }

        var (dataStoreName, coverageStoreName) = ExtractStoreNames(request.LayerElement);
        var resolvedLayerName = request.LayerElement.GetProperty("name").GetString() ?? string.Empty;
        var reference = new LayerResourceReference
        {
            BaseUrl = request.BaseUrl,
            WorkspaceName = request.WorkspaceName,
            ResourceName = resolvedLayerName,
            DataStoreName = dataStoreName,
            CoverageStoreName = coverageStoreName
        };

        var resourceUrl = BuildLayerResourceMetadataUrl(reference);
        if (resourceUrl == null)
        {
            return default;
        }

        return await TryFetchResourceDimensionsAsync(resourceUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LayerDimensions> TryFetchResourceDimensionsAsync(
        string resourceUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetRequiredJsonDocumentAsync(resourceUrl, cancellationToken).ConfigureAwait(false);
            if (GetObjectPropertyOrNull(json.RootElement, "featureType") is { } featureType)
            {
                return ExtractDimensionsFromMetadata(featureType);
            }

            if (GetObjectPropertyOrNull(json.RootElement, "coverage") is { } coverage)
            {
                return ExtractDimensionsFromMetadata(coverage);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort enrichment: endpoints without featuretype/coverage detail still allow discovery.
            _ = ex;
        }

        return default;
    }

    private static string? BuildLayerResourceMetadataUrl(LayerResourceReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.WorkspaceName) || string.IsNullOrWhiteSpace(reference.ResourceName))
        {
            return null;
        }

        var workspace = EscapePathSegment(reference.WorkspaceName);
        var resource = EscapePathSegment(reference.ResourceName);

        if (!string.IsNullOrWhiteSpace(reference.DataStoreName))
        {
            var store = EscapePathSegment(reference.DataStoreName);
            return BuildJsonUrl(reference.BaseUrl, $"workspaces/{workspace}/datastores/{store}/featuretypes/{resource}");
        }

        if (!string.IsNullOrWhiteSpace(reference.CoverageStoreName))
        {
            var store = EscapePathSegment(reference.CoverageStoreName);
            return BuildJsonUrl(reference.BaseUrl, $"workspaces/{workspace}/coveragestores/{store}/coverages/{resource}");
        }

        return null;
    }

    private static LayerDimensions ExtractDimensionsFromMetadata(JsonElement containerElement)
    {
        if (GetObjectPropertyOrNull(containerElement, "metadata") is not { } metadataElement)
        {
            return default;
        }

        var (timeDimension, elevationDimension) = ReadDimensionsFromEntries(containerElement);
        timeDimension ??= TryReadDirectDimension(metadataElement, "time", DimensionKind.Time);
        elevationDimension ??= TryReadDirectDimension(metadataElement, "elevation", DimensionKind.Elevation);
        return new LayerDimensions(timeDimension, elevationDimension);
    }

    private static (GeoServerDimensionInfo? Time, GeoServerDimensionInfo? Elevation) ReadDimensionsFromEntries(
        JsonElement containerElement)
    {
        GeoServerDimensionInfo? timeDimension = null;
        GeoServerDimensionInfo? elevationDimension = null;

        foreach (var entry in EnumerateCollectionItems(containerElement, "metadata", "entry"))
        {
            var key = GetOptionalStringProperty(entry, "@key");
            if (string.IsNullOrWhiteSpace(key) || GetObjectPropertyOrNull(entry, "dimensionInfo") is not { } dimensionElement)
            {
                continue;
            }

            if (string.Equals(key, "time", StringComparison.OrdinalIgnoreCase))
            {
                timeDimension = ParseDimensionInfo(dimensionElement, DimensionKind.Time);
            }
            else if (string.Equals(key, "elevation", StringComparison.OrdinalIgnoreCase))
            {
                elevationDimension = ParseDimensionInfo(dimensionElement, DimensionKind.Elevation);
            }
        }

        return (timeDimension, elevationDimension);
    }

    private static GeoServerDimensionInfo? TryReadDirectDimension(
        JsonElement metadataElement,
        string propertyName,
        DimensionKind kind)
        => GetObjectPropertyOrNull(metadataElement, propertyName) is { } directElement
            ? ParseDimensionInfo(directElement, kind)
            : null;

    private static GeoServerDimensionInfo ParseDimensionInfo(JsonElement element, DimensionKind kind)
    {
        var startValue = ReadOptionalScalar(element, "startValue");
        var endValue = ReadOptionalScalar(element, "endValue");
        var isTime = kind == DimensionKind.Time;
        var (defaultStrategy, defaultReference) = ReadDefaultValue(element);

        return new GeoServerDimensionInfo
        {
            Enabled = GetOptionalBoolProperty(element, "enabled") ?? false,
            Attribute = GetOptionalStringProperty(element, "attribute"),
            EndAttribute = GetOptionalStringProperty(element, "endAttribute"),
            Presentation = GetOptionalStringProperty(element, "presentation"),
            Resolution = ReadOptionalLong(element, "resolution"),
            Units = GetOptionalStringProperty(element, "units"),
            UnitSymbol = GetOptionalStringProperty(element, "unitSymbol"),
            DefaultValueStrategy = defaultStrategy,
            DefaultReferenceValue = defaultReference,
            NearestMatchEnabled = GetOptionalBoolProperty(element, "nearestMatchEnabled") ?? false,
            StartTime = isTime ? TryParseIsoTimestamp(startValue) : null,
            EndTime = isTime ? TryParseIsoTimestamp(endValue) : null,
            StartValue = startValue,
            EndValue = endValue,
            Values = ReadOptionalStringList(element, "values")
        };
    }

    private static (string? Strategy, string? ReferenceValue) ReadDefaultValue(JsonElement element)
    {
        if (GetObjectPropertyOrNull(element, "defaultValue") is not { } defaultValueElement)
        {
            return (null, null);
        }

        return (
            GetOptionalStringProperty(defaultValueElement, "strategy"),
            ReadOptionalScalar(defaultValueElement, "referenceValue"));
    }

    private async Task<GeoServerLayerAccessRule[]> GetLayerAccessRulesAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, "security/acl/layers"),
                cancellationToken).ConfigureAwait(false);

            return json.RootElement.ValueKind == JsonValueKind.Object
                ? ParseLayerAccessRules(json.RootElement)
                : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort enrichment: GeoServer instances without admin ACL access may omit this endpoint.
            _ = ex;
            return [];
        }
    }

    internal static GeoServerLayerAccessRule[] ParseLayerAccessRules(JsonElement root)
    {
        var rules = new List<GeoServerLayerAccessRule>();
        foreach (var property in root.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            var rawValue = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();

            rules.Add(ParseSingleAccessRule(property.Name, rawValue));
        }

        return rules.ToArray();
    }

    private static GeoServerLayerAccessRule ParseSingleAccessRule(string ruleKey, string rawValue)
    {
        var roles = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var segments = ruleKey.Split('.');
        if (segments.Length != 3)
        {
            return new GeoServerLayerAccessRule
            {
                RuleKey = ruleKey,
                RawValue = rawValue,
                Roles = roles
            };
        }

        return new GeoServerLayerAccessRule
        {
            RuleKey = ruleKey,
            RawValue = rawValue,
            WorkspaceName = segments[0],
            LayerName = segments[1],
            Permission = segments[2].ToLowerInvariant(),
            Roles = roles
        };
    }

    private static JsonElement? GetObjectPropertyOrNull(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? ReadOptionalScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private static long? ReadOptionalLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryParseIsoTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string[] ReadOptionalStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text.Trim());
            }
        }

        return values.ToArray();
    }
}
