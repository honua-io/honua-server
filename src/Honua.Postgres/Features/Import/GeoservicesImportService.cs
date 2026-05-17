// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for importing data from ArcGIS Server services into PostGIS.
/// </summary>
internal sealed partial class GeoservicesImportService : IGeoservicesImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly ILogger<GeoservicesImportService> _logger;

    public GeoservicesImportService(
        ArcGisRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        ILogger<GeoservicesImportService> logger,
        ILayerPublishingService? layerPublishingService = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _layerPublishingService = layerPublishingService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return _restClient.DiscoverServiceAsync(
            request.ServiceUrl,
            request.TimeoutSeconds,
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUrl = ArcGisRestClient.NormalizeServiceUrl(request.ServiceUrl);

        try
        {
            using var serviceDocument = await _restClient.GetJsonDocumentAsync(
                $"{normalizedUrl}?f=json",
                ResiliencePolicyOptions.Default.MaxRetryAttempts,
                request.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (TryReadArcGisError(serviceDocument.RootElement, out var errorCode, out var errorMessage))
            {
                var (authMode, code) = ClassifyArcGisError(errorCode);
                return CreateFailedScanArtifact(
                    normalizedUrl,
                    authMode,
                    code,
                    errorMessage,
                    serviceType: ExtractServiceType(normalizedUrl));
            }

            var serviceKey = GetServiceKey(normalizedUrl);
            var serviceDisplayName = GetServiceDisplayName(serviceDocument.RootElement, serviceKey);
            var containerId = $"service:{serviceKey}";
            var serviceCapabilities = SplitCsv(GetOptionalStringProperty(serviceDocument.RootElement, "capabilities"));
            var completenessWarnings = new List<string>();
            var missingArtifacts = new List<string>();
            var resources = new List<MigrationInventoryResource>();
            var styles = new List<MigrationInventoryStyle>();
            var dependencies = new List<MigrationExternalDependency>();

            foreach (var resourceReference in EnumerateResourceReferences(serviceDocument.RootElement))
            {
                try
                {
                    var resourceResult = await BuildScanResourceAsync(
                        normalizedUrl,
                        containerId,
                        serviceKey,
                        resourceReference,
                        serviceCapabilities,
                        request.TimeoutSeconds,
                        cancellationToken).ConfigureAwait(false);

                    resources.Add(resourceResult.Resource);
                    if (resourceResult.Style != null)
                    {
                        styles.Add(resourceResult.Style);
                    }

                    dependencies.AddRange(resourceResult.Dependencies);
                    completenessWarnings.AddRange(resourceResult.Warnings);
                    missingArtifacts.AddRange(resourceResult.MissingArtifacts);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.InventoryResourceScanFailed(_logger, normalizedUrl, resourceReference.Id, resourceReference.Kind, ex);
                    completenessWarnings.Add($"Failed to scan {resourceReference.Kind} {resourceReference.Id}: {ex.Message}");
                    missingArtifacts.Add($"{resourceReference.Kind}:{resourceReference.Id}");
                }
            }

            var orderedResources = resources.OrderBy(static resource => resource.Id, StringComparer.Ordinal).ToArray();
            var orderedStyles = styles.OrderBy(static style => style.Id, StringComparer.Ordinal).ToArray();
            var orderedDependencies = dependencies.OrderBy(static dependency => dependency.Id, StringComparer.Ordinal).ToArray();

            var containerAssessment = MigrationInventoryHelpers.Aggregate(
                orderedResources.Select(resource => resource.Compatibility)
                    .Concat(orderedStyles.Select(style => style.Compatibility))
                    .Concat(orderedDependencies.Select(dependency => dependency.Compatibility)),
                "No GeoServices resources were discovered.");

            if (HasMixedRendererCodes(orderedStyles))
            {
                containerAssessment = containerAssessment with
                {
                    Code = ImportCompatibilityCodes.ArcGisMixedRenderers
                };
            }

            var containers = new[]
            {
                new MigrationInventoryContainer
                {
                    Id = containerId,
                    Kind = "service",
                    Name = serviceKey,
                    Title = serviceDisplayName,
                    Description = GetOptionalStringProperty(serviceDocument.RootElement, "description"),
                    Compatibility = containerAssessment
                }
            };

            if (orderedResources.Length == 0 && !missingArtifacts.Contains("resources", StringComparer.Ordinal))
            {
                missingArtifacts.Add("resources");
            }

            var summary = MigrationInventoryHelpers.BuildSummary(containers, orderedResources, orderedStyles, orderedDependencies);
            var overallCompatibility = MigrationInventoryHelpers.Aggregate(
                containers.Select(container => container.Compatibility)
                    .Concat(orderedResources.Select(resource => resource.Compatibility))
                    .Concat(orderedStyles.Select(style => style.Compatibility))
                    .Concat(orderedDependencies.Select(dependency => dependency.Compatibility)),
                "No inventory items were discovered.");

            var completeness = MigrationInventoryHelpers.BuildCompleteness(
                completenessWarnings.Count == 0
                    ? "complete"
                    : orderedResources.Length > 0 || orderedStyles.Length > 0 || orderedDependencies.Length > 0
                        ? "partial"
                        : "failed",
                completenessWarnings,
                missingArtifacts);

            return new MigrationSourceInventoryArtifact
            {
                SourceKind = "arcgis-geoservices-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = serviceDisplayName,
                    BaseUrl = normalizedUrl,
                    Product = "ArcGIS GeoServices REST",
                    Version = FormatVersion(serviceDocument.RootElement.TryGetProperty("currentVersion", out var versionElement) ? versionElement : default),
                    ServiceType = ExtractServiceType(normalizedUrl)
                },
                AuthPosture = new MigrationInventoryAuthPosture
                {
                    Mode = "anonymous",
                    CredentialsSupplied = false,
                    AccessConfirmed = true
                },
                ScanCompleteness = completeness,
                Summary = summary,
                OverallCompatibility = overallCompatibility,
                Containers = containers,
                Resources = orderedResources,
                Styles = orderedStyles,
                ExternalDependencies = orderedDependencies
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Log.InventoryScanFailed(_logger, normalizedUrl, ex);
            return CreateFailedScanArtifact(
                normalizedUrl,
                "auth-required",
                ex.StatusCode == HttpStatusCode.Forbidden
                    ? ImportCompatibilityCodes.ArcGisAccessDenied
                    : ImportCompatibilityCodes.ArcGisTokenRequired,
                "The ArcGIS service requires authentication for discovery.",
                ExtractServiceType(normalizedUrl));
        }
        catch (InvalidOperationException ex)
        {
            Log.InventoryScanFailed(_logger, normalizedUrl, ex);
            return CreateFailedScanArtifact(
                normalizedUrl,
                "unknown",
                ImportCompatibilityCodes.ArcGisServiceError,
                ex.Message,
                ExtractServiceType(normalizedUrl));
        }
    }

    /// <inheritdoc />
    public Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportLayerAsync(request, null, cancellationToken);
    }

    private async Task<GeoservicesScanResourceResult> BuildScanResourceAsync(
        string normalizedUrl,
        string containerId,
        string serviceName,
        GeoservicesResourceReference resourceReference,
        string[] serviceCapabilities,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var resourceDocument = await _restClient.GetJsonDocumentAsync(
            $"{normalizedUrl}/{resourceReference.Id}?f=json",
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (TryReadArcGisError(resourceDocument.RootElement, out _, out var resourceError))
        {
            throw new InvalidOperationException(resourceError);
        }

        var resourceCapabilities = SplitCsv(GetOptionalStringProperty(resourceDocument.RootElement, "capabilities"));
        var advertisedCapabilities = resourceCapabilities.Length == 0 ? serviceCapabilities : resourceCapabilities;
        var geometryType = GetOptionalStringProperty(resourceDocument.RootElement, "geometryType");
        var hasAttachments = GetOptionalBoolProperty(resourceDocument.RootElement, "hasAttachments");

        int? featureCount = null;
        try
        {
            featureCount = await TryGetFeatureCountAsync(
                normalizedUrl,
                resourceReference.Id,
                timeoutSeconds,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.InventoryFeatureCountFailed(_logger, normalizedUrl, resourceReference.Id, ex);
        }

        var spatialReferences = await BuildArcGisSpatialReferencesAsync(resourceDocument.RootElement, cancellationToken).ConfigureAwait(false);

        var fields = ExtractFieldMetadata(
            resourceDocument.RootElement,
            out var fieldWarnings);

        Log.InventoryFieldsExtracted(
            _logger,
            normalizedUrl,
            resourceReference.Id,
            fields.Length);

        var style = BuildRendererStyle(
            containerId,
            serviceName,
            resourceReference,
            resourceDocument.RootElement,
            out var rendererWarnings,
            out var rendererDependencies);

        var dependencies = new List<MigrationExternalDependency>(rendererDependencies);
        if (hasAttachments == true)
        {
            dependencies.Add(new MigrationExternalDependency
            {
                Id = $"dependency:{serviceName}:{resourceReference.Kind}:{resourceReference.Id}:attachments",
                ContainerId = containerId,
                ResourceId = GetResourceId(serviceName, resourceReference),
                Kind = "attachments",
                Name = resourceReference.Name,
                DependencyType = "attachments",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["advertised"] = "true"
                },
                SpatialReferences = [],
                Compatibility = MigrationInventoryHelpers.Partial(
                    "Attachments require a separate migration path.",
                    ["The source resource advertises attachments."],
                    ["Plan a separate attachment migration alongside the core data import."],
                    code: ImportCompatibilityCodes.ArcGisAttachments)
            });
        }

        var resourceWarnings = new List<string>(rendererWarnings);
        resourceWarnings.AddRange(fieldWarnings);
        var missingArtifacts = new List<string>();

        if (featureCount == null)
        {
            resourceWarnings.Add($"Feature count was unavailable for {resourceReference.Kind} {resourceReference.Id}.");
            missingArtifacts.Add($"{resourceReference.Kind}:{resourceReference.Id}:feature-count");
        }

        var compatibility = BuildResourceCompatibility(
            resourceReference.Kind,
            geometryType,
            advertisedCapabilities,
            hasAttachments,
            spatialReferences.Length > 0);

        var resource = new MigrationInventoryResource
        {
            Id = GetResourceId(serviceName, resourceReference),
            ContainerId = containerId,
            Kind = resourceReference.Kind,
            Name = resourceReference.Name,
            Title = GetOptionalStringProperty(resourceDocument.RootElement, "name") ?? resourceReference.Name,
            Description = GetOptionalStringProperty(resourceDocument.RootElement, "description"),
            GeometryType = geometryType,
            FeatureCount = featureCount,
            HasAttachments = hasAttachments,
            Capabilities = advertisedCapabilities.OrderBy(static capability => capability, StringComparer.Ordinal).ToArray(),
            SpatialReferences = spatialReferences,
            Fields = fields,
            StyleIds = style == null ? [] : [style.Id],
            ExternalDependencyIds = dependencies.Select(dependency => dependency.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            Compatibility = compatibility
        };

        return new GeoservicesScanResourceResult(
            resource,
            style,
            dependencies.ToArray(),
            MigrationInventoryHelpers.NormalizeStrings(resourceWarnings),
            MigrationInventoryHelpers.NormalizeStrings(missingArtifacts));
    }

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
        CancellationToken cancellationToken)
    {
        using var countDocument = await _restClient.GetJsonDocumentAsync(
            $"{normalizedUrl}/{resourceId}/query?where=1%3D1&returnCountOnly=true&f=json",
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (TryReadArcGisError(countDocument.RootElement, out _, out _))
        {
            return null;
        }

        return GetOptionalIntProperty(countDocument.RootElement, "count");
    }

    private const int CodedValueDomainCap = 100;

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

    private static MigrationSourceInventoryArtifact CreateFailedScanArtifact(
        string normalizedUrl,
        string authMode,
        string compatibilityCode,
        string message,
        string serviceType)
    {
        var manualSteps = compatibilityCode == ImportCompatibilityCodes.ArcGisTokenRequired
            ? new[] { "Provide a valid ArcGIS token or credentials and rerun the scan." }
            : compatibilityCode == ImportCompatibilityCodes.ArcGisAccessDenied
                ? ["Confirm the supplied identity has read access to the service and rerun the scan."]
                : new[] { "Verify service reachability, access, and metadata exposure before rerunning the scan." };

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "arcgis-geoservices-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = ExtractServiceName(normalizedUrl),
                BaseUrl = normalizedUrl,
                Product = "ArcGIS GeoServices REST",
                ServiceType = serviceType
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = authMode,
                CredentialsSupplied = false,
                AccessConfirmed = false,
                Notes = MigrationInventoryHelpers.NormalizeStrings([message])
            },
            ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                "failed",
                [message],
                ["source-inventory"]),
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = MigrationInventoryHelpers.Partial(
                "The scan did not complete successfully.",
                [message],
                manualSteps,
                code: compatibilityCode),
            Containers = [],
            Resources = [],
            Styles = [],
            ExternalDependencies = []
        };
    }

    private static (string AuthMode, string CompatibilityCode) ClassifyArcGisError(int? errorCode)
        => errorCode switch
        {
            498 or 499 => ("auth-required", ImportCompatibilityCodes.ArcGisTokenRequired),
            401 => ("auth-required", ImportCompatibilityCodes.ArcGisTokenRequired),
            403 => ("auth-required", ImportCompatibilityCodes.ArcGisAccessDenied),
            _ => ("unknown", ImportCompatibilityCodes.ArcGisServiceError)
        };

    private sealed record GeoservicesResourceReference(int Id, string Name, string Kind);

    private sealed record GeoservicesScanResourceResult(
        MigrationInventoryResource Resource,
        MigrationInventoryStyle? Style,
        MigrationExternalDependency[] Dependencies,
        string[] Warnings,
        string[] MissingArtifacts);

    /// <inheritdoc />
    public async Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateTableName(request.TableName);

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var startedAt = DateTimeOffset.UtcNow;

        Log.ImportStarting(_logger, request.ServiceUrl, request.LayerId, request.TableName);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Phase 1: Discover layer metadata
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Discovering, request,
                "Discovering layer metadata", 0, null);

            var layerInfo = await _restClient.GetLayerInfoAsync(
                request.ServiceUrl,
                request.LayerId,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken);

            Log.LayerDiscovered(_logger, layerInfo.Name, layerInfo.Fields.Length, layerInfo.FeatureCount);

            var totalFeatures = layerInfo.FeatureCount;
            var batchSize = request.BatchSize ?? layerInfo.MaxRecordCount ?? 1000;

            // Phase 2: Create table
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.CreatingTable, request,
                "Creating PostGIS table", 0, totalFeatures, layerInfo.Name);

            await CreateTableAsync(connection, request.TableName, layerInfo, request.TargetSrid,
                request.OverwriteExisting, cancellationToken);

            // Phase 3: Retrieve and insert features
            var featuresProcessed = 0;
            var failedFeatures = 0;
            var offset = 0;
            var batchNumber = 0;
            var hasMore = true;

            while (hasMore && !cancellationToken.IsCancellationRequested)
            {
                batchNumber++;
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.RetrievingFeatures, request,
                    $"Retrieving batch {batchNumber}", featuresProcessed, totalFeatures, layerInfo.Name);

                // Query features from remote service
                var queryResult = await _restClient.QueryFeaturesAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    offset,
                    batchSize,
                    request.WhereClause,
                    request.OutputFields,
                    request.TargetSrid,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken);

                if (queryResult.Features.Length == 0)
                {
                    hasMore = false;
                    break;
                }

                // Insert features into PostGIS
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.InsertingFeatures, request,
                    $"Inserting batch {batchNumber} ({queryResult.Features.Length} features)",
                    featuresProcessed, totalFeatures, layerInfo.Name);

                var (inserted, failed) = await InsertFeaturesAsync(
                    connection,
                    request.TableName,
                    layerInfo,
                    queryResult.Features,
                    request.TargetSrid,
                    cancellationToken);

                featuresProcessed += inserted;
                failedFeatures += failed;

                if (failed > 0)
                {
                    warnings.Add($"Batch {batchNumber}: {failed} features failed to insert");
                }

                Log.BatchCompleted(_logger, batchNumber, inserted, failed, featuresProcessed);

                offset += queryResult.Features.Length;
                hasMore = queryResult.ExceededTransferLimit || queryResult.Features.Length == batchSize;
            }

            // Phase 4: Create spatial index
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Publishing, request,
                "Creating spatial index", featuresProcessed, totalFeatures, layerInfo.Name);

            await CreateSpatialIndexAsync(connection, request.TableName, cancellationToken);
            await AnalyzeTableAsync(connection, request.TableName, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            PublishedLayerSummary? publishedLayer = null;
            if (request.AutoPublish)
            {
                publishedLayer = await TryPublishImportedLayerAsync(
                    request,
                    layerInfo,
                    warnings,
                    progress,
                    jobId,
                    startedAt,
                    featuresProcessed,
                    cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();

            Log.ImportCompleted(_logger, request.TableName, featuresProcessed, failedFeatures,
                stopwatch.Elapsed.TotalSeconds);

            // Report final progress
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Completed, request,
                publishedLayer == null ? "Import completed" : "Import completed and layer published",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name,
                publishedLayer?.LayerId);

            return GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featuresProcessed,
                failedFeatures,
                publishedLayer?.LayerId,
                publishedLayer?.ServiceName ?? request.ServiceName,
                layerInfo.Name,
                duration: stopwatch.Elapsed,
                warnings: warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Log.ImportCancelled(_logger, request.TableName);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            stopwatch.Stop();
            Log.ImportFailed(_logger, request.TableName, ex);

            return GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                "Import from ArcGIS service failed.",
                stopwatch.Elapsed);
        }
    }

    private async Task CreateTableAsync(
        NpgsqlConnection connection,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        int targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        if (overwriteExisting)
        {
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(tableName)} CASCADE";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var createSql = BuildCreateTableSql(tableName, layerInfo, targetSrid);
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createSql;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        Log.TableCreated(_logger, tableName);
    }

    private static string BuildCreateTableSql(string tableName, GeoservicesLayerInfo layerInfo, int targetSrid)
    {
        var columns = new List<string>
        {
            $"{FieldNames.ObjectId} BIGSERIAL PRIMARY KEY"
        };

        // Add attribute fields
        foreach (var field in layerInfo.Fields)
        {
            if (field.IsObjectId || IsGeometryField(field))
                continue; // We use the canonical objectid primary key instead

            var pgType = MapEsriTypeToPgType(field.Type, field.Length);
            var nullable = field.Nullable ? "" : " NOT NULL";
            columns.Add($"\"{field.Name.SanitizeFieldName()}\" {pgType}{nullable}");
        }

        // Add geometry column if the layer has geometry
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            var pgGeomType = MapEsriGeometryType(layerInfo.GeometryType);
            columns.Add($"geom geometry({pgGeomType}, {targetSrid})");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {QuoteIdentifier(tableName)} (");

        for (var i = 0; i < columns.Count; i++)
        {
            var suffix = i == columns.Count - 1 ? string.Empty : ",";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {columns[i]}{suffix}");
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string MapEsriTypeToPgType(string esriType, int? length)
    {
        return esriType.ToPostgresType(length);
    }

    private static string MapEsriGeometryType(string esriGeometryType)
    {
        return esriGeometryType.ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => "POINT",
            "ESRIGEOMETRYMULTIPOINT" => "MULTIPOINT",
            "ESRIGEOMETRYPOLYLINE" => "MULTILINESTRING",
            "ESRIGEOMETRYPOLYGON" => "MULTIPOLYGON",
            "ESRIGEOMETRYENVELOPE" => "POLYGON",
            _ => "GEOMETRY"
        };
    }

    private static bool IsGeometryField(GeoservicesFieldInfo field)
        => field.Type.Equals("esriFieldTypeGeometry", StringComparison.OrdinalIgnoreCase);


    private async Task<(int inserted, int failed)> InsertFeaturesAsync(
        NpgsqlConnection connection,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        ArcGisFeature[] features,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var failed = 0;
        string? firstError = null;
        var higherDimensionCount = 0;

        // Build insert statement
        var fields = layerInfo.Fields.Where(f => !f.IsObjectId && !IsGeometryField(f)).ToArray();
        var hasGeometry = !string.IsNullOrEmpty(layerInfo.GeometryType);

        var columnNames = string.Join(", ", fields.Select(f => $"\"{f.Name.SanitizeFieldName()}\""));
        if (hasGeometry)
        {
            columnNames += ", geom";
        }

        var parameterPlaceholders = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        if (hasGeometry)
        {
            parameterPlaceholders += $", ST_SetSRID(ST_GeomFromGeoJSON(@geom), {targetSrid})";
        }

        var insertSql = $"INSERT INTO {QuoteIdentifier(tableName)} ({columnNames}) VALUES ({parameterPlaceholders})";

        // Create the command once, add parameters with placeholder values, and prepare
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        for (var i = 0; i < fields.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", DBNull.Value);
        }

        if (hasGeometry)
        {
            cmd.Parameters.Add("geom", NpgsqlDbType.Text).Value = DBNull.Value;
        }

        await cmd.PrepareAsync(cancellationToken);

        foreach (var feature in features)
        {
            try
            {
                // Update attribute parameter values
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    object? value = null;

                    if (feature.Attributes?.TryGetValue(field.Name, out var jsonValue) == true)
                    {
                        value = ConvertJsonValue(jsonValue, field.Type);
                    }

                    cmd.Parameters[$"p{i}"].Value = value ?? DBNull.Value;
                }

                // Update geometry parameter value
                if (hasGeometry && feature.Geometry.HasValue)
                {
                    if (HasHigherDimensionCoordinates(feature.Geometry.Value))
                    {
                        higherDimensionCount++;
                    }

                    var geoJson = ConvertEsriGeometryToGeoJson(feature.Geometry.Value);
                    if (geoJson is null)
                    {
                        Log.GeometryConversionFailed(_logger, tableName);
                    }

                    cmd.Parameters["geom"].Value = geoJson ?? (object)DBNull.Value;
                }
                else if (hasGeometry)
                {
                    cmd.Parameters["geom"].Value = DBNull.Value;
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                inserted++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                Log.FeatureInsertFailed(_logger, ex.Message);
                failed++;
            }
        }

        if (firstError is not null)
        {
            Log.FeatureInsertFailures(_logger, failed, firstError);
        }

        if (higherDimensionCount > 0)
        {
            Log.HigherDimensionGeometryDetected(_logger, higherDimensionCount, tableName);
        }

        return (inserted, failed);
    }

    private static object? ConvertJsonValue(JsonElement element, string esriType)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        return esriType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" or "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" =>
                element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null,

            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" =>
                element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null,

            "ESRIFIELDTYPESTRING" =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),

            "ESRIFIELDTYPEDATE" =>
                element.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(element.GetInt64())
                    : null,

            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" =>
                element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid)
                    ? guid
                    : null,

            _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
        };
    }

    private static string? ConvertEsriGeometryToGeoJson(JsonElement geometry)
    {
        // Esri JSON geometry format is similar to GeoJSON but not identical
        // This converts common geometry types to GeoJSON
        try
        {
            if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y))
            {
                // Point
                return BuildPointGeoJson(x.GetDouble(), y.GetDouble());
            }

            if (geometry.TryGetProperty("rings", out var rings))
            {
                // Polygon
                var ringCoordinates = rings.EnumerateArray()
                    .Select(ring => ring.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .Select(EnsureClosedRing)
                    .Where(ring => ring.Length >= 4)
                    .ToArray();

                if (ringCoordinates.Length == 0)
                    return null;

                var polygons = ClassifyPolygonRings(ringCoordinates);
                return polygons.Length == 0 ? null : BuildMultiPolygonGeoJson(polygons);
            }

            if (geometry.TryGetProperty("paths", out var paths))
            {
                // Polyline
                var coordinates = paths.EnumerateArray()
                    .Select(path => path.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .Where(path => path.Length >= 2)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiLineStringGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("points", out var points))
            {
                // Multipoint
                var coordinates = points.EnumerateArray()
                    .Where(p => p.GetArrayLength() >= 2)
                    .Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiPointGeoJson(coordinates);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasHigherDimensionCoordinates(JsonElement geometry)
    {
        try
        {
            // Point: check for z property
            if (geometry.TryGetProperty("z", out _))
                return true;

            // Rings (polygon), paths (polyline), points (multipoint): check coordinate array lengths
            foreach (var propName in new[] { "rings", "paths" })
            {
                if (geometry.TryGetProperty(propName, out var arrays))
                {
                    foreach (var array in arrays.EnumerateArray())
                    {
                        foreach (var coord in array.EnumerateArray())
                        {
                            if (coord.GetArrayLength() > 2)
                                return true;
                        }
                    }
                }
            }

            if (geometry.TryGetProperty("points", out var pts))
            {
                foreach (var coord in pts.EnumerateArray())
                {
                    if (coord.GetArrayLength() > 2)
                        return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildPointGeoJson(double x, double y)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Point");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static double[][] EnsureClosedRing(double[][] ring)
    {
        if (ring.Length == 0)
        {
            return ring;
        }

        var first = ring[0];
        var last = ring[^1];
        if (first.Length >= 2 && last.Length >= 2 && first[0] == last[0] && first[1] == last[1])
        {
            return ring;
        }

        var closed = new double[ring.Length + 1][];
        Array.Copy(ring, closed, ring.Length);
        closed[^1] = [first[0], first[1]];
        return closed;
    }

    private static double[][][][] ClassifyPolygonRings(double[][][] rings)
    {
        var classified = rings
            .Select(ring => new EsriPolygonRing(ring, SignedRingArea(ring)))
            .Where(ring => ring.AbsArea > 0)
            .ToArray();

        if (classified.Length == 0)
        {
            return [];
        }

        for (var i = 0; i < classified.Length; i++)
        {
            var parentIndex = -1;
            var parentArea = double.PositiveInfinity;

            for (var j = 0; j < classified.Length; j++)
            {
                if (i == j || classified[j].AbsArea <= classified[i].AbsArea)
                {
                    continue;
                }

                if (classified[j].AbsArea < parentArea &&
                    RingContainsPoint(classified[j].Coordinates, classified[i].Coordinates[0]))
                {
                    parentIndex = j;
                    parentArea = classified[j].AbsArea;
                }
            }

            classified[i].ParentIndex = parentIndex;
        }

        for (var i = 0; i < classified.Length; i++)
        {
            var depth = 0;
            var parentIndex = classified[i].ParentIndex;
            while (parentIndex >= 0)
            {
                depth++;
                parentIndex = classified[parentIndex].ParentIndex;
            }

            classified[i].Depth = depth;
        }

        var polygons = new List<double[][][]>();
        var shellIndexes = classified
            .Select((ring, index) => (Ring: ring, Index: index))
            .Where(item => item.Ring.Depth % 2 == 0)
            .OrderBy(item => item.Index)
            .ToArray();

        foreach (var shell in shellIndexes)
        {
            var polygonRings = new List<double[][]>
            {
                EnsureCounterClockwise(shell.Ring.Coordinates)
            };

            for (var i = 0; i < classified.Length; i++)
            {
                if (classified[i].Depth % 2 == 0)
                {
                    continue;
                }

                if (FindNearestEvenDepthAncestor(classified, i) == shell.Index)
                {
                    polygonRings.Add(EnsureClockwise(classified[i].Coordinates));
                }
            }

            polygons.Add(polygonRings.ToArray());
        }

        return polygons.ToArray();
    }

    private static int FindNearestEvenDepthAncestor(EsriPolygonRing[] rings, int ringIndex)
    {
        var parentIndex = rings[ringIndex].ParentIndex;
        while (parentIndex >= 0)
        {
            if (rings[parentIndex].Depth % 2 == 0)
            {
                return parentIndex;
            }

            parentIndex = rings[parentIndex].ParentIndex;
        }

        return -1;
    }

    private static double SignedRingArea(double[][] ring)
    {
        var area = 0d;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            area += (ring[i][0] * ring[i + 1][1]) - (ring[i + 1][0] * ring[i][1]);
        }

        return area / 2d;
    }

    private static bool RingContainsPoint(double[][] ring, double[] point)
    {
        var inside = false;
        var x = point[0];
        var y = point[1];

        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var xi = ring[i][0];
            var yi = ring[i][1];
            var xj = ring[j][0];
            var yj = ring[j][1];

            if ((yi > y) != (yj > y) &&
                x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double[][] EnsureCounterClockwise(double[][] ring)
        => SignedRingArea(ring) < 0 ? ReverseRing(ring) : ring;

    private static double[][] EnsureClockwise(double[][] ring)
        => SignedRingArea(ring) > 0 ? ReverseRing(ring) : ring;

    private static double[][] ReverseRing(double[][] ring)
    {
        var reversed = ring.Reverse()
            .Select(coord => new[] { coord[0], coord[1] })
            .ToArray();
        return EnsureClosedRing(reversed);
    }

    private sealed class EsriPolygonRing
    {
        public EsriPolygonRing(double[][] coordinates, double signedArea)
        {
            Coordinates = coordinates;
            SignedArea = signedArea;
            AbsArea = Math.Abs(signedArea);
        }

        public double[][] Coordinates { get; }
        public double SignedArea { get; }
        public double AbsArea { get; }
        public int ParentIndex { get; set; } = -1;
        public int Depth { get; set; }
    }

    private static string BuildMultiPolygonGeoJson(double[][][][] polygons)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPolygon");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var polygon in polygons)
            {
                writer.WriteStartArray();
                foreach (var ring in polygon)
                {
                    writer.WriteStartArray();
                    foreach (var coord in ring)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(coord[0]);
                        writer.WriteNumberValue(coord[1]);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiLineStringGeoJson(double[][][] lines)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiLineString");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var line in lines)
            {
                writer.WriteStartArray();
                foreach (var coord in line)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(coord[0]);
                    writer.WriteNumberValue(coord[1]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiPointGeoJson(double[][] points)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPoint");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var point in points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(point[0]);
                writer.WriteNumberValue(point[1]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildGeoJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task CreateSpatialIndexAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(tableName + "_geom_idx")} ON {QuoteIdentifier(tableName)} USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Log.SpatialIndexCreated(_logger, tableName);
    }

    private static async Task AnalyzeTableAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ANALYZE {QuoteIdentifier(tableName)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);

    private async Task<PublishedLayerSummary?> TryPublishImportedLayerAsync(
        GeoservicesImportRequest request,
        GeoservicesLayerInfo layerInfo,
        List<string> warnings,
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        int featuresProcessed,
        CancellationToken cancellationToken)
    {
        if (_layerPublishingService == null)
        {
            warnings.Add("AutoPublish was requested, but no layer publishing service is registered for this server.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ServiceName))
        {
            warnings.Add("AutoPublish was requested, but no target serviceName was supplied; the imported table was not published.");
            return null;
        }

        try
        {
            ReportProgress(
                progress,
                jobId,
                startedAt,
                GeoservicesImportStatus.Publishing,
                request,
                "Publishing imported layer",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name);

            var publishRequest = new LayerPublishRequest
            {
                Schema = "public",
                Table = request.TableName,
                LayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
                Description = layerInfo.Description,
                GeometryColumn = "geom",
                GeometryType = string.IsNullOrWhiteSpace(layerInfo.GeometryType)
                    ? null
                    : MapEsriGeometryType(layerInfo.GeometryType),
                Srid = request.TargetSrid,
                PrimaryKey = FieldNames.ObjectId,
                Fields = [],
                ServiceName = request.ServiceName,
                Enabled = true
            };

            return await _layerPublishingService.PublishLayerAsync(
                    _connectionProvider.GetConnectionString(),
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LayerPublishingException ex)
        {
            warnings.Add($"AutoPublish was requested, but publishing did not complete: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoPublishFailed(_logger, request.TableName, request.ServiceName!, ex);
            warnings.Add("AutoPublish was requested, but publishing did not complete.");
            return null;
        }
    }

    private static void ReportProgress(
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        GeoservicesImportStatus status,
        GeoservicesImportRequest request,
        string phase,
        int featuresProcessed,
        int? totalFeatures,
        string? layerName = null,
        int? publishedLayerId = null)
    {
        progress?.Report(new GeoservicesImportProgress
        {
            JobId = jobId,
            Status = status,
            FeaturesProcessed = featuresProcessed,
            EstimatedTotalFeatures = totalFeatures,
            SourceServiceUrl = request.ServiceUrl,
            SourceLayerId = request.LayerId,
            SourceLayerName = layerName,
            TableName = request.TableName,
            ServiceName = request.ServiceName,
            PublishedLayerId = publishedLayerId,
            StartedAt = startedAt,
            CurrentPhase = phase
        });
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*$")]
    private static partial Regex TableNameRegex();

    private static void ValidateTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        if (tableName.Length > 63)
            throw new ArgumentException("Table name exceeds PostgreSQL identifier limit of 63 characters", nameof(tableName));

        if (!TableNameRegex().IsMatch(tableName))
            throw new ArgumentException("Table name must start with a letter and contain only letters, digits, and underscores", nameof(tableName));
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static partial class Log
    {
        [LoggerMessage(7820, LogLevel.Information,
            "Starting Geoservices import from {ServiceUrl} layer {LayerId} to table {TableName}")]
        public static partial void ImportStarting(ILogger logger, string serviceUrl, int layerId, string tableName);

        [LoggerMessage(7821, LogLevel.Information,
            "Layer discovered: {LayerName}, {FieldCount} fields, ~{FeatureCount} features")]
        public static partial void LayerDiscovered(ILogger logger, string layerName, int fieldCount, int? featureCount);

        [LoggerMessage(78215, LogLevel.Warning,
            "GeoServices inventory scan failed for {ServiceUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string serviceUrl, Exception exception);

        [LoggerMessage(78216, LogLevel.Warning,
            "GeoServices inventory resource scan failed for {ServiceUrl} resource {ResourceId} ({ResourceKind})")]
        public static partial void InventoryResourceScanFailed(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            string resourceKind,
            Exception exception);

        [LoggerMessage(78217, LogLevel.Debug,
            "GeoServices feature count was unavailable for {ServiceUrl} resource {ResourceId}")]
        public static partial void InventoryFeatureCountFailed(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            Exception exception);

        [LoggerMessage(78218, LogLevel.Debug,
            "GeoServices inventory captured {FieldCount} fields for {ServiceUrl} resource {ResourceId}")]
        public static partial void InventoryFieldsExtracted(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            int fieldCount);

        [LoggerMessage(7822, LogLevel.Debug, "Table {TableName} created")]
        public static partial void TableCreated(ILogger logger, string tableName);

        [LoggerMessage(7823, LogLevel.Debug,
            "Batch {BatchNumber} completed: {Inserted} inserted, {Failed} failed, {Total} total")]
        public static partial void BatchCompleted(ILogger logger, int batchNumber, int inserted, int failed, int total);

        [LoggerMessage(7824, LogLevel.Debug, "Spatial index created on {TableName}")]
        public static partial void SpatialIndexCreated(ILogger logger, string tableName);

        [LoggerMessage(7825, LogLevel.Information,
            "Import completed: {TableName}, {FeatureCount} features, {FailedCount} failed, {DurationSeconds:F1}s")]
        public static partial void ImportCompleted(
            ILogger logger, string tableName, int featureCount, int failedCount, double durationSeconds);

        [LoggerMessage(7826, LogLevel.Warning, "Import cancelled: {TableName}")]
        public static partial void ImportCancelled(ILogger logger, string tableName);

        [LoggerMessage(7827, LogLevel.Error, "Import failed: {TableName}")]
        public static partial void ImportFailed(ILogger logger, string tableName, Exception exception);

        [LoggerMessage(7828, LogLevel.Debug, "Feature insert failed: {ErrorMessage}")]
        public static partial void FeatureInsertFailed(ILogger logger, string errorMessage);

        [LoggerMessage(7829, LogLevel.Warning,
            "Geoservices import encountered {FailedCount} insert failures. First error: {ErrorMessage}")]
        public static partial void FeatureInsertFailures(ILogger logger, int failedCount, string errorMessage);

        [LoggerMessage(7830, LogLevel.Warning,
            "Geometry conversion failed for feature with non-null geometry in table {TableName}")]
        public static partial void GeometryConversionFailed(ILogger logger, string tableName);

        [LoggerMessage(7831, LogLevel.Warning,
            "Batch contains {Count} features with higher-dimension (Z/M) coordinates that will be dropped during 2D import in table {TableName}")]
        public static partial void HigherDimensionGeometryDetected(ILogger logger, int count, string tableName);

        [LoggerMessage(7832, LogLevel.Warning, "Auto-publish failed for imported table {TableName} into service {ServiceName}")]
        public static partial void AutoPublishFailed(ILogger logger, string tableName, string serviceName, Exception exception);
    }
}
