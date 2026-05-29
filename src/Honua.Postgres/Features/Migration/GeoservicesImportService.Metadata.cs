// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private const int CodedValueDomainCap = 100;

    private async Task<MigrationSpatialReferenceInfo[]> BuildArcGisSpatialReferencesAsync(
        JsonElement resourceElement,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<MigrationSpatialReferenceInfo?>>(capacity: 3);

        if (TryGetSpatialReference(resourceElement, out var spatialReference))
        {
            tasks.Add(BuildArcGisSpatialReferenceAsync("resource", spatialReference, cancellationToken));
        }

        if (resourceElement.TryGetProperty("sourceSpatialReference", out var sourceSpatialReference))
        {
            tasks.Add(BuildArcGisSpatialReferenceAsync("source", sourceSpatialReference, cancellationToken));
        }

        if (resourceElement.TryGetProperty("extent", out var extentElement) &&
            TryGetSpatialReference(extentElement, out var extentSpatialReference))
        {
            tasks.Add(BuildArcGisSpatialReferenceAsync("extent", extentSpatialReference, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return [];
        }

        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .OfType<MigrationSpatialReferenceInfo>()
            .GroupBy(info => $"{info.Role}|{info.Srid}|{info.SourceValue}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(static info => info.Role, StringComparer.Ordinal)
            .ToArray();
    }

    private Task<MigrationSpatialReferenceInfo?> BuildArcGisSpatialReferenceAsync(
        string role,
        JsonElement spatialReference,
        CancellationToken cancellationToken)
    {
        var wkid = GetOptionalIntProperty(spatialReference, "wkid");
        var latestWkid = GetOptionalIntProperty(spatialReference, "latestWkid");
        var wkt = GetOptionalStringProperty(spatialReference, "wkt");
        var sourceValue = BuildArcGisSourceValue(wkid, latestWkid, wkt);
        var normalizedSrid = latestWkid ?? NormalizeArcGisWkid(wkid);
        var allowFallbackCrsUri = latestWkid.HasValue || IsKnownArcGisAlias(wkid);

        return MigrationInventoryHelpers.BuildSpatialReferenceAsync(
            _crsRegistry,
            role,
            sourceValue,
            cancellationToken,
            explicitSrid: normalizedSrid,
            explicitWkt: wkt,
            allowFallbackCrsUri: allowFallbackCrsUri);
    }

    private async Task<int?> TryGetFeatureCountAsync(
        string normalizedUrl,
        int resourceId,
        int timeoutSeconds,
        GeoservicesCredentialDescriptor? credentials,
        CancellationToken cancellationToken)
    {
        using var countDocument = await _restClient.GetJsonDocumentAsync(
            $"{normalizedUrl}/{resourceId}/query?where=1%3D1&returnCountOnly=true&f=json",
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            timeoutSeconds,
            credentials,
            cancellationToken).ConfigureAwait(false);

        if (TryReadArcGisError(countDocument.RootElement, out _, out _))
        {
            return null;
        }

        return GetOptionalIntProperty(countDocument.RootElement, "count");
    }

    private static MigrationInventoryField[] ExtractFieldMetadata(JsonElement resourceElement, out string[] warnings)
    {
        warnings = [];

        if (!resourceElement.TryGetProperty("fields", out var fieldsElement) ||
            fieldsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<MigrationInventoryField>();
        var truncatedDomains = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var fieldElement in fieldsElement.EnumerateArray())
        {
            if (fieldElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetOptionalStringProperty(fieldElement, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var fieldType = GetOptionalStringProperty(fieldElement, "type") ?? "esriFieldTypeUnknown";
            var alias = GetOptionalStringProperty(fieldElement, "alias");
            var nullable = GetOptionalBoolProperty(fieldElement, "nullable");
            var (domainType, domainName, domainValues, domainTruncated) = ExtractDomain(fieldElement);

            if (domainTruncated)
            {
                var label = !string.IsNullOrWhiteSpace(domainName)
                    ? $"'{domainName}' on field '{name}'"
                    : $"on field '{name}'";
                _ = truncatedDomains.Add(label);
            }

            fields.Add(new MigrationInventoryField
            {
                Name = name,
                Alias = string.Equals(alias, name, StringComparison.Ordinal) ? null : alias,
                FieldType = fieldType,
                Nullable = nullable,
                DomainType = domainType,
                DomainName = domainName,
                DomainValues = domainValues
            });
        }

        if (truncatedDomains.Count > 0)
        {
            warnings = truncatedDomains
                .Select(static label => $"{ImportCompatibilityCodes.ArcGisDomainTruncated}: coded-value domain {label} exceeds the {CodedValueDomainCap}-entry capture cap and was omitted from the artifact.")
                .ToArray();
        }

        return fields
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string? Type, string? Name, MigrationInventoryCodedValue[]? Values, bool Truncated) ExtractDomain(JsonElement fieldElement)
    {
        if (!fieldElement.TryGetProperty("domain", out var domainElement) ||
            domainElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, false);
        }

        var type = GetOptionalStringProperty(domainElement, "type");
        var name = GetOptionalStringProperty(domainElement, "name");

        if (!string.Equals(type, "codedValue", StringComparison.Ordinal))
        {
            return (type, name, null, false);
        }

        if (!domainElement.TryGetProperty("codedValues", out var codedValuesElement) ||
            codedValuesElement.ValueKind != JsonValueKind.Array)
        {
            return (type, name, [], false);
        }

        var entries = new List<MigrationInventoryCodedValue>();
        var truncated = false;
        foreach (var entry in codedValuesElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var codeText = ConvertCodedValueCode(entry);
            var entryName = GetOptionalStringProperty(entry, "name");
            if (codeText == null || string.IsNullOrWhiteSpace(entryName))
            {
                continue;
            }

            if (entries.Count >= CodedValueDomainCap)
            {
                truncated = true;
                break;
            }

            entries.Add(new MigrationInventoryCodedValue
            {
                Code = codeText,
                Name = entryName!
            });
        }

        var ordered = entries
            .OrderBy(static value => value.Code, StringComparer.Ordinal)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

        return truncated
            ? (type, name, null, true)
            : (type, name, ordered, false);
    }

    private static string? ConvertCodedValueCode(JsonElement entry)
    {
        if (!entry.TryGetProperty("code", out var codeElement))
        {
            return null;
        }

        return codeElement.ValueKind switch
        {
            JsonValueKind.String => codeElement.GetString(),
            JsonValueKind.Number => codeElement.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static MigrationInventoryStyle? BuildRendererStyle(
        string containerId,
        string serviceName,
        GeoservicesResourceReference resourceReference,
        JsonElement resourceElement,
        out string[] warnings,
        out MigrationExternalDependency[] dependencies)
    {
        warnings = [];
        dependencies = [];

        if (!resourceElement.TryGetProperty("drawingInfo", out var drawingInfo) ||
            !drawingInfo.TryGetProperty("renderer", out var renderer) ||
            renderer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rendererType = GetOptionalStringProperty(renderer, "type") ?? "unknown";
        var resourceId = GetResourceId(serviceName, resourceReference);
        var styleId = $"renderer:{serviceName}:{resourceReference.Id}";
        var externalUrls = ExtractJsonUrls(renderer)
            .Select(MigrationInventoryHelpers.NormalizeExternalAddress)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static address => address, StringComparer.Ordinal)
            .ToArray();

        warnings = externalUrls.Length == 0
            ? []
            : ["Renderer references one or more external symbol URLs."];

        dependencies = externalUrls.Select(address => new MigrationExternalDependency
        {
            Id = MigrationInventoryHelpers.BuildExternalDependencyId(styleId, address),
            ContainerId = containerId,
            ResourceId = resourceId,
            Kind = "external-symbol",
            Name = resourceReference.Name,
            DependencyType = "url",
            Address = address,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "renderer"
            },
            SpatialReferences = [],
            Compatibility = MigrationInventoryHelpers.Partial(
                "External symbol URLs require manual migration review.",
                ["The renderer references one or more external URLs."],
                ["Mirror or replace external symbol assets in the target deployment."],
                code: ImportCompatibilityCodes.ArcGisExternalSymbol)
        }).ToArray();

        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["rendererType"] = rendererType
        };

        AddRendererMetadata(metadata, renderer, "field1");
        AddRendererMetadata(metadata, renderer, "field2");
        AddRendererMetadata(metadata, renderer, "field3");
        AddRendererMetadata(metadata, renderer, "normalizationField");

        return new MigrationInventoryStyle
        {
            Id = styleId,
            ContainerId = containerId,
            Kind = "renderer",
            Name = resourceReference.Name,
            Format = "esri-renderer",
            ResourceIds = [resourceId],
            ExternalDependencyIds = dependencies.Select(dependency => dependency.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            Metadata = metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            Compatibility = BuildRendererCompatibility(rendererType, externalUrls.Length > 0)
        };
    }

    private static MigrationCompatibilityAssessment BuildRendererCompatibility(string rendererType, bool hasExternalUrls)
    {
        var warnings = new List<string>();
        var manualSteps = new List<string>();

        if (hasExternalUrls)
        {
            warnings.Add("Renderer references external symbol URLs.");
            manualSteps.Add("Mirror or replace external symbol assets in the target deployment.");
        }

        switch (rendererType)
        {
            case "simple":
            case "uniqueValue":
            case "classBreaks":
                manualSteps.Add("Recreate the renderer via the Honua style endpoints after data import.");
                return MigrationInventoryHelpers.Partial(
                    $"Renderer type '{rendererType}' can be recreated in Honua with manual follow-up.",
                    warnings,
                    manualSteps,
                    code: hasExternalUrls
                        ? ImportCompatibilityCodes.ArcGisExternalSymbol
                        : ImportCompatibilityCodes.ManualReview);
            default:
                manualSteps.Add("Review the renderer manually and rebuild an equivalent target style.");
                return MigrationInventoryHelpers.Incompatible(
                    $"Renderer type '{rendererType}' is not currently classified as portable to Honua.",
                    warnings,
                    manualSteps,
                    code: ImportCompatibilityCodes.ArcGisUnsupportedRenderer);
        }
    }

    private static MigrationCompatibilityAssessment BuildResourceCompatibility(
        string resourceKind,
        string? geometryType,
        IReadOnlyCollection<string> capabilities,
        bool? hasAttachments,
        bool hasSpatialReference)
    {
        var warnings = new List<string>();
        var manualSteps = new List<string>();

        if (!capabilities.Contains("Query", StringComparer.OrdinalIgnoreCase))
        {
            return MigrationInventoryHelpers.Incompatible(
                "The resource does not advertise query capability.",
                null,
                ["Enable query access or export the source data through another path before migration."],
                code: ImportCompatibilityCodes.ArcGisQueryCapabilityMissing);
        }

        if (!resourceKind.Equals("table", StringComparison.OrdinalIgnoreCase) &&
            !IsSupportedGeometryType(geometryType))
        {
            return MigrationInventoryHelpers.Incompatible(
                $"Geometry type '{geometryType ?? "unknown"}' is not supported by the current import path.",
                null,
                ["Normalize or export the resource to a supported vector geometry type before migration."],
                code: ImportCompatibilityCodes.ArcGisUnsupportedGeometry);
        }

        string? primaryCode = null;
        if (!resourceKind.Equals("table", StringComparison.OrdinalIgnoreCase) && !hasSpatialReference)
        {
            warnings.Add("Spatial reference metadata was unavailable.");
            manualSteps.Add("Confirm CRS, datum, and units before migration.");
            primaryCode = ImportCompatibilityCodes.ArcGisMissingSpatialRef;
        }

        if (hasAttachments == true)
        {
            warnings.Add("Attachments are advertised on this resource.");
            manualSteps.Add("Plan a separate attachment migration.");
            primaryCode ??= ImportCompatibilityCodes.ArcGisAttachments;
        }

        if (warnings.Count == 0)
        {
            return MigrationInventoryHelpers.Compatible(
                resourceKind.Equals("table", StringComparison.OrdinalIgnoreCase)
                    ? "Tabular resource can be queried through the GeoServices API."
                    : "Vector resource can be queried through the GeoServices API.",
                code: ImportCompatibilityCodes.Compatible);
        }

        return MigrationInventoryHelpers.Partial(
            "The resource data is queryable, but follow-up migration work is required.",
            warnings,
            manualSteps,
            code: primaryCode ?? ImportCompatibilityCodes.ManualReview);
    }

    private static bool IsSupportedGeometryType(string? geometryType)
        => geometryType is "esriGeometryPoint" or "esriGeometryPolyline" or "esriGeometryPolygon" or "esriGeometryMultipoint";

    private static bool HasMixedRendererCodes(MigrationInventoryStyle[] styles)
    {
        if (styles.Length < 2)
        {
            return false;
        }

        string? seen = null;
        foreach (var style in styles)
        {
            var code = style.Compatibility.Code;
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (seen == null)
            {
                seen = code;
                continue;
            }

            if (!string.Equals(seen, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
