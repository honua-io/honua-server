// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

internal sealed partial class GeoServerRestClient
{
    private sealed record LayerResourceDetails
    {
        public GeoServerDimensionInfo? TimeDimension { get; init; }
        public GeoServerDimensionInfo? ElevationDimension { get; init; }
        public string? NativeName { get; init; }
        public string? Srs { get; init; }
        public string? NativeCrs { get; init; }
        public GeoServerBoundingBox? LatLonBoundingBox { get; init; }
        public GeoServerBoundingBox? NativeBoundingBox { get; init; }
    }

    private async Task<GeoServerLayerInfo> EnrichLayerFromResourceAsync(
        string baseUrl,
        GeoServerLayerInfo layer,
        CancellationToken cancellationToken)
    {
        if (layer.TimeDimension != null || layer.ElevationDimension != null)
        {
            return layer;
        }

        if (string.IsNullOrWhiteSpace(layer.DataStoreName) && string.IsNullOrWhiteSpace(layer.CoverageStoreName))
        {
            return layer;
        }

        var details = await TryGetLayerResourceDetailsAsync(baseUrl, layer, cancellationToken).ConfigureAwait(false);
        if (details == null)
        {
            return layer;
        }

        return layer with
        {
            TimeDimension = details.TimeDimension,
            ElevationDimension = details.ElevationDimension,
            NativeName = layer.NativeName ?? details.NativeName,
            SRS = layer.SRS ?? details.Srs,
            NativeCRS = layer.NativeCRS ?? details.NativeCrs,
            LatLonBoundingBox = layer.LatLonBoundingBox ?? details.LatLonBoundingBox,
            NativeBoundingBox = layer.NativeBoundingBox ?? details.NativeBoundingBox
        };
    }

    private async Task<LayerResourceDetails?> TryGetLayerResourceDetailsAsync(
        string baseUrl,
        GeoServerLayerInfo layer,
        CancellationToken cancellationToken)
    {
        var resourceName = layer.NativeName ?? layer.Name;
        var isFeatureType = !string.IsNullOrWhiteSpace(layer.DataStoreName);
        var relativePath = isFeatureType
            ? $"workspaces/{EscapePathSegment(layer.WorkspaceName)}/datastores/{EscapePathSegment(layer.DataStoreName!)}/featuretypes/{EscapePathSegment(resourceName)}"
            : $"workspaces/{EscapePathSegment(layer.WorkspaceName)}/coveragestores/{EscapePathSegment(layer.CoverageStoreName!)}/coverages/{EscapePathSegment(resourceName)}";
        var rootProperty = isFeatureType ? "featureType" : "coverage";

        try
        {
            using var json = await GetRequiredJsonDocumentAsync(BuildJsonUrl(baseUrl, relativePath), cancellationToken)
                .ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty(rootProperty, out var resourceElement) ||
                resourceElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var (timeDim, elevDim) = ExtractDimensionsFromMetadata(resourceElement);
            return new LayerResourceDetails
            {
                TimeDimension = timeDim,
                ElevationDimension = elevDim,
                NativeName = GetOptionalStringProperty(resourceElement, "nativeName"),
                Srs = GetOptionalStringProperty(resourceElement, "srs"),
                NativeCrs = GetOptionalStringProperty(resourceElement, "nativeCRS"),
                LatLonBoundingBox = GetOptionalBoundingBox(resourceElement, "latLonBoundingBox"),
                NativeBoundingBox = GetOptionalBoundingBox(resourceElement, "nativeBoundingBox")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            Log.FeatureTypeMetadataUnavailable(_logger, layer.WorkspaceName, resourceName, ex);
            return null;
        }
    }

    private static (GeoServerDimensionInfo? TimeDimension, GeoServerDimensionInfo? ElevationDimension) ExtractDimensionsFromMetadata(
        JsonElement parentElement)
    {
        GeoServerDimensionInfo? timeDimension = null;
        GeoServerDimensionInfo? elevationDimension = null;

        foreach (var entry in EnumerateCollectionItems(parentElement, "metadata", "entry"))
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = GetOptionalStringProperty(entry, "@key");
            if (string.IsNullOrWhiteSpace(key) ||
                !entry.TryGetProperty("dimensionInfo", out var dimensionElement) ||
                dimensionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var parsed = ParseDimensionInfo(dimensionElement);
            if (string.Equals(key, "time", StringComparison.OrdinalIgnoreCase))
            {
                timeDimension = parsed;
            }
            else if (string.Equals(key, "elevation", StringComparison.OrdinalIgnoreCase))
            {
                elevationDimension = parsed;
            }
        }

        return (timeDimension, elevationDimension);
    }

    private static GeoServerDimensionInfo ParseDimensionInfo(JsonElement element)
    {
        string? defaultStrategy = null;
        string? defaultReference = null;
        if (element.TryGetProperty("defaultValue", out var defaultValueElement) &&
            defaultValueElement.ValueKind == JsonValueKind.Object)
        {
            defaultStrategy = GetOptionalStringProperty(defaultValueElement, "strategy");
            defaultReference = GetOptionalStringProperty(defaultValueElement, "referenceValue");
        }

        return new GeoServerDimensionInfo
        {
            Enabled = GetOptionalBoolProperty(element, "enabled") ?? false,
            Attribute = GetOptionalStringProperty(element, "attribute"),
            EndAttribute = GetOptionalStringProperty(element, "endAttribute"),
            Presentation = GetOptionalStringProperty(element, "presentation"),
            Resolution = GetOptionalDoubleProperty(element, "resolution"),
            Units = GetOptionalStringProperty(element, "units"),
            UnitSymbol = GetOptionalStringProperty(element, "unitSymbol"),
            NearestMatchEnabled = GetOptionalBoolProperty(element, "nearestMatchEnabled") ?? false,
            DefaultValueStrategy = defaultStrategy,
            DefaultValueReference = defaultReference
        };
    }

    private async Task<GeoServerSecurityRule[]> GetSecurityRulesAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetRequiredJsonDocumentAsync(
                BuildJsonUrl(baseUrl, "security/acl/layers"),
                cancellationToken).ConfigureAwait(false);
            return ParseSecurityRuleDocument(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            Log.SecurityRulesUnavailable(_logger, ex);
            return [];
        }
    }

    private static GeoServerSecurityRule[] ParseSecurityRuleDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var rules = new List<GeoServerSecurityRule>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var roles = (property.Value.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static role => role.Length > 0)
                .ToArray();
            rules.Add(ParseSecurityRule(property.Name, roles));
        }

        return rules.OrderBy(static rule => rule.RuleKey, StringComparer.Ordinal).ToArray();
    }

    private static GeoServerSecurityRule ParseSecurityRule(string ruleKey, string[] roles)
    {
        if (string.Equals(ruleKey, "mode", StringComparison.OrdinalIgnoreCase))
        {
            return new GeoServerSecurityRule
            {
                RuleKey = ruleKey,
                WorkspacePattern = "*",
                LayerPattern = "*",
                Mode = "catalog-mode",
                Roles = roles
            };
        }

        var parts = ruleKey.Split('.');
        if (parts.Length == 3)
        {
            return new GeoServerSecurityRule
            {
                RuleKey = ruleKey,
                WorkspacePattern = parts[0],
                LayerPattern = parts[1],
                Mode = parts[2],
                Roles = roles
            };
        }

        return new GeoServerSecurityRule
        {
            RuleKey = ruleKey,
            WorkspacePattern = "*",
            LayerPattern = "*",
            Mode = "unknown",
            Roles = roles
        };
    }
}
