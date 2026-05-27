// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Postgres.Features.Import;

internal sealed partial class GeoservicesImportService
{
    private static IEnumerable<GeoservicesResourceReference> EnumerateResourceReferences(JsonElement serviceRoot)
    {
        foreach (var layer in EnumerateResourceReferences(serviceRoot, "layers", "layer"))
        {
            yield return layer with { Kind = "layer" };
        }

        foreach (var table in EnumerateResourceReferences(serviceRoot, "tables", "table"))
        {
            yield return table with { Kind = "table" };
        }
    }

    private static IEnumerable<GeoservicesResourceReference> EnumerateResourceReferences(
        JsonElement serviceRoot,
        string collectionName,
        string defaultKind)
    {
        if (!serviceRoot.TryGetProperty(collectionName, out var collection) || collection.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in collection.EnumerateArray().OrderBy(static element => GetOptionalIntProperty(element, "id") ?? int.MaxValue))
        {
            var id = GetOptionalIntProperty(item, "id");
            if (!id.HasValue)
            {
                continue;
            }

            yield return new GeoservicesResourceReference(
                id.Value,
                GetOptionalStringProperty(item, "name") ?? $"{defaultKind}-{id.Value}",
                defaultKind);
        }
    }

    private static string GetServiceDisplayName(JsonElement serviceRoot, string serviceKey)
        => GetOptionalStringProperty(serviceRoot, "serviceDescription") ?? serviceKey;

    private static string GetServiceKey(string normalizedUrl)
        => ExtractServiceName(normalizedUrl);

    private static string ExtractServiceName(string normalizedUrl)
    {
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("MapServer", StringComparison.OrdinalIgnoreCase))
            {
                return i > 0 ? segments[i - 1] : segments[i];
            }
        }

        return segments.Length == 0 ? "ArcGIS Service" : segments[^1];
    }

    private static string ExtractServiceType(string normalizedUrl)
    {
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "GeoServices" : segments[^1];
    }

    private static string? BuildArcGisSourceValue(int? wkid, int? latestWkid, string? wkt)
    {
        if (wkid.HasValue)
        {
            if (latestWkid.HasValue && latestWkid.Value != wkid.Value)
            {
                return $"WKID:{wkid.Value}; latestWkid:{latestWkid.Value}";
            }

            return $"WKID:{wkid.Value}";
        }

        if (latestWkid.HasValue)
        {
            return $"latestWkid:{latestWkid.Value}";
        }

        return wkt;
    }

    private static int? NormalizeArcGisWkid(int? wkid)
        => wkid switch
        {
            102100 or 102113 or 900913 or 3785 => 3857,
            _ => wkid
        };

    private static bool IsKnownArcGisAlias(int? wkid)
        => wkid is 102100 or 102113 or 900913 or 3785;

    private static string[] SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray();

    private static string FormatVersion(JsonElement versionElement)
    {
        return versionElement.ValueKind switch
        {
            JsonValueKind.Number => versionElement.GetRawText(),
            JsonValueKind.String => versionElement.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string GetResourceId(string serviceName, GeoservicesResourceReference resourceReference)
        => $"resource:{serviceName}:{resourceReference.Kind}:{resourceReference.Id}";

    private static bool TryReadArcGisError(JsonElement rootElement, out int? code, out string message)
    {
        code = null;
        message = string.Empty;

        if (!rootElement.TryGetProperty("error", out var errorElement) || errorElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        code = GetOptionalIntProperty(errorElement, "code");
        message = GetOptionalStringProperty(errorElement, "message") ?? "ArcGIS service returned an error.";
        return true;
    }

    private static bool IsAuthError(int? code)
        => code is 401 or 403 or 498 or 499;

    private static bool TryGetSpatialReference(JsonElement element, out JsonElement spatialReference)
    {
        if (element.TryGetProperty("spatialReference", out spatialReference) &&
            spatialReference.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        spatialReference = default;
        return false;
    }

    private static string? GetOptionalStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetOptionalIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            return intValue;
        }

        return null;
    }

    private static bool? GetOptionalBoolProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static void AddRendererMetadata(SortedDictionary<string, string> metadata, JsonElement renderer, string propertyName)
    {
        var value = GetOptionalStringProperty(renderer, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[propertyName] = value;
        }
    }

    private static string[] ExtractJsonUrls(JsonElement element)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        CollectJsonUrls(element, urls);
        return urls.OrderBy(static url => url, StringComparer.Ordinal).ToArray();
    }

    private static void CollectJsonUrls(JsonElement element, ISet<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("url") || property.NameEquals("href")) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        var candidate = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate) &&
                            Uri.TryCreate(candidate, UriKind.Absolute, out _))
                        {
                            _ = urls.Add(candidate);
                        }
                    }

                    CollectJsonUrls(property.Value, urls);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectJsonUrls(child, urls);
                }

                break;
        }
    }

    private static bool HasCredentialMaterial(GeoservicesCredentialDescriptor? credentials)
        => credentials?.HasCredentialMaterial == true &&
            credentials.GetNormalizedMode() != GeoservicesAuthenticationModes.Anonymous;

    private static (string AuthMode, string CompatibilityCode) ClassifyArcGisError(
        int? errorCode,
        GeoservicesCredentialDescriptor? credentials)
        => errorCode switch
        {
            498 => (GeoservicesAuthenticationModes.ExpiredToken, ImportCompatibilityCodes.ArcGisTokenExpired),
            499 => (GeoservicesAuthenticationModes.AuthRequired, ImportCompatibilityCodes.ArcGisTokenRequired),
            401 => (HasCredentialMaterial(credentials)
                ? GeoservicesAuthenticationModes.Denied
                : GeoservicesAuthenticationModes.AuthRequired, ImportCompatibilityCodes.ArcGisTokenRequired),
            403 => (GeoservicesAuthenticationModes.Denied, ImportCompatibilityCodes.ArcGisAccessDenied),
            _ => (GeoservicesAuthenticationModes.Unknown, ImportCompatibilityCodes.ArcGisServiceError)
        };

    private sealed record GeoservicesResourceReference(int Id, string Name, string Kind);

    private sealed record GeoservicesScanResourceResult(
        MigrationInventoryResource Resource,
        MigrationInventoryStyle? Style,
        MigrationExternalDependency[] Dependencies,
        MigrationFidelityClassificationRecord[] FidelityClassifications,
        string[] Warnings,
        string[] MissingArtifacts);
}
