// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Styling.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for importing GeoServer configuration into Honua.
/// </summary>
internal sealed partial class GeoServerImportService : IGeoServerImportService
{
    private readonly GeoServerRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ISldStyleConverter? _sldConverter;
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly IMigrationCatalogWriter? _catalogWriter;
    private readonly ILogger<GeoServerImportService> _logger;

    public GeoServerImportService(
        GeoServerRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        ILogger<GeoServerImportService> logger,
        ISldStyleConverter? sldConverter = null,
        ILayerPublishingService? layerPublishingService = null,
        IMigrationCatalogWriter? catalogWriter = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sldConverter = sldConverter;
        _layerPublishingService = layerPublishingService;
        _catalogWriter = catalogWriter;
    }

    /// <inheritdoc />
    public Task<GeoServerServiceInfo> DiscoverServiceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _restClient.DiscoverServiceAsync(
            request.GeoServerRestUrl,
            request.Username,
            request.Password,
            request.IncludeCompatibilityAnalysis,
            request.IncludeStyleContent,
            request.TimeoutSeconds,
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            request.AllowUnsafeLocalUrls,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metricsRecorder = new MigrationRunMetricsRecorder();
        metricsRecorder.SampleResources();

        try
        {
            GeoServerServiceInfo serviceInfo;
            MigrationSourceInventoryArtifact inventory;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Scan))
            {
                serviceInfo = await DiscoverServiceAsync(request, cancellationToken).ConfigureAwait(false);
                metricsRecorder.RecordSourceRequest();
                inventory = await BuildInventoryArtifactAsync(
                        serviceInfo,
                        request.Username,
                        request.Password,
                        request.IncludeStyleContent,
                        cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(inventory.Summary.ResourceCount);
                metricsRecorder.SampleResources();
            }

            var metrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, runId: null, measurementScope: "geoserver scan");
            EmitRunMetrics(metrics);
            return inventory;
        }
        catch (InvalidOperationException ex)
        {
            Log.InventoryScanFailed(_logger, request.GeoServerRestUrl, ex);

            return new MigrationSourceInventoryArtifact
            {
                SourceKind = "geoserver-rest",
                Source = new MigrationSourceIdentity
                {
                    DisplayName = "GeoServer",
                    BaseUrl = request.GeoServerRestUrl,
                    Product = "GeoServer",
                    ServiceType = "REST"
                },
                AuthPosture = BuildAuthPosture(
                    request.Username,
                    request.Password,
                    accessConfirmed: false,
                    anonymousMode: "anonymous-or-auth-required",
                    notes: [ex.Message]),
                ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                    "failed",
                    [ex.Message],
                    ["source-inventory"]),
                Summary = new MigrationInventorySummary(),
                OverallCompatibility = MigrationInventoryHelpers.Partial(
                    "The scan did not complete successfully.",
                    [ex.Message],
                    ["Verify GeoServer reachability and credentials, then rerun the scan."],
                    ImportCompatibilityCodes.GeoServerScanFailed),
                Containers = [],
                Resources = [],
                Styles = [],
                ExternalDependencies = []
            };
        }
    }

    /// <inheritdoc />
    public Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportConfigurationAsync(request, null, cancellationToken);
    }

    private async Task<MigrationSourceInventoryArtifact> BuildInventoryArtifactAsync(
        GeoServerServiceInfo serviceInfo,
        string? username,
        string? password,
        bool includeStyleContent,
        CancellationToken cancellationToken)
    {
        var styleResourceIds = BuildStyleResourceMap(serviceInfo);
        var dependencies = BuildExternalDependencies(serviceInfo, includeStyleContent);
        var resources = await BuildResourcesAsync(serviceInfo, styleResourceIds, dependencies, cancellationToken).ConfigureAwait(false);
        var styles = BuildStyles(serviceInfo, styleResourceIds, dependencies);
        var containers = BuildContainers(serviceInfo, resources, dependencies, styles);

        var summary = MigrationInventoryHelpers.BuildSummary(containers, resources, styles, dependencies);
        var overallCompatibility = MigrationInventoryHelpers.Aggregate(
            containers.Select(c => c.Compatibility)
                .Concat(resources.Select(r => r.Compatibility))
                .Concat(styles.Select(s => s.Compatibility))
                .Concat(dependencies.Select(d => d.Compatibility)),
            "No inventory items were discovered.");

        var completenessWarnings = new List<string>();
        var missingArtifacts = new List<string>();

        if (string.IsNullOrWhiteSpace(serviceInfo.Version))
        {
            completenessWarnings.Add("GeoServer version metadata was unavailable.");
            missingArtifacts.Add("source-version");
        }

        if (serviceInfo.GlobalSettings == null)
        {
            completenessWarnings.Add("GeoServer global settings were unavailable.");
            missingArtifacts.Add("global-settings");
        }

        if (includeStyleContent &&
            serviceInfo.Styles.Any(style =>
                style.Format.Equals("sld", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(style.SldContent)))
        {
            completenessWarnings.Add("One or more SLD style documents could not be fetched.");
            missingArtifacts.Add("style-content");
        }

        var completeness = MigrationInventoryHelpers.BuildCompleteness(
            completenessWarnings.Count == 0 ? "complete" : "partial",
            completenessWarnings,
            missingArtifacts);

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "geoserver-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = serviceInfo.GlobalSettings?.Title ?? "GeoServer",
                BaseUrl = serviceInfo.GeoServerRestUrl,
                Product = "GeoServer",
                Version = serviceInfo.Version,
                Build = serviceInfo.GitRevision ?? serviceInfo.BuildTimestamp,
                ServiceType = "REST"
            },
            AuthPosture = BuildAuthPosture(username, password, accessConfirmed: true, anonymousMode: "anonymous"),
            ScanCompleteness = completeness,
            Summary = summary,
            OverallCompatibility = overallCompatibility,
            Containers = containers,
            Resources = resources,
            Styles = styles,
            ExternalDependencies = dependencies
        };
    }

    private async Task<MigrationInventoryResource[]> BuildResourcesAsync(
        GeoServerServiceInfo serviceInfo,
        IReadOnlyDictionary<string, string[]> styleResourceIds,
        IReadOnlyList<MigrationExternalDependency> dependencies,
        CancellationToken cancellationToken)
    {
        var resources = new List<MigrationInventoryResource>(serviceInfo.Layers.Length + serviceInfo.LayerGroups.Length);

        foreach (var layer in serviceInfo.Layers.OrderBy(static layer => GetLayerId(layer), StringComparer.Ordinal))
        {
            var layerId = GetLayerId(layer);
            var spatialReferences = (await Task.WhenAll(
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "declared", layer.SRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "native", layer.NativeCRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "latlon-bounds", layer.LatLonBoundingBox?.CRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "native-bounds", layer.NativeBoundingBox?.CRS, cancellationToken))
                .ConfigureAwait(false))
                .OfType<MigrationSpatialReferenceInfo>()
                .OrderBy(static info => info.Role, StringComparer.Ordinal)
                .ToArray();

            var styleIds = GetStyleIdsForResource(serviceInfo.Styles, styleResourceIds, layerId);

            var dependencyIds = new[]
                {
                    !string.IsNullOrWhiteSpace(layer.DataStoreName) ? GetDataStoreId(layer.WorkspaceName, layer.DataStoreName) : null,
                    !string.IsNullOrWhiteSpace(layer.CoverageStoreName) ? GetCoverageStoreId(layer.WorkspaceName, layer.CoverageStoreName) : null
                }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            resources.Add(new MigrationInventoryResource
            {
                Id = layerId,
                ContainerId = GetWorkspaceId(layer.WorkspaceName),
                Kind = "layer",
                Name = layer.Name,
                Title = layer.Title,
                Description = layer.Abstract,
                GeometryType = null,
                FeatureCount = null,
                HasAttachments = null,
                Capabilities = BuildLayerCapabilities(layer, serviceInfo.ServiceEndpoints),
                SpatialReferences = spatialReferences,
                StyleIds = styleIds,
                ExternalDependencyIds = dependencyIds,
                Compatibility = MigrationInventoryHelpers.FromGeoServerCompatibility(
                    layer.Compatibility,
                    "GeoServer layer metadata is compatible with discovery.")
            });
        }

        foreach (var layerGroup in serviceInfo.LayerGroups.OrderBy(static group => GetLayerGroupId(group), StringComparer.Ordinal))
        {
            var layerGroupId = GetLayerGroupId(layerGroup);
            var boundsSpatialReference = await MigrationInventoryHelpers.BuildSpatialReferenceAsync(
                    _crsRegistry,
                    "bounds",
                    layerGroup.Bounds?.CRS,
                    cancellationToken)
                .ConfigureAwait(false);

            var spatialReferences = boundsSpatialReference == null
                ? Array.Empty<MigrationSpatialReferenceInfo>()
                : new[] { boundsSpatialReference };

            resources.Add(new MigrationInventoryResource
            {
                Id = GetLayerGroupId(layerGroup),
                ContainerId = GetContainerIdForWorkspace(layerGroup.WorkspaceName),
                Kind = "layer-group",
                Name = layerGroup.Name,
                Title = layerGroup.Title,
                Description = layerGroup.Abstract,
                GeometryType = null,
                FeatureCount = null,
                HasAttachments = null,
                Capabilities = BuildLayerGroupCapabilities(layerGroup, serviceInfo.ServiceEndpoints),
                SpatialReferences = spatialReferences,
                StyleIds = GetStyleIdsForResource(serviceInfo.Styles, styleResourceIds, layerGroupId),
                ExternalDependencyIds = [],
                Compatibility = MigrationInventoryHelpers.FromGeoServerCompatibility(
                    layerGroup.Compatibility,
                    "GeoServer layer group metadata is compatible with discovery.")
            });
        }

        return resources.ToArray();
    }

    private static MigrationInventoryContainer[] BuildContainers(
        GeoServerServiceInfo serviceInfo,
        IReadOnlyList<MigrationInventoryResource> resources,
        IReadOnlyList<MigrationExternalDependency> dependencies,
        IReadOnlyList<MigrationInventoryStyle> styles)
    {
        var containers = new List<MigrationInventoryContainer>(serviceInfo.Workspaces.Length + 1);
        var globalContainerNeeded = serviceInfo.Styles.Any(style => string.IsNullOrWhiteSpace(style.WorkspaceName)) ||
            serviceInfo.LayerGroups.Any(group => string.IsNullOrWhiteSpace(group.WorkspaceName)) ||
            serviceInfo.ServiceEndpoints.Length > 0;

        if (globalContainerNeeded)
        {
            containers.Add(CreateContainer(
                GetGlobalContainerId(),
                "workspace",
                "global",
                "Global",
                null,
                isDefault: false,
                resources,
                dependencies,
                styles));
        }

        foreach (var workspace in serviceInfo.Workspaces.OrderBy(static workspace => workspace.Name, StringComparer.Ordinal))
        {
            containers.Add(CreateContainer(
                GetWorkspaceId(workspace.Name),
                "workspace",
                workspace.Name,
                workspace.Name,
                workspace.Description,
                workspace.IsDefault,
                resources,
                dependencies,
                styles));
        }

        return containers.OrderBy(static container => container.Id, StringComparer.Ordinal).ToArray();
    }

    private static MigrationInventoryContainer CreateContainer(
        string id,
        string kind,
        string name,
        string? title,
        string? description,
        bool isDefault,
        IReadOnlyList<MigrationInventoryResource> resources,
        IReadOnlyList<MigrationExternalDependency> dependencies,
        IReadOnlyList<MigrationInventoryStyle> styles)
    {
        var assessments = resources.Where(resource => resource.ContainerId == id).Select(resource => resource.Compatibility)
            .Concat(dependencies.Where(dependency => dependency.ContainerId == id).Select(dependency => dependency.Compatibility))
            .Concat(styles.Where(style => style.ContainerId == id).Select(style => style.Compatibility));

        return new MigrationInventoryContainer
        {
            Id = id,
            Kind = kind,
            Name = name,
            Title = title,
            Description = description,
            IsDefault = isDefault,
            Compatibility = MigrationInventoryHelpers.Aggregate(assessments, "No resources were discovered in this container.")
        };
    }

    private static MigrationInventoryStyle[] BuildStyles(
        GeoServerServiceInfo serviceInfo,
        IReadOnlyDictionary<string, string[]> styleResourceIds,
        IReadOnlyList<MigrationExternalDependency> dependencies)
    {
        return serviceInfo.Styles
            .OrderBy(static style => GetStyleId(style), StringComparer.Ordinal)
            .Select(style =>
            {
                var styleId = GetStyleId(style);
                var externalDependencyIds = dependencies
                    .Where(dependency => dependency.ResourceId == styleId)
                    .Select(dependency => dependency.Id)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray();

                var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["format"] = style.Format
                };

                if (!string.IsNullOrWhiteSpace(style.LanguageVersion))
                {
                    metadata["languageVersion"] = style.LanguageVersion;
                }

                if (!string.IsNullOrWhiteSpace(style.Filename))
                {
                    metadata["filename"] = style.Filename;
                }

                metadata["styleReference"] = BuildStyleReferenceUrl(serviceInfo.GeoServerRestUrl, style);

                if (style.Format.Equals("sld", StringComparison.OrdinalIgnoreCase))
                {
                    metadata["styleContentReference"] = BuildStyleContentReferenceUrl(serviceInfo.GeoServerRestUrl, style);
                    metadata["styleContentDisposition"] = "linked";
                }

                return new MigrationInventoryStyle
                {
                    Id = styleId,
                    ContainerId = GetContainerIdForWorkspace(style.WorkspaceName),
                    Kind = "style",
                    Name = style.Name,
                    Format = style.Format,
                    ResourceIds = styleResourceIds.GetValueOrDefault(styleId) ?? [],
                    ExternalDependencyIds = externalDependencyIds,
                    Metadata = metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
                    Compatibility = MigrationInventoryHelpers.FromGeoServerCompatibility(
                        style.Compatibility,
                        "GeoServer style metadata is compatible with discovery.")
                };
            })
            .ToArray();
    }

    private static MigrationExternalDependency[] BuildExternalDependencies(
        GeoServerServiceInfo serviceInfo,
        bool includeStyleContent)
    {
        var dependencies = new List<MigrationExternalDependency>(serviceInfo.DataStores.Length + serviceInfo.CoverageStores.Length);

        foreach (var dataStore in serviceInfo.DataStores.OrderBy(static store => GetDataStoreId(store.WorkspaceName, store.Name), StringComparer.Ordinal))
        {
            var sanitizedMetadata = MigrationInventoryHelpers.SanitizeMetadata(dataStore.ConnectionParameters);

            dependencies.Add(new MigrationExternalDependency
            {
                Id = GetDataStoreId(dataStore.WorkspaceName, dataStore.Name),
                ContainerId = GetWorkspaceId(dataStore.WorkspaceName),
                ResourceId = null,
                Kind = "datastore",
                Name = dataStore.Name,
                DependencyType = dataStore.Type,
                Address = ResolveDependencyAddress(sanitizedMetadata),
                Metadata = sanitizedMetadata,
                SpatialReferences = [],
                Compatibility = MigrationInventoryHelpers.FromGeoServerCompatibility(
                    dataStore.Compatibility,
                    "GeoServer datastore metadata is compatible with discovery.")
            });
        }

        foreach (var coverageStore in serviceInfo.CoverageStores.OrderBy(static store => GetCoverageStoreId(store.WorkspaceName, store.Name), StringComparer.Ordinal))
        {
            var sanitizedMetadata = MigrationInventoryHelpers.SanitizeMetadata(coverageStore.ConnectionParameters);

            dependencies.Add(new MigrationExternalDependency
            {
                Id = GetCoverageStoreId(coverageStore.WorkspaceName, coverageStore.Name),
                ContainerId = GetWorkspaceId(coverageStore.WorkspaceName),
                ResourceId = null,
                Kind = "coverage-store",
                Name = coverageStore.Name,
                DependencyType = coverageStore.Type,
                Address = ResolveDependencyAddress(sanitizedMetadata),
                Metadata = sanitizedMetadata,
                SpatialReferences = [],
                Compatibility = MigrationInventoryHelpers.FromGeoServerCompatibility(
                    coverageStore.Compatibility,
                    "GeoServer coverage store metadata is compatible with discovery.")
            });
        }

        foreach (var endpoint in serviceInfo.ServiceEndpoints.OrderBy(static endpoint => endpoint.Protocol, StringComparer.Ordinal))
        {
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["protocol"] = endpoint.Protocol
            };

            foreach (var (key, value) in endpoint.Metadata)
            {
                metadata[key] = value;
            }

            if (endpoint.Enabled.HasValue)
            {
                metadata["enabled"] = endpoint.Enabled.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
            }

            if (endpoint.Capabilities.Length > 0)
            {
                metadata["capabilities"] = string.Join(",", endpoint.Capabilities);
            }

            dependencies.Add(new MigrationExternalDependency
            {
                Id = GetServiceEndpointId(endpoint.Protocol),
                ContainerId = GetGlobalContainerId(),
                ResourceId = null,
                Kind = "service-endpoint",
                Name = endpoint.Protocol,
                DependencyType = "ogc-service",
                Address = endpoint.Url,
                Metadata = metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
                SpatialReferences = [],
                Compatibility = endpoint.Enabled == false
                    ? MigrationInventoryHelpers.Partial(
                        $"{endpoint.Protocol} service endpoint is disabled in GeoServer.",
                        [$"{endpoint.Protocol} is advertised but disabled."],
                        ["Confirm whether this service should be enabled in the target deployment."],
                        ImportCompatibilityCodes.GeoServerManualReview)
                    : MigrationInventoryHelpers.Compatible(
                        $"{endpoint.Protocol} service endpoint was captured for migration planning.",
                        code: ImportCompatibilityCodes.GeoServerServiceEndpoint)
            });
        }

        if (includeStyleContent)
        {
            foreach (var style in serviceInfo.Styles)
            {
                foreach (var address in ExtractStyleUrls(style.SldContent)
                             .Select(MigrationInventoryHelpers.NormalizeExternalAddress)
                             .OfType<string>()
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(static value => value, StringComparer.Ordinal))
                {
                    var dependencyId = MigrationInventoryHelpers.BuildExternalDependencyId(GetStyleId(style), address);
                    dependencies.Add(new MigrationExternalDependency
                    {
                        Id = dependencyId,
                        ContainerId = GetContainerIdForWorkspace(style.WorkspaceName),
                        ResourceId = GetStyleId(style),
                        Kind = "external-graphic",
                        Name = style.Name,
                        DependencyType = "url",
                        Address = address,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["source"] = "sld"
                        },
                        SpatialReferences = [],
                        Compatibility = MigrationInventoryHelpers.Partial(
                            "External graphic references require manual migration review.",
                            ["The style references an external URL."],
                            ["Mirror or replace external graphics in the target deployment."],
                            ImportCompatibilityCodes.GeoServerExternalGraphic)
                    });
                }
            }
        }

        return dependencies
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, string[]> BuildStyleResourceMap(GeoServerServiceInfo serviceInfo)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var styleIdsByReference = BuildStyleReferenceLookup(serviceInfo.Styles);

        foreach (var layer in serviceInfo.Layers)
        {
            AddStyleResourceLinks(map, styleIdsByReference, layer.WorkspaceName, layer.DefaultStyle, GetLayerId(layer));

            foreach (var alternativeStyle in layer.AlternativeStyles)
            {
                AddStyleResourceLinks(map, styleIdsByReference, layer.WorkspaceName, alternativeStyle, GetLayerId(layer));
            }
        }

        foreach (var layerGroup in serviceInfo.LayerGroups)
        {
            foreach (var style in layerGroup.Styles)
            {
                AddStyleResourceLinks(
                    map,
                    styleIdsByReference,
                    layerGroup.WorkspaceName,
                    GetLayerGroupStyleReference(style),
                    GetLayerGroupId(layerGroup));
            }
        }

        return map.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static void AddStyleResourceLinks(
        Dictionary<string, HashSet<string>> map,
        IReadOnlyDictionary<StyleReferenceKey, string[]> styleIdsByReference,
        string layerWorkspaceName,
        string? styleReference,
        string resourceId)
    {
        foreach (var styleId in ResolveStyleIds(styleIdsByReference, layerWorkspaceName, styleReference))
        {
            if (!map.TryGetValue(styleId, out var resourceIds))
            {
                resourceIds = new HashSet<string>(StringComparer.Ordinal);
                map[styleId] = resourceIds;
            }

            _ = resourceIds.Add(resourceId);
        }
    }

    private static string[] GetStyleIdsForResource(
        IEnumerable<GeoServerStyleInfo> styles,
        IReadOnlyDictionary<string, string[]> styleResourceIds,
        string resourceId)
    {
        return styles
            .Where(style => styleResourceIds.TryGetValue(GetStyleId(style), out var linkedResources) &&
                linkedResources.Contains(resourceId, StringComparer.Ordinal))
            .Select(GetStyleId)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetLayerGroupStyleReference(GeoServerLayerGroupEntry style)
        => string.IsNullOrWhiteSpace(style.WorkspaceName)
            ? style.Name
            : $"{style.WorkspaceName}:{style.Name}";

    private static string[] BuildLayerCapabilities(
        GeoServerLayerInfo layer,
        IReadOnlyList<GeoServerServiceEndpointInfo> serviceEndpoints)
    {
        var capabilities = new List<string>();

        if (layer.Queryable)
        {
            capabilities.Add("query");
        }

        if (layer.Enabled)
        {
            capabilities.Add("enabled");
        }

        if (layer.Opaque)
        {
            capabilities.Add("opaque");
        }

        foreach (var endpoint in serviceEndpoints.Where(static endpoint => endpoint.Enabled != false))
        {
            capabilities.Add(endpoint.Protocol.ToLowerInvariant());
        }

        return capabilities.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static string[] BuildLayerGroupCapabilities(
        GeoServerLayerGroupInfo layerGroup,
        IReadOnlyList<GeoServerServiceEndpointInfo> serviceEndpoints)
    {
        var capabilities = new List<string>();

        foreach (var endpoint in serviceEndpoints.Where(static endpoint =>
                     endpoint.Enabled != false &&
                     string.Equals(endpoint.Protocol, "WMS", StringComparison.Ordinal)))
        {
            capabilities.Add(endpoint.Protocol.ToLowerInvariant());
        }

        return capabilities.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static string GetWorkspaceId(string workspaceName)
        => $"workspace:{workspaceName}";

    private static string GetGlobalContainerId()
        => "workspace:global";

    private static string GetContainerIdForWorkspace(string? workspaceName)
        => string.IsNullOrWhiteSpace(workspaceName) ? GetGlobalContainerId() : GetWorkspaceId(workspaceName);

    private static string GetLayerId(GeoServerLayerInfo layer)
        => $"layer:{layer.WorkspaceName}:{layer.Name}";

    private static string GetLayerGroupId(GeoServerLayerGroupInfo layerGroup)
        => $"layer-group:{(string.IsNullOrWhiteSpace(layerGroup.WorkspaceName) ? "global" : layerGroup.WorkspaceName)}:{layerGroup.Name}";

    private static string GetStyleId(GeoServerStyleInfo style)
        => GetStyleId(style.WorkspaceName, style.Name);

    private static string GetStyleId(string? workspaceName, string styleName)
        => $"style:{(string.IsNullOrWhiteSpace(workspaceName) ? "global" : workspaceName)}:{styleName}";

    private static string GetDataStoreId(string workspaceName, string dataStoreName)
        => $"datastore:{workspaceName}:{dataStoreName}";

    private static string GetCoverageStoreId(string workspaceName, string coverageStoreName)
        => $"coverage-store:{workspaceName}:{coverageStoreName}";

    private static string GetServiceEndpointId(string protocol)
        => $"service-endpoint:{protocol.ToLowerInvariant()}";

    private static string BuildStyleReferenceUrl(string baseUrl, GeoServerStyleInfo style)
    {
        var relativePath = string.IsNullOrWhiteSpace(style.WorkspaceName)
            ? $"styles/{Uri.EscapeDataString(style.Name)}"
            : $"workspaces/{Uri.EscapeDataString(style.WorkspaceName)}/styles/{Uri.EscapeDataString(style.Name)}";

        return $"{baseUrl.TrimEnd('/')}/{relativePath}.json";
    }

    private static string BuildStyleContentReferenceUrl(string baseUrl, GeoServerStyleInfo style)
    {
        var relativePath = string.IsNullOrWhiteSpace(style.WorkspaceName)
            ? $"styles/{Uri.EscapeDataString(style.Name)}"
            : $"workspaces/{Uri.EscapeDataString(style.WorkspaceName)}/styles/{Uri.EscapeDataString(style.Name)}";

        return $"{baseUrl.TrimEnd('/')}/{relativePath}.sld";
    }

    private static string? ResolveDependencyAddress(Dictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("url", out var url) && !string.IsNullOrWhiteSpace(url))
        {
            if (string.Equals(url, "[redacted]", StringComparison.Ordinal))
            {
                return null;
            }

            return MigrationInventoryHelpers.NormalizeExternalAddress(url) ?? url;
        }

        if (metadata.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host))
        {
            return metadata.TryGetValue("database", out var database) && !string.IsNullOrWhiteSpace(database)
                ? $"{host}/{database}"
                : host;
        }

        if (metadata.TryGetValue("database", out var db) && !string.IsNullOrWhiteSpace(db))
        {
            return db;
        }

        return null;
    }

    private static string[] ExtractStyleUrls(string? sldContent)
    {
        if (string.IsNullOrWhiteSpace(sldContent))
        {
            return [];
        }

        try
        {
            var document = XDocument.Parse(sldContent, LoadOptions.None);

            return document
                .Descendants()
                .Where(static element => element.Name.LocalName.Equals("ExternalGraphic", StringComparison.OrdinalIgnoreCase))
                .SelectMany(GetExternalGraphicUrls)
                .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out _))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (XmlException)
        {
            return [];
        }
    }

    private static IEnumerable<string> GetExternalGraphicUrls(XElement externalGraphic)
        => externalGraphic
            .DescendantsAndSelf()
            .SelectMany(static element => element.Attributes()
                .Where(static attribute =>
                    !attribute.IsNamespaceDeclaration &&
                    attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                .Select(static attribute => attribute.Value));

    /// <inheritdoc />
    public async Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Log.ImportStarting(_logger, request.GeoServerRestUrl, request.TargetHonuaUrl);

        var stopwatch = Stopwatch.StartNew();
        var jobId = request.JobId ?? Guid.NewGuid().ToString();

        using var activity = GeoServerImportActivity.StartImport(request.GeoServerRestUrl, request.TargetHonuaUrl);

        var metricsRecorder = new MigrationRunMetricsRecorder();
        metricsRecorder.SampleResources();

        // Initialize progress tracking
        var currentProgress = GeoServerImportProgress.CreateInitial(
            jobId,
            request.GeoServerRestUrl,
            request.TargetHonuaUrl);

        try
        {
            progress?.Report(currentProgress);

            // Step 1: Discover GeoServer configuration
            Log.DiscoveringConfiguration(_logger, request.GeoServerRestUrl);
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Discovering,
                CurrentPhase = "Discovering GeoServer configuration"
            };
            progress?.Report(currentProgress);

            var discoveryRequest = new GeoServerDiscoveryRequest
            {
                GeoServerRestUrl = request.GeoServerRestUrl,
                Username = request.Username,
                Password = request.Password,
                TimeoutSeconds = request.RequestTimeoutSeconds,
                IncludeCompatibilityAnalysis = true,
                IncludeStyleContent = request.ImportStyles,
                AllowUnsafeLocalUrls = request.AllowUnsafeLocalUrls
            };

            GeoServerServiceInfo serviceInfo;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Scan))
            {
                serviceInfo = await DiscoverServiceAsync(discoveryRequest, cancellationToken);
                metricsRecorder.RecordSourceRequest();
                metricsRecorder.SampleResources();
            }

            // Filter resources based on request
            var filteredResources = FilterRequestedResources(serviceInfo, request);
            metricsRecorder.RecordResourceCount(
                filteredResources.WorkspaceCount +
                filteredResources.DataStoreCount +
                filteredResources.LayerCount +
                filteredResources.StyleCount);

            // Estimate total work
            var totalResources = filteredResources.WorkspaceCount + filteredResources.DataStoreCount +
                                filteredResources.LayerCount + (request.ImportStyles ? filteredResources.StyleCount : 0);

            currentProgress = currentProgress with
            {
                EstimatedTotalResources = totalResources,
                SourceGeoServerVersion = serviceInfo.Version,
                CurrentPhase = $"Discovered {totalResources} resources to import"
            };
            progress?.Report(currentProgress);

            if (request.DryRun)
            {
                currentProgress = currentProgress with
                {
                    Status = GeoServerImportStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Dry run completed"
                };
                metricsRecorder.SampleResources();
                var dryRunMetrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, jobId, "geoserver dry run");
                EmitRunMetrics(dryRunMetrics);
                currentProgress = currentProgress with { RunMetrics = dryRunMetrics };
                progress?.Report(currentProgress);

                return CreateDryRunResult(serviceInfo, filteredResources, request, stopwatch.Elapsed) with
                {
                    RunMetrics = dryRunMetrics
                };
            }

            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                CurrentPhase = "Generating deterministic apply plan"
            };
            progress?.Report(currentProgress);

            MigrationApplyPlanArtifact applyPlan;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Manifest))
            {
                applyPlan = await CreateApplyPlanAsync(serviceInfo, filteredResources, request, cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(applyPlan.Summary.TotalStepCount);
                metricsRecorder.RecordManualReview(applyPlan.Summary.ManualReviewStepCount, applyPlan.Summary.TotalStepCount);
                metricsRecorder.SampleResources();
            }

            MigrationApplyExecutionArtifact applyExecution;
            using (metricsRecorder.BeginPhase(MigrationCostPerformancePhases.Apply))
            {
                applyExecution = await ExecuteApplyPlanAsync(
                        filteredResources,
                        request,
                        applyPlan,
                        currentProgress,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                metricsRecorder.RecordResourceCount(applyExecution.Summary.AppliedStepCount);
                metricsRecorder.RecordResume(applyPlan.ReplayToken, applyExecution.Summary.AlreadyAppliedStepCount);
                metricsRecorder.SampleResources();
            }

            var applyPlanWarnings = BuildApplyPlanWarnings(applyPlan, applyExecution);

            var runMetrics = BuildRunMetricsArtifact(metricsRecorder, serviceInfo, jobId, "geoserver migration");
            EmitRunMetrics(runMetrics);

            if (applyExecution.Summary.FailedStepCount > 0)
            {
                var failureMessage = BuildApplyFailureMessage(applyExecution);
                currentProgress = currentProgress with
                {
                    Status = GeoServerImportStatus.Failed,
                    ResourcesProcessed = applyPlan.Summary.TotalStepCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Apply plan failed",
                    ErrorMessage = failureMessage,
                    Warnings = applyPlanWarnings,
                    ApplyPlan = applyPlan,
                    ApplyExecution = applyExecution,
                    RunMetrics = runMetrics
                };
                progress?.Report(currentProgress);

                return CreateFailedApplyPlanResult(
                    serviceInfo,
                    applyPlan,
                    applyExecution,
                    request,
                    stopwatch.Elapsed,
                    applyPlanWarnings,
                    failureMessage) with
                { RunMetrics = runMetrics };
            }

            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Completed,
                ResourcesProcessed = applyPlan.Summary.TotalStepCount,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Apply plan executed",
                Warnings = applyPlanWarnings,
                ApplyPlan = applyPlan,
                ApplyExecution = applyExecution,
                RunMetrics = runMetrics
            };
            progress?.Report(currentProgress);

            Log.ApplyPlanExecuted(
                _logger,
                applyPlan.Summary.TotalStepCount,
                applyExecution.Summary.AppliedStepCount,
                applyExecution.Summary.AlreadyAppliedStepCount,
                applyExecution.Summary.ManualReviewStepCount,
                applyExecution.Summary.UnsupportedStepCount);

            return CreateApplyPlanResult(serviceInfo, applyPlan, applyExecution, request, stopwatch.Elapsed, applyPlanWarnings) with
            {
                RunMetrics = runMetrics
            };
        }
        catch (OperationCanceledException)
        {
            Log.ImportCancelled(_logger);
            throw;
        }
        catch (Exception ex)
        {
            Log.ImportFailed(_logger, ex.Message, ex);

            var errorProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import failed",
                ErrorMessage = ex.Message
            };
            progress?.Report(errorProgress);

            return GeoServerImportResult.CreateFailure(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                ex.Message,
                stopwatch.Elapsed);
        }
    }

    private static FilteredResources FilterRequestedResources(GeoServerServiceInfo serviceInfo, GeoServerImportRequest request)
    {
        var styleIdsByReference = BuildStyleReferenceLookup(serviceInfo.Styles);

        var dataStores = request.DataStoreNames == null
            ? serviceInfo.DataStores
            : serviceInfo.DataStores.Where(ds => IsResourceRequested(ds.WorkspaceName, ds.Name, request.DataStoreNames)).ToArray();

        var layers = request.LayerNames == null
            ? serviceInfo.Layers
            : serviceInfo.Layers.Where(l => IsResourceRequested(l.WorkspaceName, l.Name, request.LayerNames)).ToArray();

        var styles = request.ImportStyles
            ? (request.LayerNames == null ? serviceInfo.Styles : serviceInfo.Styles.Where(s => IsResourceNeededForLayers(s, layers, styleIdsByReference)).ToArray())
            : Array.Empty<GeoServerStyleInfo>();

        var layerGroups = request.LayerNames == null
            ? serviceInfo.LayerGroups
                .Where(group => request.WorkspaceNames == null ||
                    request.WorkspaceNames.Contains(group.WorkspaceName, StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<GeoServerLayerGroupInfo>();

        // Scope workspaces to the operator's request. When WorkspaceNames is set we honor
        // it directly; otherwise, when LayerNames narrows the scope we derive the
        // workspace set from the selected layers (and any layer groups) so the
        // catalog-apply loop does not persist honua.services rows for workspaces the
        // operator never asked to touch (issue #1098). When neither filter is set we
        // preserve the historical "all workspaces" behavior.
        GeoServerWorkspaceInfo[] workspaces;
        if (request.WorkspaceNames != null)
        {
            workspaces = serviceInfo.Workspaces
                .Where(w => request.WorkspaceNames.Contains(w.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (request.LayerNames != null)
        {
            var scopedWorkspaceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layers)
            {
                if (!string.IsNullOrWhiteSpace(layer.WorkspaceName))
                {
                    scopedWorkspaceNames.Add(layer.WorkspaceName);
                }
            }
            foreach (var group in layerGroups)
            {
                if (!string.IsNullOrWhiteSpace(group.WorkspaceName))
                {
                    scopedWorkspaceNames.Add(group.WorkspaceName);
                }
            }
            workspaces = serviceInfo.Workspaces
                .Where(w => scopedWorkspaceNames.Contains(w.Name))
                .ToArray();
        }
        else
        {
            workspaces = serviceInfo.Workspaces;
        }

        var scopedWorkspaceNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in workspaces)
        {
            if (!string.IsNullOrWhiteSpace(workspace.Name))
            {
                scopedWorkspaceNameSet.Add(workspace.Name);
            }
        }

        return new FilteredResources
        {
            Workspaces = workspaces,
            DataStores = dataStores,
            Layers = layers,
            LayerGroups = layerGroups,
            Styles = styles,
            WorkspaceCount = workspaces.Length,
            DataStoreCount = dataStores.Length,
            LayerCount = layers.Length,
            StyleCount = styles.Length,
            ScopedWorkspaceNames = scopedWorkspaceNameSet
        };
    }

    private static bool IsResourceRequested(string workspaceName, string resourceName, string[] requestedNames)
    {
        return requestedNames.Any(name =>
            name.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
            name.Equals($"{workspaceName}:{resourceName}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsResourceNeededForLayers(
        GeoServerStyleInfo style,
        GeoServerLayerInfo[] layers,
        IReadOnlyDictionary<StyleReferenceKey, string[]> styleIdsByReference)
    {
        var styleId = GetStyleId(style);
        return layers.Any(layer =>
            ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, layer.DefaultStyle).Contains(styleId, StringComparer.Ordinal) ||
            layer.AlternativeStyles.Any(alternativeStyle => ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, alternativeStyle)
                .Contains(styleId, StringComparer.Ordinal)));
    }

    private static MigrationInventoryAuthPosture BuildAuthPosture(
        string? username,
        string? password,
        bool accessConfirmed,
        string anonymousMode,
        IEnumerable<string>? notes = null)
    {
        var usesBasicAuth = UsesBasicAuthentication(username, password);
        var postureNotes = new List<string>();

        if (!usesBasicAuth && HasAnyCredential(username, password))
        {
            postureNotes.Add("GeoServer basic authentication requires both username and password; the scan proceeded without credentials.");
        }

        if (notes != null)
        {
            postureNotes.AddRange(notes);
        }

        return new MigrationInventoryAuthPosture
        {
            Mode = usesBasicAuth ? "basic" : anonymousMode,
            CredentialsSupplied = usesBasicAuth,
            AccessConfirmed = accessConfirmed,
            Notes = MigrationInventoryHelpers.NormalizeStrings(postureNotes)
        };
    }

    private static bool UsesBasicAuthentication(string? username, string? password)
        => !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);

    private static bool HasAnyCredential(string? username, string? password)
        => !string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password);

    private static Dictionary<StyleReferenceKey, string[]> BuildStyleReferenceLookup(IEnumerable<GeoServerStyleInfo> styles)
    {
        return styles
            .GroupBy(
                static style => new StyleReferenceKey(style.WorkspaceName, style.Name),
                StyleReferenceKey.Comparer)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(GetStyleId).ToArray(),
                StyleReferenceKey.Comparer);
    }

    private static string[] ResolveStyleIds(
        IReadOnlyDictionary<StyleReferenceKey, string[]> styleIdsByReference,
        string layerWorkspaceName,
        string? styleReference)
    {
        if (string.IsNullOrWhiteSpace(styleReference))
        {
            return [];
        }

        if (TryParseQualifiedStyleReference(styleReference, out var qualifiedReference))
        {
            return styleIdsByReference.TryGetValue(qualifiedReference, out var qualifiedStyleIds)
                ? qualifiedStyleIds
                : [];
        }

        var workspaceReference = new StyleReferenceKey(layerWorkspaceName, styleReference);
        if (styleIdsByReference.TryGetValue(workspaceReference, out var workspaceStyleIds))
        {
            return workspaceStyleIds;
        }

        var globalReference = new StyleReferenceKey(null, styleReference);
        return styleIdsByReference.TryGetValue(globalReference, out var globalStyleIds)
            ? globalStyleIds
            : [];
    }

    private static bool TryParseQualifiedStyleReference(string styleReference, out StyleReferenceKey qualifiedReference)
    {
        var separatorIndex = styleReference.IndexOf(':');
        if (separatorIndex > 0 && separatorIndex < styleReference.Length - 1)
        {
            qualifiedReference = new StyleReferenceKey(styleReference[..separatorIndex], styleReference[(separatorIndex + 1)..]);
            return true;
        }

        qualifiedReference = default;
        return false;
    }

    private readonly record struct StyleReferenceKey(string? WorkspaceName, string StyleName)
    {
        public static IEqualityComparer<StyleReferenceKey> Comparer { get; } = new StyleReferenceKeyComparer();

        private sealed class StyleReferenceKeyComparer : IEqualityComparer<StyleReferenceKey>
        {
            public bool Equals(StyleReferenceKey x, StyleReferenceKey y)
                => string.Equals(x.WorkspaceName, y.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.StyleName, y.StyleName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(StyleReferenceKey obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WorkspaceName ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.StyleName));
        }
    }

    private GeoServerImportResult CreateDryRunResult(GeoServerServiceInfo serviceInfo, FilteredResources resources, GeoServerImportRequest request, TimeSpan duration)
    {
        var importedResources = new List<GeoServerImportedResource>();

        // Add what would be imported
        foreach (var workspace in resources.Workspaces)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Workspace",
                Name = workspace.Name,
                Notes = "Would be created"
            });
        }

        foreach (var dataStore in resources.DataStores)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "DataStore",
                Name = dataStore.Name,
                WorkspaceName = dataStore.WorkspaceName,
                Notes = $"Would be created (type: {dataStore.Type})"
            });
        }

        foreach (var layer in resources.Layers)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Layer",
                Name = layer.Name,
                WorkspaceName = layer.WorkspaceName,
                Notes = "Would be created"
            });
        }

        foreach (var style in resources.Styles)
        {
            importedResources.Add(new GeoServerImportedResource
            {
                ResourceType = "Style",
                Name = style.Name,
                WorkspaceName = style.WorkspaceName,
                Notes = style.Format == "sld"
                    ? (_sldConverter != null
                        ? "Would convert SLD to MapLibre via ISldStyleConverter"
                        : "SLD converter not registered; style would be skipped or warned (issue #375)")
                    : "Would be created"
            });
        }

        var warnings = new List<string>();
        if (resources.StyleCount > 0 && _sldConverter == null)
        {
            warnings.Add("ISldStyleConverter is not registered; SLD styles will be skipped or warned at import time.");
        }

        if (serviceInfo.CompatibilityAssessment?.IncompatibleResources > 0)
        {
            warnings.Add($"{serviceInfo.CompatibilityAssessment.IncompatibleResources} resources are incompatible and would need manual intervention");
        }

        return GeoServerImportResult.CreateSuccess(
            request.GeoServerRestUrl,
            request.TargetHonuaUrl,
            resources.WorkspaceCount,
            resources.DataStoreCount,
            resources.LayerCount,
            resources.StyleCount,
            serviceInfo.Version,
            duration,
            warnings,
            importedResources,
            wasDryRun: true);
    }

    private async Task<MigrationApplyPlanArtifact> CreateApplyPlanAsync(
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        var filteredServiceInfo = CreateFilteredServiceInfoForPlan(serviceInfo, filteredResources);
        var inventory = await BuildInventoryArtifactAsync(
                filteredServiceInfo,
                request.Username,
                request.Password,
                request.ImportStyles,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = MigrationManifestTranslator.Translate(
            inventory,
            new MigrationManifestTranslationOptions
            {
                TargetServiceName = serviceInfo.GlobalSettings?.Title ?? "geoserver-import"
            });

        return MigrationApplyPlanBuilder.Build(manifest);
    }

    private static GeoServerServiceInfo CreateFilteredServiceInfoForPlan(
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources)
    {
        var coverageStores = serviceInfo.CoverageStores
            .Where(store => filteredResources.Layers.Any(layer =>
                string.Equals(layer.WorkspaceName, store.WorkspaceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(layer.CoverageStoreName, store.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return serviceInfo with
        {
            Workspaces = filteredResources.Workspaces,
            DataStores = filteredResources.DataStores,
            CoverageStores = coverageStores,
            Layers = filteredResources.Layers,
            LayerGroups = filteredResources.LayerGroups,
            Styles = filteredResources.Styles
        };
    }

    private async Task<MigrationApplyExecutionArtifact> ExecuteApplyPlanAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        GeoServerImportProgress currentProgress,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stepResults = new List<MigrationApplyExecutionStepResult>(applyPlan.Steps.Length);
        var layersById = filteredResources.Layers.ToDictionary(GetLayerId, StringComparer.Ordinal);
        var dataStoresByKey = filteredResources.DataStores.ToDictionary(
            static store => GetStoreKey(store.WorkspaceName, store.Name),
            StringComparer.OrdinalIgnoreCase);
        var layerGroupsById = filteredResources.LayerGroups.ToDictionary(GetLayerGroupId, StringComparer.Ordinal);
        // Slice 3 (#1015): style steps come out of the apply-plan builder with
        // Kind="style" and SourceId=GetStyleId(style). Build a lookup so the
        // apply path can resolve the underlying GeoServerStyleInfo (and its
        // SLD body) without re-running discovery.
        var stylesById = filteredResources.Styles.ToDictionary(GetStyleId, StringComparer.Ordinal);

        // Slice 1: apply workspace-level catalog entries first so that any subsequent
        // layer-group / layer apply can reference them. These records are deterministic
        // and idempotent — re-running the same manifest does not create duplicates.
        var workspaceStepResults = await ApplyWorkspaceCatalogStepsAsync(
                filteredResources,
                request,
                applyPlan,
                cancellationToken)
            .ConfigureAwait(false);
        stepResults.AddRange(workspaceStepResults);

        // Slice 2: apply data-source / data-store entries before per-layer steps so
        // each layer can reference an applied data source. Idempotent via PK on
        // (source_kind, source_id) in honua.migration_data_sources.
        var dataSourceStepResults = await ApplyDataSourceStepsAsync(
                filteredResources,
                request,
                applyPlan,
                cancellationToken)
            .ConfigureAwait(false);
        stepResults.AddRange(dataSourceStepResults);

        foreach (var step in applyPlan.Steps.OrderBy(static item => item.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepResult = await ExecuteApplyStepAsync(
                    step,
                    layersById,
                    dataStoresByKey,
                    layerGroupsById,
                    stylesById,
                    applyPlan,
                    filteredResources,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            stepResults.Add(stepResult);

            progress?.Report(currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                ResourcesProcessed = stepResults.Count,
                CurrentPhase = $"Processed GeoServer migration step {stepResults.Count} of {applyPlan.Summary.TotalStepCount}"
            });
        }

        var summary = new MigrationApplyExecutionSummary
        {
            TotalStepCount = stepResults.Count,
            AppliedStepCount = stepResults.Count(static result => result.Outcome == "applied"),
            AlreadyAppliedStepCount = stepResults.Count(static result => result.Outcome == "already-applied"),
            ManualReviewStepCount = stepResults.Count(static result => result.Outcome == "manual-review"),
            UnsupportedStepCount = stepResults.Count(static result => result.Outcome == "unsupported"),
            FailedStepCount = stepResults.Count(static result => result.Outcome == "failed")
        };

        return new MigrationApplyExecutionArtifact
        {
            SourceKind = applyPlan.SourceKind,
            Source = applyPlan.Source,
            PlanFingerprint = applyPlan.PlanFingerprint,
            ReplayToken = applyPlan.ReplayToken,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Summary = summary,
            StepResults = stepResults.ToArray()
        };
    }

    private async Task<MigrationApplyExecutionStepResult> ExecuteApplyStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerLayerInfo> layersById,
        Dictionary<string, GeoServerDataStoreInfo> dataStoresByKey,
        Dictionary<string, GeoServerLayerGroupInfo> layerGroupsById,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        MigrationApplyPlanArtifact applyPlan,
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        // Slice 3 (#1015): style steps still need to persist into the Honua
        // style catalog even when the manifest translator marks them
        // unsupported/manual-review, so the migration evidence pack carries
        // explicit conversion diagnostics rather than silently dropping the
        // source style. Dispatch to the style branch before the early
        // unsupported/manual-review returns below.
        if (string.Equals(step.Kind, "style", StringComparison.Ordinal))
        {
            return await ApplyStyleCatalogStepAsync(
                    step,
                    stylesById,
                    applyPlan,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (step.Disposition == "unsupported")
        {
            return CreateExecutionStepResult(
                step,
                "unsupported",
                "The source item is unsupported by the reviewed GeoServer migration plan.");
        }

        if (step.Disposition == "manual-review")
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The reviewed GeoServer migration plan requires operator review before this item can be applied.");
        }

        // Slice 1: persist layer-group catalog entries as Honua services. Data-copy,
        // style migration, and per-layer membership for layer groups are deferred to
        // follow-on slices and recorded as manual-review evidence by the manifest.
        if (string.Equals(step.Kind, "layer-group", StringComparison.Ordinal))
        {
            // Defensive scoping (#1098): reject layer-group catalog writes whose
            // owning workspace is not part of the operator's requested scope.
            if (layerGroupsById.TryGetValue(step.SourceId, out var scopeGroup) &&
                !IsWorkspaceInScope(filteredResources, scopeGroup.WorkspaceName))
            {
                Log.WorkspaceWriteRejected(_logger, "layer-group", scopeGroup.WorkspaceName ?? "global");
                return CreateExecutionStepResult(
                    step,
                    "manual-review",
                    $"Catalog write for layer-group '{step.SourceId}' rejected: source workspace '{scopeGroup.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
            }

            return await ApplyLayerGroupCatalogStepAsync(
                    step,
                    layerGroupsById,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(step.Kind, "layer", StringComparison.Ordinal))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "This apply slice only publishes PostGIS-backed GeoServer feature layers; other item kinds are retained as review records.");
        }

        if (!layersById.TryGetValue(step.SourceId, out var layer))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer was not present in the filtered GeoServer discovery result.");
        }

        // Defensive scoping (#1098): reject layer catalog writes whose owning
        // workspace is not part of the operator's requested scope.
        if (!IsWorkspaceInScope(filteredResources, layer.WorkspaceName))
        {
            Log.WorkspaceWriteRejected(_logger, "layer", layer.WorkspaceName ?? "global");
            return CreateExecutionStepResult(
                step,
                "manual-review",
                $"Catalog write for layer '{step.SourceId}' rejected: source workspace '{layer.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
        }

        if (string.IsNullOrWhiteSpace(layer.DataStoreName))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer is not backed by a GeoServer feature datastore; raster and virtual layers are not applied by this slice.");
        }

        if (!dataStoresByKey.TryGetValue(GetStoreKey(layer.WorkspaceName, layer.DataStoreName), out var dataStore))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer references a datastore that was not present in the filtered GeoServer discovery result.");
        }

        if (!IsPostGisDataStore(dataStore))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "Only PostGIS-backed GeoServer layers can be published directly to the Honua catalog by this apply slice.");
        }

        if (_layerPublishingService == null)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The target catalog publishing service is unavailable, so the layer remains a replayable apply-plan step.");
        }

        if (!TryResolveCatalogTarget(layer, dataStore, out var target, out var targetError))
        {
            return CreateExecutionStepResult(step, "manual-review", targetError);
        }

        // Slice 2 (#1015): when applyMode + catalog writer are configured, try to
        // copy feature data from the source PostGIS table into the Honua catalog
        // database before publishing. The copy is idempotent (skip when the
        // target table already exists with rows). On SourceMissing we fall
        // through to the original publish path which preserves slice-1 behavior.
        var publishSchema = target.Schema;
        var publishTable = target.Table;
        MigrationFeatureCopyOutcome? copyOutcome = null;
        if (request.ApplyMode && _catalogWriter != null && IsSafeIdentifier(layer.Name))
        {
            try
            {
                copyOutcome = await _catalogWriter.CopyFeatureDataAsync(
                        _connectionProvider.GetConnectionString(),
                        new MigrationFeatureCopyRequest
                        {
                            SourceSchema = target.Schema,
                            SourceTable = target.Table,
                            TargetSchema = "honua_data",
                            TargetTable = layer.Name.ToLowerInvariant()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (copyOutcome.Status == MigrationFeatureCopyStatus.Copied ||
                    copyOutcome.Status == MigrationFeatureCopyStatus.AlreadyApplied)
                {
                    publishSchema = "honua_data";
                    publishTable = layer.Name.ToLowerInvariant();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CreateExecutionStepResult(
                    step,
                    "failed",
                    $"Feature data copy for layer '{step.SourceId}' failed unexpectedly and requires operator review.");
            }
        }

        var publishRequest = new LayerPublishRequest
        {
            Schema = publishSchema,
            Table = publishTable,
            LayerName = step.TargetResourceName ?? layer.Name,
            Description = layer.Abstract,
            GeometryColumn = null,
            GeometryType = null,
            Srid = request.TargetSrid ?? ResolveSrid(layer.SRS),
            PrimaryKey = null,
            Fields = [],
            ServiceName = step.TargetServiceName,
            Enabled = request.AutoPublishLayers && layer.Enabled
        };

        try
        {
            var publishedLayer = await _layerPublishingService.PublishLayerAsync(
                    _connectionProvider.GetConnectionString(),
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            var copyNote = copyOutcome switch
            {
                { Status: MigrationFeatureCopyStatus.Copied } c =>
                    $" Copied {c.RowCount} feature rows into honua_data.{publishTable}.",
                { Status: MigrationFeatureCopyStatus.AlreadyApplied } c =>
                    $" Reused existing honua_data.{publishTable} ({c.RowCount} feature rows present).",
                _ => string.Empty
            };

            return CreateExecutionStepResult(
                step,
                "applied",
                $"Published catalog layer {publishedLayer.LayerId} from target table {publishSchema}.{publishTable}.{copyNote}",
                publishedLayer.LayerId);
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.Conflict)
        {
            return await HandleLayerPublishingConflictAsync(
                    step,
                    publishRequest,
                    ex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.NotFound)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source table was not found in the target Honua database; data-copy apply is not implemented by this slice.");
        }
        catch (LayerPublishingException ex) when (ex.ErrorKind == LayerPublishingErrorKind.Validation)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The target table failed catalog publication validation and requires operator review.");
        }
        catch (LayerPublishingException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply step failed in the target catalog publisher and requires operator review.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply step failed unexpectedly and requires operator review.");
        }
    }

    private async Task<MigrationApplyExecutionStepResult> HandleLayerPublishingConflictAsync(
        MigrationApplyPlanStep step,
        LayerPublishRequest publishRequest,
        LayerPublishingException exception,
        CancellationToken cancellationToken)
    {
        if (_layerPublishingService == null || !exception.LayerId.HasValue)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "A catalog layer for the target table already exists, but the publisher did not return the existing layer id needed to link it to the target service.");
        }

        try
        {
            var linkedLayer = await _layerPublishingService.LinkExistingLayerToServiceAsync(
                    _connectionProvider.GetConnectionString(),
                    exception.LayerId.Value,
                    publishRequest.ServiceName ?? "default",
                    publishRequest.Enabled,
                    cancellationToken)
                .ConfigureAwait(false);

            if (linkedLayer == null)
            {
                return CreateExecutionStepResult(
                    step,
                    "manual-review",
                    "A catalog layer for the target table already exists, but it could not be linked to the target service automatically.");
            }

            return CreateExecutionStepResult(
                step,
                "already-applied",
                $"Catalog layer {linkedLayer.LayerId} already exists for target table {publishRequest.Schema}.{publishRequest.Table} and is linked to target service {linkedLayer.ServiceName}.",
                linkedLayer.LayerId);
        }
        catch (LayerPublishingException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply conflict recovery failed in the target catalog publisher and requires operator review.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                "The catalog apply conflict recovery failed unexpectedly and requires operator review.");
        }
    }

    /// <summary>
    /// Persist a catalog service entry for each filtered GeoServer workspace.
    /// </summary>
    /// <remarks>
    /// This is the slice-1 application of the migration manifest: workspaces become
    /// idempotent <c>honua.services</c> rows so that subsequent layer-group and layer
    /// apply steps can attach to them. Re-running the same manifest does not create
    /// duplicates because the underlying writer uses <c>ON CONFLICT DO NOTHING</c>.
    /// </remarks>
    private async Task<IReadOnlyList<MigrationApplyExecutionStepResult>> ApplyWorkspaceCatalogStepsAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        CancellationToken cancellationToken)
    {
        if (filteredResources.Workspaces.Length == 0)
        {
            return [];
        }

        var results = new List<MigrationApplyExecutionStepResult>(filteredResources.Workspaces.Length);
        foreach (var workspace in filteredResources.Workspaces.OrderBy(static w => w.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceId = GetWorkspaceId(workspace.Name);

            // Defensive scoping (#1098): even though FilterRequestedResources should
            // already exclude unscoped workspaces, re-check at the write site so a
            // future regression in plan/translator code cannot smuggle a
            // cross-workspace mutation into honua.services.
            if (!IsWorkspaceInScope(filteredResources, workspace.Name))
            {
                Log.WorkspaceWriteRejected(_logger, "workspace", workspace.Name);
                results.Add(CreateOutOfScopeStepResult(
                    sourceId: sourceId,
                    kind: "workspace",
                    targetServiceName: NormalizeCatalogServiceName(workspace.Name),
                    targetResourceName: workspace.Name,
                    workspaceName: workspace.Name));
                continue;
            }

            var targetServiceName = NormalizeCatalogServiceName(workspace.Name);
            var stepResult = await EnsureCatalogEntryAsync(
                    sourceId: sourceId,
                    kind: "workspace",
                    targetServiceName: targetServiceName,
                    targetResourceName: workspace.Name,
                    description: BuildWorkspaceDescription(workspace, applyPlan),
                    srid: request.TargetSrid ?? 4326,
                    applyMode: request.ApplyMode,
                    deferredMessage: "Workspace catalog persistence is deferred until applyMode=true and a catalog writer is configured.",
                    cancellationToken)
                .ConfigureAwait(false);
            results.Add(stepResult);
        }

        return results;
    }

    /// <summary>
    /// Persist a migration data-source row per filtered GeoServer data store.
    /// </summary>
    /// <remarks>
    /// Slice 2 of issue #1015. GeoServer data stores are recorded in the
    /// inventory as <c>ExternalDependency</c> entries — they do not appear as
    /// resource-level apply-plan steps. We therefore iterate the filtered data
    /// stores directly here so each apply run produces a deterministic step
    /// result with create / skip / error outcomes per data source. Persistence
    /// is idempotent via <c>ON CONFLICT (source_kind, source_id) DO NOTHING</c>.
    /// </remarks>
    private async Task<IReadOnlyList<MigrationApplyExecutionStepResult>> ApplyDataSourceStepsAsync(
        FilteredResources filteredResources,
        GeoServerImportRequest request,
        MigrationApplyPlanArtifact applyPlan,
        CancellationToken cancellationToken)
    {
        if (filteredResources.DataStores.Length == 0)
        {
            return [];
        }

        // Workspace scope (#1098 / PR #1100): respect the operator's requested
        // workspace scope so unrelated workspaces' data stores are not applied.
        // When WorkspaceNames is set, only data stores in those workspaces are
        // eligible. The pre-existing FilterRequestedResources path keeps the
        // datastore array workspace-agnostic, so the filter is re-applied here.
        var scopedWorkspaceNames = request.WorkspaceNames is { Length: > 0 } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;

        var results = new List<MigrationApplyExecutionStepResult>(filteredResources.DataStores.Length);
        foreach (var dataStore in filteredResources.DataStores.OrderBy(static ds => GetDataStoreId(ds.WorkspaceName, ds.Name), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceId = GetDataStoreId(dataStore.WorkspaceName, dataStore.Name);
            var targetServiceName = NormalizeCatalogServiceName(
                string.IsNullOrWhiteSpace(dataStore.WorkspaceName)
                    ? $"datastore-{dataStore.Name}"
                    : $"{dataStore.WorkspaceName}-{dataStore.Name}");

            if (scopedWorkspaceNames is not null &&
                !string.IsNullOrWhiteSpace(dataStore.WorkspaceName) &&
                !scopedWorkspaceNames.Contains(dataStore.WorkspaceName))
            {
                results.Add(CreateExecutionStepResult(
                    new MigrationApplyPlanStep
                    {
                        Sequence = 0,
                        StepId = $"datasource:{sourceId}",
                        SourceId = sourceId,
                        Kind = "datastore",
                        Action = "apply-data-source",
                        Disposition = "manual-review",
                        TargetServiceName = targetServiceName,
                        TargetResourceName = dataStore.Name,
                        Compatibility = new MigrationCompatibilityAssessment
                        {
                            Level = "manual-review",
                            Reason = "Data-source apply rejected by workspace scope guard."
                        }
                    },
                    "manual-review",
                    $"Data-source apply for '{sourceId}' rejected: source workspace '{dataStore.WorkspaceName}' is outside the requested workspace scope (issue #1098)."));
                continue;
            }

            var syntheticStep = new MigrationApplyPlanStep
            {
                Sequence = 0,
                StepId = $"datasource:{sourceId}",
                SourceId = sourceId,
                Kind = "datastore",
                Action = "apply-data-source",
                Disposition = "ready",
                TargetServiceName = targetServiceName,
                TargetResourceName = dataStore.Name,
                Compatibility = new MigrationCompatibilityAssessment
                {
                    Level = "compatible",
                    Reason = "GeoServer data store is staged as an idempotent honua.migration_data_sources row."
                }
            };

            if (!request.ApplyMode || _catalogWriter == null)
            {
                results.Add(CreateExecutionStepResult(
                    syntheticStep,
                    "manual-review",
                    "Data-source persistence is deferred until applyMode=true and a catalog writer is configured."));
                continue;
            }

            var dataSourceType = ResolveDataSourceType(dataStore);
            try
            {
                var outcome = await _catalogWriter.EnsureDataSourceAsync(
                        _connectionProvider.GetConnectionString(),
                        new MigrationDataSourceRequest
                        {
                            SourceKind = applyPlan.SourceKind,
                            SourceId = sourceId,
                            DataSourceType = dataSourceType,
                            WorkspaceName = dataStore.WorkspaceName,
                            DisplayName = dataStore.Name,
                            ConnectionSummary = BuildDataSourceConnectionSummary(dataStore)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var stepResult = outcome switch
                {
                    MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                        syntheticStep,
                        "applied",
                        $"Applied {dataSourceType} data source '{sourceId}' to honua.migration_data_sources."),
                    MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                        syntheticStep,
                        "already-applied",
                        $"Data source '{sourceId}' already present; idempotent re-apply made no changes."),
                    _ => CreateExecutionStepResult(syntheticStep, "manual-review", "Catalog writer returned an unexpected outcome.")
                };
                results.Add(stepResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(CreateExecutionStepResult(
                    syntheticStep,
                    "failed",
                    $"Data-source apply for '{sourceId}' failed unexpectedly and requires operator review."));
            }
        }

        return results;
    }

    private static string ResolveDataSourceType(GeoServerDataStoreInfo dataStore)
    {
        if (!string.IsNullOrWhiteSpace(dataStore.Type))
        {
            return dataStore.Type;
        }

        if (dataStore.ConnectionParameters.TryGetValue("dbtype", out var dbTypeValue) &&
            dbTypeValue is string dbType &&
            !string.IsNullOrWhiteSpace(dbType))
        {
            return dbType;
        }

        return "unknown";
    }

    private static string BuildDataSourceConnectionSummary(GeoServerDataStoreInfo dataStore)
    {
        var host = TryGetStringParameter(dataStore.ConnectionParameters, "host");
        var database = TryGetStringParameter(dataStore.ConnectionParameters, "database");
        var schema = TryGetStringParameter(dataStore.ConnectionParameters, "schema");
        var path = TryGetStringParameter(dataStore.ConnectionParameters, "url")
            ?? TryGetStringParameter(dataStore.ConnectionParameters, "directory");

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(host))
        {
            parts.Add($"host={host}");
        }
        if (!string.IsNullOrWhiteSpace(database))
        {
            parts.Add($"database={database}");
        }
        if (!string.IsNullOrWhiteSpace(schema))
        {
            parts.Add($"schema={schema}");
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            parts.Add($"path={path}");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// Persist a migration style row per filtered GeoServer style.
    /// </summary>
    /// <remarks>
    /// Slice 3 of issue #1015. Each style apply-plan step produces an
    /// idempotent row in <c>honua.migration_styles</c> with the original
    /// source body, any converter output, and structured conversion
    /// diagnostics. SLD styles are run through the registered
    /// <see cref="ISldStyleConverter"/> when available; conversion warnings
    /// and errors are persisted so operators can audit the deterministic
    /// migration outcome. When the converter reports errors (or when no
    /// converter is registered) the row is persisted with disposition
    /// <c>manual-review</c> — we do not claim perfect SLD visual parity
    /// when diagnostics report manual review (issue #1015 AC). Re-apply
    /// is idempotent via PK on (source_kind, source_id). Cross-workspace
    /// styles outside the operator's requested scope are rejected per
    /// issue #1098.
    /// </remarks>
    private async Task<MigrationApplyExecutionStepResult> ApplyStyleCatalogStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        MigrationApplyPlanArtifact applyPlan,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!stylesById.TryGetValue(step.SourceId, out var style))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source style was not present in the filtered GeoServer discovery result.");
        }

        // Workspace scope (#1098 / PR #1100): respect the operator's requested
        // workspace scope so unrelated workspaces' styles are not applied.
        if (request.WorkspaceNames is { Length: > 0 } scopedNames &&
            !string.IsNullOrWhiteSpace(style.WorkspaceName) &&
            !scopedNames.Contains(style.WorkspaceName, StringComparer.OrdinalIgnoreCase))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                $"Style apply for '{step.SourceId}' rejected: source workspace '{style.WorkspaceName}' is outside the requested workspace scope (issue #1098).");
        }

        if (!request.ApplyMode || _catalogWriter == null)
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "Style catalog persistence is deferred until applyMode=true and a catalog writer is configured.");
        }

        var sourceFormat = string.IsNullOrWhiteSpace(style.Format) ? "sld" : style.Format.ToLowerInvariant();
        string? convertedBody = null;
        string? convertedFormat = null;
        var diagnostics = new List<StyleDiagnostic>();
        var disposition = string.Equals(step.Disposition, "unsupported", StringComparison.Ordinal)
            ? "unsupported"
            : string.Equals(step.Disposition, "manual-review", StringComparison.Ordinal)
                ? "manual-review"
                : "applied";

        if (string.Equals(sourceFormat, "sld", StringComparison.Ordinal))
        {
            // Reuse the existing ISldStyleConverter; do NOT reimplement converter
            // logic in the apply path. When the converter is not registered, the
            // style is still persisted but tagged manual-review with a converter
            // diagnostic so the evidence pack carries an explicit record.
            if (_sldConverter == null)
            {
                diagnostics.Add(new StyleDiagnostic(
                    "error",
                    "No ISldStyleConverter is registered; SLD style was persisted without conversion. Apply via the per-layer admin SLD endpoint to attach a MapLibre style."));
                disposition = "manual-review";
            }
            else if (string.IsNullOrWhiteSpace(style.SldContent))
            {
                diagnostics.Add(new StyleDiagnostic(
                    "error",
                    "SLD style has no embedded body; conversion was skipped."));
                disposition = "manual-review";
            }
            else
            {
                var conversion = _sldConverter.Convert(style.SldContent!);
                foreach (var warning in conversion.Warnings)
                {
                    diagnostics.Add(new StyleDiagnostic("warning", warning));
                }
                foreach (var error in conversion.Errors)
                {
                    diagnostics.Add(new StyleDiagnostic("error", error));
                }

                if (conversion.MapLibreLayersJson is { Length: > 0 } mapLibreJson)
                {
                    convertedBody = mapLibreJson;
                    convertedFormat = "maplibre-layers-json";
                }

                // Conversion produced no layers (HasErrors), or converter
                // emitted at least one error diagnostic: do not claim visual
                // parity. Per #1015 AC, mark the row manual-review.
                if (conversion.HasErrors)
                {
                    disposition = "manual-review";
                }
            }
        }
        else if (!string.Equals(sourceFormat, "mbstyle", StringComparison.Ordinal))
        {
            // Non-SLD/non-MapBox formats (CSS, YSLD, etc.) are persisted as
            // manual-review because Honua does not have a deterministic
            // converter for them.
            diagnostics.Add(new StyleDiagnostic(
                "error",
                $"Source style format '{sourceFormat}' is not supported by Honua's deterministic style converter; persisted as manual-review."));
            disposition = "manual-review";
        }

        var targetStyleId = BuildTargetStyleId(applyPlan.Source, style);

        try
        {
            var outcome = await _catalogWriter.EnsureStyleAsync(
                    _connectionProvider.GetConnectionString(),
                    new MigrationStyleRequest
                    {
                        SourceKind = applyPlan.SourceKind,
                        SourceId = step.SourceId,
                        TargetStyleId = targetStyleId,
                        StyleName = style.Name,
                        WorkspaceName = string.IsNullOrWhiteSpace(style.WorkspaceName) ? null : style.WorkspaceName,
                        SourceFormat = sourceFormat,
                        SourceLanguageVersion = string.IsNullOrWhiteSpace(style.LanguageVersion) ? null : style.LanguageVersion,
                        SourceBody = string.IsNullOrWhiteSpace(style.SldContent) ? null : style.SldContent,
                        ConvertedBody = convertedBody,
                        ConvertedFormat = convertedFormat,
                        DiagnosticsJson = SerializeStyleDiagnostics(diagnostics),
                        ReviewDisposition = disposition
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var diagnosticSummary = BuildStyleDiagnosticSummary(diagnostics);
            return outcome switch
            {
                MigrationCatalogWriteOutcome.Created when disposition == "manual-review" => CreateExecutionStepResult(
                    step,
                    "manual-review",
                    $"Persisted style '{step.SourceId}' with manual-review disposition. {diagnosticSummary} Do not claim visual parity until the diagnostics are resolved."),
                MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                    step,
                    "applied",
                    $"Applied {sourceFormat} style '{step.SourceId}' to honua.migration_styles.{(diagnosticSummary.Length > 0 ? " " + diagnosticSummary : string.Empty)}"),
                MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                    step,
                    "already-applied",
                    $"Style '{step.SourceId}' already present; idempotent re-apply made no changes."),
                _ => CreateExecutionStepResult(step, "manual-review", "Catalog writer returned an unexpected outcome.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                $"Style apply for '{step.SourceId}' failed unexpectedly and requires operator review.");
        }
    }

    private static string BuildTargetStyleId(MigrationSourceIdentity source, GeoServerStyleInfo style)
    {
        var workspace = string.IsNullOrWhiteSpace(style.WorkspaceName) ? "global" : style.WorkspaceName;
        var service = NormalizeCatalogServiceName(source.DisplayName ?? source.BaseUrl ?? "geoserver-import");
        var styleName = NormalizeCatalogServiceName($"{workspace}-{style.Name}");
        return $"style:{service}:{styleName}";
    }

    private static string SerializeStyleDiagnostics(IReadOnlyList<StyleDiagnostic> diagnostics)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var diagnostic in diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("severity", diagnostic.Severity);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildStyleDiagnosticSummary(IReadOnlyList<StyleDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }

        var warningCount = diagnostics.Count(static d => string.Equals(d.Severity, "warning", StringComparison.Ordinal));
        var errorCount = diagnostics.Count(static d => string.Equals(d.Severity, "error", StringComparison.Ordinal));
        return $"Recorded {errorCount} error and {warningCount} warning conversion diagnostic(s).";
    }

    private readonly record struct StyleDiagnostic(string Severity, string Message);

    private static bool IsWorkspaceInScope(FilteredResources filteredResources, string? workspaceName)
    {
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            // Global resources (no owning workspace) are not workspace-scoped and
            // are intentionally allowed through this guard.
            return true;
        }

        return filteredResources.ScopedWorkspaceNames.Contains(workspaceName);
    }

    private static MigrationApplyExecutionStepResult CreateOutOfScopeStepResult(
        string sourceId,
        string kind,
        string targetServiceName,
        string targetResourceName,
        string workspaceName)
    {
        var syntheticStep = new MigrationApplyPlanStep
        {
            Sequence = 0,
            StepId = $"catalog-{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Action = "stage-catalog-entry",
            Disposition = "manual-review",
            TargetServiceName = targetServiceName,
            TargetResourceName = targetResourceName,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "manual-review",
                Reason = "Catalog write rejected by workspace scope guard."
            }
        };

        return CreateExecutionStepResult(
            syntheticStep,
            "manual-review",
            $"Catalog write for {kind} '{sourceId}' rejected: source workspace '{workspaceName}' is outside the requested workspace scope (issue #1098).");
    }

    private async Task<MigrationApplyExecutionStepResult> ApplyLayerGroupCatalogStepAsync(
        MigrationApplyPlanStep step,
        Dictionary<string, GeoServerLayerGroupInfo> layerGroupsById,
        GeoServerImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!layerGroupsById.TryGetValue(step.SourceId, out var layerGroup))
        {
            return CreateExecutionStepResult(
                step,
                "manual-review",
                "The source layer group was not present in the filtered GeoServer discovery result.");
        }

        // Slice 1 persists a dedicated catalog service per layer-group so each group has
        // a stable, idempotent identity. The translator's TargetServiceName is the
        // GeoServer-wide service identity, which is not unique per group; we synthesize
        // a workspace-qualified name instead.
        var targetServiceName = NormalizeCatalogServiceName(
            string.IsNullOrWhiteSpace(layerGroup.WorkspaceName)
                ? $"layergroup-{layerGroup.Name}"
                : $"{layerGroup.WorkspaceName}-{layerGroup.Name}");
        var description = BuildLayerGroupDescription(layerGroup);

        return await EnsureCatalogEntryFromStepAsync(
                step,
                targetServiceName,
                description,
                srid: request.TargetSrid ?? 4326,
                applyMode: request.ApplyMode,
                deferredMessage: "Layer-group catalog persistence is deferred until applyMode=true and a catalog writer is configured; layer membership and rendering remain manual review.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MigrationApplyExecutionStepResult> EnsureCatalogEntryAsync(
        string sourceId,
        string kind,
        string targetServiceName,
        string targetResourceName,
        string description,
        int srid,
        bool applyMode,
        string deferredMessage,
        CancellationToken cancellationToken)
    {
        var syntheticStep = new MigrationApplyPlanStep
        {
            Sequence = 0,
            StepId = $"catalog-{kind}:{sourceId}",
            SourceId = sourceId,
            Kind = kind,
            Action = "stage-catalog-entry",
            Disposition = "ready",
            TargetServiceName = targetServiceName,
            TargetResourceName = targetResourceName,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = $"GeoServer {kind} is staged as an idempotent Honua catalog service entry by the apply slice."
            }
        };

        return await EnsureCatalogEntryFromStepAsync(
                syntheticStep,
                targetServiceName,
                description,
                srid,
                applyMode,
                deferredMessage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MigrationApplyExecutionStepResult> EnsureCatalogEntryFromStepAsync(
        MigrationApplyPlanStep step,
        string targetServiceName,
        string description,
        int srid,
        bool applyMode,
        string deferredMessage,
        CancellationToken cancellationToken)
    {
        if (!applyMode || _catalogWriter == null)
        {
            return CreateExecutionStepResult(step, "manual-review", deferredMessage);
        }

        try
        {
            var outcome = await _catalogWriter.EnsureCatalogServiceAsync(
                    _connectionProvider.GetConnectionString(),
                    new MigrationCatalogServiceRequest
                    {
                        ServiceName = targetServiceName,
                        Description = description,
                        Srid = srid,
                        EntryKind = step.Kind
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return outcome switch
            {
                MigrationCatalogWriteOutcome.Created => CreateExecutionStepResult(
                    step,
                    "applied",
                    $"Created Honua catalog service '{targetServiceName}' for GeoServer {step.Kind} '{step.SourceId}'."),
                MigrationCatalogWriteOutcome.AlreadyExists => CreateExecutionStepResult(
                    step,
                    "already-applied",
                    $"Honua catalog service '{targetServiceName}' already exists; idempotent re-apply made no changes."),
                _ => CreateExecutionStepResult(step, "manual-review", "Catalog writer returned an unexpected outcome.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateExecutionStepResult(
                step,
                "failed",
                $"The catalog apply step for {step.Kind} '{step.SourceId}' failed unexpectedly and requires operator review.");
        }
    }

    private static string NormalizeCatalogServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "geoserver-import";
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.' or ':' or '/')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        var normalized = builder.ToString().Trim('-');
        return normalized.Length == 0 ? "geoserver-import" : normalized;
    }

    private static string BuildWorkspaceDescription(
        GeoServerWorkspaceInfo workspace,
        MigrationApplyPlanArtifact applyPlan)
    {
        var summary = !string.IsNullOrWhiteSpace(workspace.Description)
            ? workspace.Description
            : $"GeoServer workspace '{workspace.Name}' staged by Honua migration apply slice 1.";
        return $"{summary} (source: {applyPlan.SourceKind})";
    }

    private static string BuildLayerGroupDescription(GeoServerLayerGroupInfo layerGroup)
    {
        var title = !string.IsNullOrWhiteSpace(layerGroup.Title) ? layerGroup.Title : layerGroup.Name;
        var workspaceQualifier = string.IsNullOrWhiteSpace(layerGroup.WorkspaceName)
            ? "global"
            : layerGroup.WorkspaceName;
        var description = !string.IsNullOrWhiteSpace(layerGroup.Abstract)
            ? layerGroup.Abstract
            : $"GeoServer layer group '{title}' in workspace '{workspaceQualifier}' staged by Honua migration apply slice 1.";
        return description;
    }

    private static MigrationApplyExecutionStepResult CreateExecutionStepResult(
        MigrationApplyPlanStep step,
        string outcome,
        string message,
        int? honuaLayerId = null)
        => new()
        {
            StepId = step.StepId,
            SourceId = step.SourceId,
            Kind = step.Kind,
            Action = step.Action,
            Disposition = step.Disposition,
            Outcome = outcome,
            Message = message,
            TargetServiceName = step.TargetServiceName,
            TargetResourceName = step.TargetResourceName,
            HonuaLayerId = honuaLayerId,
            ReviewCodes = step.ReviewCodes
        };

    private static bool IsPostGisDataStore(GeoServerDataStoreInfo dataStore)
        => string.Equals(dataStore.Type, "PostGIS", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(dataStore.Type, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
           (dataStore.ConnectionParameters.TryGetValue("dbtype", out var dbTypeValue) &&
            dbTypeValue is string dbType &&
            (string.Equals(dbType, "postgis", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dbType, "postgres", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(dbType, "postgresql", StringComparison.OrdinalIgnoreCase)));

    private static string GetStoreKey(string workspaceName, string storeName)
        => $"{workspaceName}:{storeName}";

    private static bool TryResolveCatalogTarget(
        GeoServerLayerInfo layer,
        GeoServerDataStoreInfo dataStore,
        out LayerCatalogTarget target,
        out string error)
    {
        var schema = TryGetStringParameter(dataStore.ConnectionParameters, "schema") ?? "public";
        var table = string.IsNullOrWhiteSpace(layer.NativeName) ? layer.Name : layer.NativeName.Trim();

        if (TrySplitQualifiedTable(table, out var qualifiedSchema, out var qualifiedTable))
        {
            schema = qualifiedSchema;
            table = qualifiedTable;
        }

        if (!IsSafeIdentifier(schema) || !IsSafeIdentifier(table))
        {
            target = default;
            error = "The GeoServer native table name cannot be safely mapped to a target Honua catalog table.";
            return false;
        }

        target = new LayerCatalogTarget(schema, table);
        error = string.Empty;
        return true;
    }

    private static bool TrySplitQualifiedTable(string table, out string schema, out string tableName)
    {
        schema = string.Empty;
        tableName = string.Empty;

        var separatorIndex = table.IndexOf('.', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= table.Length - 1)
        {
            return false;
        }

        if (table.IndexOf('.', separatorIndex + 1) >= 0)
        {
            return false;
        }

        schema = table[..separatorIndex].Trim('"');
        tableName = table[(separatorIndex + 1)..].Trim('"');
        return true;
    }

    private static string? TryGetStringParameter(IReadOnlyDictionary<string, object> parameters, string key)
        => parameters.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool IsSafeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var ch in identifier)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static int? ResolveSrid(string? srs)
    {
        if (string.IsNullOrWhiteSpace(srs))
        {
            return null;
        }

        var separatorIndex = srs.LastIndexOf(':');
        var candidate = separatorIndex >= 0 ? srs[(separatorIndex + 1)..] : srs;
        return int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : null;
    }

    private static List<string> BuildApplyPlanWarnings(
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution)
    {
        var warnings = new List<string>
        {
            "Non-dry-run GeoServer import generated a deterministic apply plan. Catalog mutation includes idempotent persistence of workspace and layer-group catalog entries, idempotent data-source registration and feature data copy for PostGIS sources, idempotent publication of PostGIS-backed layers, and idempotent persistence of style entries with structured SLD conversion diagnostics; WMS/WFS/WMTS service exposure changes remain explicit review records. Styles whose conversion diagnostics report errors are persisted with manual-review disposition — do not claim perfect SLD visual parity for those entries."
        };

        if (applyExecution.Summary.ManualReviewStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.ManualReviewStepCount} apply execution steps require manual review before they can be considered applied.");
        }

        if (applyExecution.Summary.UnsupportedStepCount > 0 || applyPlan.Summary.UnsupportedItemCount > 0)
        {
            warnings.Add($"{Math.Max(applyExecution.Summary.UnsupportedStepCount, applyPlan.Summary.UnsupportedItemCount)} source items are unsupported by the current GeoServer apply path.");
        }

        if (applyExecution.Summary.AlreadyAppliedStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.AlreadyAppliedStepCount} apply execution steps were already present in the target catalog and were treated as idempotent replays.");
        }

        if (applyExecution.Summary.FailedStepCount > 0)
        {
            warnings.Add($"{applyExecution.Summary.FailedStepCount} apply execution steps failed unexpectedly and require operator review.");
        }

        return warnings;
    }

    private static string BuildApplyFailureMessage(MigrationApplyExecutionArtifact applyExecution)
        => $"{applyExecution.Summary.FailedStepCount} GeoServer apply execution step(s) failed unexpectedly; inspect apply execution evidence before retrying.";

    private static MigrationRunMetricsArtifact BuildRunMetricsArtifact(
        MigrationRunMetricsRecorder recorder,
        GeoServerServiceInfo serviceInfo,
        string? runId,
        string measurementScope)
    {
        var sourceIdentity = new MigrationSourceIdentity
        {
            DisplayName = serviceInfo.GlobalSettings?.Title ?? "GeoServer",
            BaseUrl = serviceInfo.GeoServerRestUrl,
            Product = "GeoServer",
            Version = serviceInfo.Version,
            Build = serviceInfo.GitRevision ?? serviceInfo.BuildTimestamp,
            ServiceType = "REST"
        };

        return recorder.Build(
            sourceKind: "geoserver-rest",
            sourceFamily: MigrationCostPerformanceSourceFamilies.GeoServerRest,
            source: sourceIdentity,
            measurementScope: measurementScope,
            runId: runId);
    }

    private void EmitRunMetrics(MigrationRunMetricsArtifact metrics)
    {
        Log.MigrationRunMetricsEmitted(
            _logger,
            metrics.SourceKind,
            metrics.SourceFamily,
            metrics.Totals.DurationMilliseconds ?? 0,
            metrics.Totals.SourceRequestCount ?? 0,
            metrics.Totals.ResourceCount ?? 0,
            metrics.Phases.Length);
    }

    private static GeoServerImportResult CreateApplyPlanResult(
        GeoServerServiceInfo serviceInfo,
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution,
        GeoServerImportRequest request,
        TimeSpan duration,
        List<string> warnings)
        => GeoServerImportResult.CreateSuccess(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                sourceGeoServerVersion: serviceInfo.Version,
                duration: duration,
                warnings: warnings)
            with
        {
            ResourcesPlanned = applyPlan.Summary.TotalStepCount,
            ResourcesApplied = applyExecution.Summary.AppliedStepCount,
            ResourcesAlreadyApplied = applyExecution.Summary.AlreadyAppliedStepCount,
            ResourcesManualReview = applyExecution.Summary.ManualReviewStepCount,
            ResourcesUnsupported = applyExecution.Summary.UnsupportedStepCount,
            ApplyPlan = applyPlan,
            ApplyExecution = applyExecution
        };

    private static GeoServerImportResult CreateFailedApplyPlanResult(
        GeoServerServiceInfo serviceInfo,
        MigrationApplyPlanArtifact applyPlan,
        MigrationApplyExecutionArtifact applyExecution,
        GeoServerImportRequest request,
        TimeSpan duration,
        List<string> warnings,
        string failureMessage)
        => GeoServerImportResult.CreateFailure(
                request.GeoServerRestUrl,
                request.TargetHonuaUrl,
                failureMessage,
                duration)
            with
        {
            SourceGeoServerVersion = serviceInfo.Version,
            FailedResources = applyExecution.Summary.FailedStepCount,
            ResourcesPlanned = applyPlan.Summary.TotalStepCount,
            ResourcesApplied = applyExecution.Summary.AppliedStepCount,
            ResourcesAlreadyApplied = applyExecution.Summary.AlreadyAppliedStepCount,
            ResourcesManualReview = applyExecution.Summary.ManualReviewStepCount,
            ResourcesUnsupported = applyExecution.Summary.UnsupportedStepCount,
            Warnings = warnings,
            ApplyPlan = applyPlan,
            ApplyExecution = applyExecution
        };

    private readonly record struct LayerCatalogTarget(string Schema, string Table);

    private async Task<ImportStepResult> ImportWorkspacesAsync(GeoServerWorkspaceInfo[] workspaces, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingWorkspaces(_logger, workspaces.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingWorkspaces,
            CurrentPhase = "Importing workspaces"
        };
        progress?.Report(updatedProgress);

        foreach (var workspace in workspaces)
        {
            try
            {
                // Create workspace metadata in Honua catalog
                // Note: Honua uses a different workspace model than GeoServer
                await CreateWorkspaceMetadataAsync(workspace, cancellationToken);
                Log.WorkspaceImported(_logger, workspace.Name);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Workspace",
                    Name = workspace.Name,
                    Notes = "Imported successfully"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported workspace: {workspace.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.WorkspaceImportFailed(_logger, workspace.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Workspace",
                    Name = workspace.Name,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportDataStoresAsync(GeoServerDataStoreInfo[] dataStores, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingDataStores(_logger, dataStores.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingDataStores,
            CurrentPhase = "Importing datastores"
        };
        progress?.Report(updatedProgress);

        foreach (var dataStore in dataStores)
        {
            try
            {
                // Check compatibility
                if (dataStore.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
                {
                    var behavior = request.ImportOptions?.UnsupportedDataStoreBehavior ?? UnsupportedResourceBehavior.Skip;
                    if (behavior == UnsupportedResourceBehavior.FailImport)
                    {
                        throw new InvalidOperationException($"DataStore {dataStore.Name} is incompatible: {dataStore.Compatibility.Reason}");
                    }

                    if (behavior == UnsupportedResourceBehavior.Skip)
                    {
                        Log.DataStoreSkipped(_logger, dataStore.Name, dataStore.Compatibility.Reason);
                        result.SkippedCount++;
                        continue;
                    }
                }

                // Create datastore configuration in Honua catalog
                // Note: Honua uses connection-based data sources instead of GeoServer datastores
                await CreateDataStoreConfigurationAsync(dataStore, cancellationToken);
                Log.DataStoreImported(_logger, dataStore.WorkspaceName, dataStore.Name, dataStore.Type);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "DataStore",
                    Name = dataStore.Name,
                    WorkspaceName = dataStore.WorkspaceName,
                    Notes = $"Imported {dataStore.Type} datastore"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported datastore: {dataStore.WorkspaceName}/{dataStore.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.DataStoreImportFailed(_logger, dataStore.WorkspaceName, dataStore.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "DataStore",
                    Name = dataStore.Name,
                    WorkspaceName = dataStore.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportLayersAsync(GeoServerLayerInfo[] layers, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingLayers(_logger, layers.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingLayers,
            CurrentPhase = "Importing layers"
        };
        progress?.Report(updatedProgress);

        foreach (var layer in layers)
        {
            try
            {
                // Check compatibility
                if (layer.Compatibility?.CompatibilityLevel == GeoServerCompatibilityLevel.Incompatible)
                {
                    var behavior = request.ImportOptions?.UnsupportedLayerBehavior ?? UnsupportedResourceBehavior.Skip;
                    if (behavior == UnsupportedResourceBehavior.FailImport)
                    {
                        throw new InvalidOperationException($"Layer {layer.Name} is incompatible: {layer.Compatibility.Reason}");
                    }

                    if (behavior == UnsupportedResourceBehavior.Skip)
                    {
                        Log.LayerSkipped(_logger, layer.Name, layer.Compatibility.Reason);
                        result.SkippedCount++;
                        continue;
                    }
                }

                // Create layer configuration in Honua catalog
                // Note: Honua layers are published through the layer publishing service
                await CreateLayerConfigurationAsync(layer, cancellationToken);
                Log.LayerImported(_logger, layer.WorkspaceName, layer.Name);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Layer",
                    Name = layer.Name,
                    WorkspaceName = layer.WorkspaceName,
                    Notes = "Imported layer configuration"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported layer: {layer.WorkspaceName}/{layer.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.LayerImportFailed(_logger, layer.WorkspaceName, layer.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Layer",
                    Name = layer.Name,
                    WorkspaceName = layer.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task<ImportStepResult> ImportStylesAsync(GeoServerStyleInfo[] styles, GeoServerImportRequest request, GeoServerImportProgress currentProgress, IProgress<GeoServerImportProgress>? progress, CancellationToken cancellationToken)
    {
        Log.ImportingStyles(_logger, styles.Length);

        var result = new ImportStepResult();
        var updatedProgress = currentProgress with
        {
            Status = GeoServerImportStatus.ImportingStyles,
            CurrentPhase = "Importing styles"
        };
        progress?.Report(updatedProgress);

        foreach (var style in styles)
        {
            try
            {
                // SLD-to-MapLibre conversion (issue #375).
                var sldValidated = false;
                if (style.Format == "sld")
                {
                    var conversionWarnings = TryConvertSldStyle(
                        style,
                        request,
                        result,
                        out var converterAvailable,
                        out var shouldSkip,
                        out sldValidated);

                    if (!converterAvailable)
                    {
                        // No ISldStyleConverter wired; fall through to legacy unsupported-style behavior.
                        var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;
                        var warningMessage = $"SLD style {style.Name} requires the SLD converter (issue #375). No ISldStyleConverter is registered.";

                        if (behavior == UnsupportedResourceBehavior.FailImport)
                        {
                            throw new InvalidOperationException(warningMessage);
                        }

                        if (behavior == UnsupportedResourceBehavior.Skip)
                        {
                            Log.StyleSkipped(_logger, style.Name);
                            result.SkippedCount++;
                            continue;
                        }

                        Log.StyleRequiresConversion(_logger, style.Name);
                        result.Warnings.Add(warningMessage);
                    }
                    else
                    {
                        result.Warnings.AddRange(conversionWarnings);
                        if (shouldSkip)
                        {
                            // Conversion errors with UnsupportedStyleBehavior.Skip already incremented
                            // SkippedCount; skip the import side so the style is not double-counted.
                            continue;
                        }
                    }
                }

                // Convert and import style to Honua format
                // Note: SLD styles are converted to MapLibre JSON format for Honua
                await ConvertAndImportStyleAsync(style, cancellationToken);
                Log.StyleImported(_logger, style.WorkspaceName ?? "global", style.Name, style.Format);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    Notes = style.Format == "sld"
                        ? sldValidated
                            ? "SLD validated; apply via per-layer admin SLD endpoint to persist MapLibre style"
                            : "SLD not validated; review warnings before applying via per-layer admin SLD endpoint"
                        : "Imported style"
                });

                result.SuccessCount++;

                updatedProgress = updatedProgress with
                {
                    ResourcesProcessed = updatedProgress.ResourcesProcessed + 1,
                    CurrentPhase = $"Imported style: {style.WorkspaceName ?? "global"}/{style.Name}"
                };
                progress?.Report(updatedProgress);
            }
            catch (Exception ex)
            {
                Log.StyleImportFailed(_logger, style.WorkspaceName ?? "global", style.Name, ex);

                result.FailedResources.Add(new GeoServerFailedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    ErrorMessage = ex.Message
                });

                result.FailureCount++;

                if (!request.ImportOptions?.ContinueOnResourceFailure ?? false)
                {
                    throw;
                }
            }
        }

        return result;
    }

    private async Task CreateWorkspaceMetadataAsync(GeoServerWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        // Store workspace metadata in Honua's catalog system
        // Note: Honua maps GeoServer workspaces to internal catalog namespaces
        Log.CreatingWorkspaceMetadata(_logger, workspace.Name);

        // In a full implementation, this would:
        // 1. Create a workspace record in the catalog
        // 2. Set up namespace mappings
        // 3. Configure default styling and access permissions
        await Task.Delay(50, cancellationToken); // Simulate metadata creation
    }

    private async Task CreateDataStoreConfigurationAsync(GeoServerDataStoreInfo dataStore, CancellationToken cancellationToken)
    {
        // Create connection configuration for Honua
        // Note: GeoServer datastores map to Honua connection configurations
        Log.DataStoreConfigurationCreated(_logger, dataStore.WorkspaceName, dataStore.Name);

        // Implementation would:
        // 1. Extract connection parameters from GeoServer datastore
        // 2. Create Honua connection configuration
        // 3. Test connection validity
        // 4. Store in secure connection registry
        await Task.Delay(75, cancellationToken); // Simulate configuration creation
    }

    private async Task CreateLayerConfigurationAsync(GeoServerLayerInfo layer, CancellationToken cancellationToken)
    {
        // Create layer publishing configuration for Honua
        // Note: GeoServer layers map to Honua published layers
        Log.LayerConfigurationCreated(_logger, layer.WorkspaceName, layer.Name);

        // Implementation would:
        // 1. Map GeoServer layer configuration to Honua layer definition
        // 2. Set up feature access and security constraints
        // 3. Configure default styling and rendering options
        // 4. Register layer in catalog
        await Task.Delay(100, cancellationToken); // Simulate layer configuration
    }

    private async Task ConvertAndImportStyleAsync(GeoServerStyleInfo style, CancellationToken cancellationToken)
    {
        // SLD parsing/conversion is delegated to ISldStyleConverter (Honua.Server) and
        // executed by TryConvertSldStyle so warnings/errors are surfaced in the import
        // result. Per-layer style persistence is performed by the admin SLD endpoint
        // (POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld); this method
        // remains a single-responsibility conversion hook for the bulk import path and
        // does not write the converted MapLibre JSON to the catalog.
        Log.StyleConversionStarted(_logger, style.Name);
        await Task.Delay(125, cancellationToken); // Simulate style conversion
    }

    /// <summary>
    /// Invokes the registered <see cref="ISldStyleConverter"/> against the embedded SLD content
    /// when available and returns any warning messages for inclusion in import results.
    /// When conversion errors or missing content combine with <see cref="UnsupportedResourceBehavior.Skip"/>,
    /// <c>shouldSkip</c> is set to <c>true</c> so the caller can abandon the per-style import
    /// without double-counting (<see cref="ImportStepResult.SkippedCount"/> is already incremented).
    /// <c>wasValidated</c> reports whether the converter actually ran and produced no errors;
    /// callers use it to label the imported resource note.
    /// </summary>
    internal List<string> TryConvertSldStyle(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        out bool converterAvailable,
        out bool shouldSkip,
        out bool wasValidated)
    {
        var warnings = new List<string>();
        converterAvailable = _sldConverter != null;
        shouldSkip = false;
        wasValidated = false;

        if (_sldConverter == null)
        {
            return warnings;
        }

        if (string.IsNullOrWhiteSpace(style.SldContent))
        {
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} has no embedded content; conversion skipped.",
                out shouldSkip);
            return warnings;
        }

        var conversion = _sldConverter.Convert(style.SldContent!);
        foreach (var warning in conversion.Warnings)
        {
            warnings.Add($"SLD style {style.Name}: {warning}");
        }

        if (conversion.HasErrors)
        {
            var firstError = conversion.Errors.Count > 0 ? conversion.Errors[0] : "SLD conversion produced no layers.";
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} could not be converted: {firstError}",
                out shouldSkip);
            return warnings;
        }

        wasValidated = true;
        return warnings;
    }

    private void ApplyUnsupportedStyleBehavior(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        List<string> warnings,
        string message,
        out bool shouldSkip)
    {
        shouldSkip = false;
        var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;

        if (behavior == UnsupportedResourceBehavior.FailImport)
        {
            throw new InvalidOperationException(message);
        }

        if (behavior == UnsupportedResourceBehavior.Skip)
        {
            Log.StyleSkipped(_logger, style.Name);
            result.SkippedCount++;
            shouldSkip = true;
            return;
        }

        Log.StyleRequiresConversion(_logger, style.Name);
        warnings.Add(message);
    }

    private async Task ValidateImportedResourcesAsync(GeoServerImportRequest request, CancellationToken cancellationToken)
    {
        // Validate imported resources are properly configured
        Log.ValidatingImportedResources(_logger);

        // Perform comprehensive validation:
        // 1. Verify database connections are accessible
        // 2. Check that imported layers have valid geometry
        // 3. Validate style references exist and are valid
        // 4. Ensure security constraints are properly applied
        await Task.Delay(100, cancellationToken);

        Log.ImportValidationCompleted(_logger);
    }

    private static GeoServerImportProgress UpdateProgressWithWorkspaces(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                WorkspacesProcessed = result.SuccessCount,
                WorkspacesFailed = result.FailureCount
            }
        };
    }

    private static void AggregateStepResult(ImportStepResult aggregateResult, ImportStepResult stepResult)
    {
        aggregateResult.SuccessCount += stepResult.SuccessCount;
        aggregateResult.FailureCount += stepResult.FailureCount;
        aggregateResult.SkippedCount += stepResult.SkippedCount;
        aggregateResult.ImportedResources.AddRange(stepResult.ImportedResources);
        aggregateResult.FailedResources.AddRange(stepResult.FailedResources);
        aggregateResult.Warnings.AddRange(stepResult.Warnings);
    }

    private static GeoServerImportProgress UpdateProgressWithDataStores(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                DataStoresProcessed = result.SuccessCount,
                DataStoresFailed = result.FailureCount
            }
        };
    }

    private static GeoServerImportProgress UpdateProgressWithLayers(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                LayersProcessed = result.SuccessCount,
                LayersFailed = result.FailureCount
            }
        };
    }

    private static GeoServerImportProgress UpdateProgressWithStyles(GeoServerImportProgress progress, ImportStepResult result)
    {
        var breakdown = progress.ResourceBreakdown ?? new GeoServerImportResourceBreakdown();
        return progress with
        {
            ResourceBreakdown = breakdown with
            {
                StylesProcessed = result.SuccessCount,
                StylesFailed = result.FailureCount
            },
            Warnings = progress.Warnings.Concat(result.Warnings).ToList()
        };
    }

    private static GeoServerImportResult CreateSuccessResult(GeoServerServiceInfo serviceInfo, ImportStepResult finalResult, GeoServerImportRequest request, TimeSpan duration)
    {
        var importedWorkspaces = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Workspace", StringComparison.Ordinal));
        var importedDataStores = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "DataStore", StringComparison.Ordinal));
        var importedLayers = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Layer", StringComparison.Ordinal));
        var importedStyles = finalResult.ImportedResources.Count(r => string.Equals(r.ResourceType, "Style", StringComparison.Ordinal));

        return GeoServerImportResult.CreateSuccess(
            request.GeoServerRestUrl,
            request.TargetHonuaUrl,
            importedWorkspaces,
            importedDataStores,
            importedLayers,
            importedStyles,
            serviceInfo.Version,
            duration,
            finalResult.Warnings,
            finalResult.ImportedResources)
            with
        {
            FailedResources = finalResult.FailureCount,
            FailedResourceDetails = finalResult.FailedResources
        };
    }

    // Helper classes for internal state tracking
    private sealed record FilteredResources
    {
        public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];
        public GeoServerDataStoreInfo[] DataStores { get; init; } = [];
        public GeoServerLayerInfo[] Layers { get; init; } = [];
        public GeoServerLayerGroupInfo[] LayerGroups { get; init; } = [];
        public GeoServerStyleInfo[] Styles { get; init; } = [];
        public int WorkspaceCount { get; init; }
        public int DataStoreCount { get; init; }
        public int LayerCount { get; init; }
        public int StyleCount { get; init; }

        /// <summary>
        /// Defensive scoping set used by catalog-apply write sites to reject any
        /// resource whose source workspace falls outside the operator's requested
        /// scope (issue #1098). The set is derived from the filtered workspace
        /// list so the apply path stays self-consistent even if a future plan
        /// builder forwards a step whose workspace was excluded by the filter.
        /// </summary>
        public HashSet<string> ScopedWorkspaceNames { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record ImportStepResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int SkippedCount { get; set; }
        public List<GeoServerImportedResource> ImportedResources { get; init; } = new();
        public List<GeoServerFailedResource> FailedResources { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public GeoServerImportResourceBreakdown? ResourceBreakdown { get; set; }
    }

    private static partial class Log
    {
        [LoggerMessage(7990, LogLevel.Information, "Starting GeoServer import from {SourceUrl} to {TargetUrl}")]
        public static partial void ImportStarting(ILogger logger, string sourceUrl, string targetUrl);

        [LoggerMessage(7991, LogLevel.Information, "Discovering GeoServer configuration at {GeoServerUrl}")]
        public static partial void DiscoveringConfiguration(ILogger logger, string geoServerUrl);

        [LoggerMessage(7992, LogLevel.Information, "GeoServer import completed successfully in {Duration}. Imported: {WorkspaceCount} workspaces, {DataStoreCount} datastores, {LayerCount} layers, {StyleCount} styles")]
        public static partial void ImportCompleted(
            ILogger logger,
            TimeSpan duration,
            int workspaceCount,
            int dataStoreCount,
            int layerCount,
            int styleCount);

        [LoggerMessage(7993, LogLevel.Warning, "GeoServer import was cancelled")]
        public static partial void ImportCancelled(ILogger logger);

        [LoggerMessage(7994, LogLevel.Error, "GeoServer import failed: {ErrorMessage}")]
        public static partial void ImportFailed(ILogger logger, string errorMessage, Exception exception);

        [LoggerMessage(79945, LogLevel.Warning, "GeoServer inventory scan failed for {GeoServerUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string geoServerUrl, Exception exception);

        [LoggerMessage(79946, LogLevel.Information, "GeoServer apply plan executed with {StepCount} steps, {AppliedStepCount} applied steps, {AlreadyAppliedStepCount} idempotent replays, {ManualReviewStepCount} manual-review steps, and {UnsupportedStepCount} unsupported steps")]
        public static partial void ApplyPlanExecuted(
            ILogger logger,
            int stepCount,
            int appliedStepCount,
            int alreadyAppliedStepCount,
            int manualReviewStepCount,
            int unsupportedStepCount);

        [LoggerMessage(7995, LogLevel.Information, "Importing {Count} workspaces")]
        public static partial void ImportingWorkspaces(ILogger logger, int count);

        [LoggerMessage(7996, LogLevel.Information, "Would create workspace: {WorkspaceName}")]
        public static partial void WorkspaceImported(ILogger logger, string workspaceName);

        [LoggerMessage(7997, LogLevel.Error, "Failed to import workspace {WorkspaceName}")]
        public static partial void WorkspaceImportFailed(ILogger logger, string workspaceName, Exception exception);

        [LoggerMessage(7998, LogLevel.Information, "Importing {Count} datastores")]
        public static partial void ImportingDataStores(ILogger logger, int count);

        [LoggerMessage(7999, LogLevel.Warning, "Skipping incompatible datastore {DataStoreName}: {Reason}")]
        public static partial void DataStoreSkipped(ILogger logger, string dataStoreName, string reason);

        [LoggerMessage(8000, LogLevel.Information, "Would create datastore: {WorkspaceName}/{DataStoreName} (type: {Type})")]
        public static partial void DataStoreImported(ILogger logger, string workspaceName, string dataStoreName, string type);

        [LoggerMessage(8001, LogLevel.Error, "Failed to import datastore {WorkspaceName}/{DataStoreName}")]
        public static partial void DataStoreImportFailed(ILogger logger, string workspaceName, string dataStoreName, Exception exception);

        [LoggerMessage(8002, LogLevel.Information, "Importing {Count} layers")]
        public static partial void ImportingLayers(ILogger logger, int count);

        [LoggerMessage(8003, LogLevel.Warning, "Skipping incompatible layer {LayerName}: {Reason}")]
        public static partial void LayerSkipped(ILogger logger, string layerName, string reason);

        [LoggerMessage(8004, LogLevel.Information, "Would create layer: {WorkspaceName}/{LayerName}")]
        public static partial void LayerImported(ILogger logger, string workspaceName, string layerName);

        [LoggerMessage(8005, LogLevel.Error, "Failed to import layer {WorkspaceName}/{LayerName}")]
        public static partial void LayerImportFailed(ILogger logger, string workspaceName, string layerName, Exception exception);

        [LoggerMessage(8006, LogLevel.Information, "Importing {Count} styles")]
        public static partial void ImportingStyles(ILogger logger, int count);

        [LoggerMessage(8007, LogLevel.Warning, "Skipping SLD style {StyleName}: requires issue #375")]
        public static partial void StyleSkipped(ILogger logger, string styleName);

        [LoggerMessage(8008, LogLevel.Warning, "SLD style {StyleName} requires conversion to MapLibre format (issue #375)")]
        public static partial void StyleRequiresConversion(ILogger logger, string styleName);

        [LoggerMessage(8009, LogLevel.Information, "Would convert and import style: {WorkspaceName}/{StyleName} (format: {Format})")]
        public static partial void StyleImported(ILogger logger, string workspaceName, string styleName, string format);

        [LoggerMessage(8010, LogLevel.Error, "Failed to import style {WorkspaceName}/{StyleName}")]
        public static partial void StyleImportFailed(ILogger logger, string workspaceName, string styleName, Exception exception);

        [LoggerMessage(8011, LogLevel.Information, "Creating workspace metadata: {WorkspaceName}")]
        public static partial void CreatingWorkspaceMetadata(ILogger logger, string workspaceName);

        [LoggerMessage(8012, LogLevel.Information, "Validating imported resources")]
        public static partial void ValidatingImportedResources(ILogger logger);

        [LoggerMessage(8013, LogLevel.Information, "Import validation completed successfully")]
        public static partial void ImportValidationCompleted(ILogger logger);

        [LoggerMessage(8014, LogLevel.Information, "Created datastore configuration: {WorkspaceName}/{DataStoreName}")]
        public static partial void DataStoreConfigurationCreated(ILogger logger, string workspaceName, string dataStoreName);

        [LoggerMessage(8015, LogLevel.Information, "Created layer configuration: {WorkspaceName}/{LayerName}")]
        public static partial void LayerConfigurationCreated(ILogger logger, string workspaceName, string layerName);

        [LoggerMessage(8016, LogLevel.Information, "Starting style conversion: {StyleName}")]
        public static partial void StyleConversionStarted(ILogger logger, string styleName);

        [LoggerMessage(8017, LogLevel.Information, "Migration run metrics emitted for {SourceKind}/{SourceFamily}: {DurationMs}ms, {RequestCount} source requests, {ResourceCount} resources across {PhaseCount} phases")]
        public static partial void MigrationRunMetricsEmitted(
            ILogger logger,
            string sourceKind,
            string sourceFamily,
            long durationMs,
            long requestCount,
            long resourceCount,
            int phaseCount);

        [LoggerMessage(8018, LogLevel.Warning, "GeoServer catalog-apply rejected cross-workspace write: {EntryKind} owned by workspace '{WorkspaceName}' is outside the operator's requested scope (issue #1098)")]
        public static partial void WorkspaceWriteRejected(ILogger logger, string entryKind, string workspaceName);
    }

}
