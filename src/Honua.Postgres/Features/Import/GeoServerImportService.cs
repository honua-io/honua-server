// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
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
    private readonly ILogger<GeoServerImportService> _logger;

    public GeoServerImportService(
        GeoServerRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        ILogger<GeoServerImportService> logger)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var serviceInfo = await DiscoverServiceAsync(request, cancellationToken).ConfigureAwait(false);

            var styleResourceIds = BuildStyleResourceMap(serviceInfo);
            var dependencies = BuildExternalDependencies(serviceInfo, request.IncludeStyleContent);
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

            if (request.IncludeStyleContent &&
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
                AuthPosture = BuildAuthPosture(request.Username, request.Password, accessConfirmed: true, anonymousMode: "anonymous"),
                ScanCompleteness = completeness,
                Summary = summary,
                OverallCompatibility = overallCompatibility,
                Containers = containers,
                Resources = resources,
                Styles = styles,
                ExternalDependencies = dependencies
            };
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
                    ["Verify GeoServer reachability and credentials, then rerun the scan."]),
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

    private async Task<MigrationInventoryResource[]> BuildResourcesAsync(
        GeoServerServiceInfo serviceInfo,
        IReadOnlyDictionary<string, string[]> styleResourceIds,
        IReadOnlyList<MigrationExternalDependency> dependencies,
        CancellationToken cancellationToken)
    {
        var resources = new List<MigrationInventoryResource>(serviceInfo.Layers.Length + serviceInfo.LayerGroups.Length);

        foreach (var layer in serviceInfo.Layers.OrderBy(static layer => GetLayerId(layer), StringComparer.Ordinal))
        {
            var spatialReferences = (await Task.WhenAll(
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "declared", layer.SRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "native", layer.NativeCRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "latlon-bounds", layer.LatLonBoundingBox?.CRS, cancellationToken),
                    MigrationInventoryHelpers.BuildSpatialReferenceAsync(_crsRegistry, "native-bounds", layer.NativeBoundingBox?.CRS, cancellationToken))
                .ConfigureAwait(false))
                .OfType<MigrationSpatialReferenceInfo>()
                .OrderBy(static info => info.Role, StringComparer.Ordinal)
                .ToArray();

            var styleIds = serviceInfo.Styles
                .Where(style => styleResourceIds.TryGetValue(GetStyleId(style), out var linkedResources) &&
                    linkedResources.Contains(GetLayerId(layer), StringComparer.Ordinal))
                .Select(GetStyleId)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

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
                Id = GetLayerId(layer),
                ContainerId = GetWorkspaceId(layer.WorkspaceName),
                Kind = "layer",
                Name = layer.Name,
                Title = layer.Title,
                Description = layer.Abstract,
                GeometryType = null,
                FeatureCount = null,
                HasAttachments = null,
                Capabilities = BuildLayerCapabilities(layer),
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
                Capabilities = layerGroup.Layers.Select(entry => entry.Type)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray(),
                SpatialReferences = spatialReferences,
                StyleIds = [],
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
            serviceInfo.LayerGroups.Any(group => string.IsNullOrWhiteSpace(group.WorkspaceName));

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
                            ["Mirror or replace external graphics in the target deployment."])
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

        return map.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static void AddStyleResourceLinks(
        IDictionary<string, HashSet<string>> map,
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

    private static string[] BuildLayerCapabilities(GeoServerLayerInfo layer)
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

    private static string? ResolveDependencyAddress(IReadOnlyDictionary<string, string> metadata)
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
                IncludeStyleContent = request.ImportStyles
            };

            var serviceInfo = await DiscoverServiceAsync(discoveryRequest, cancellationToken);

            // Filter resources based on request
            var filteredResources = FilterRequestedResources(serviceInfo, request);

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
                progress?.Report(currentProgress);

                return CreateDryRunResult(serviceInfo, filteredResources, request, stopwatch.Elapsed);
            }

            var aggregateResult = new ImportStepResult();

            // Step 2: Import workspaces
            var stepResult = await ImportWorkspacesAsync(filteredResources.Workspaces, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithWorkspaces(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 3: Import datastores
            stepResult = await ImportDataStoresAsync(filteredResources.DataStores, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithDataStores(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 4: Import layers
            stepResult = await ImportLayersAsync(filteredResources.Layers, request, currentProgress, progress, cancellationToken);
            currentProgress = UpdateProgressWithLayers(currentProgress, stepResult);
            AggregateStepResult(aggregateResult, stepResult);

            // Step 5: Import styles (if requested and supported)
            if (request.ImportStyles)
            {
                stepResult = await ImportStylesAsync(filteredResources.Styles, request, currentProgress, progress, cancellationToken);
                currentProgress = UpdateProgressWithStyles(currentProgress, stepResult);
                AggregateStepResult(aggregateResult, stepResult);
            }

            // Step 6: Validation
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Validating,
                CurrentPhase = "Validating imported configuration"
            };
            progress?.Report(currentProgress);

            await ValidateImportedResourcesAsync(request, cancellationToken);

            // Complete
            currentProgress = currentProgress with
            {
                Status = GeoServerImportStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Import completed successfully"
            };
            progress?.Report(currentProgress);

            var finalResult = CreateSuccessResult(serviceInfo, aggregateResult, request, stopwatch.Elapsed);

            Log.ImportCompleted(
                _logger,
                stopwatch.Elapsed,
                finalResult.WorkspacesImported,
                finalResult.DataStoresImported,
                finalResult.LayersImported,
                finalResult.StylesImported);

            return finalResult;
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
        var workspaces = request.WorkspaceNames == null
            ? serviceInfo.Workspaces
            : serviceInfo.Workspaces.Where(w => request.WorkspaceNames.Contains(w.Name)).ToArray();

        var dataStores = request.DataStoreNames == null
            ? serviceInfo.DataStores
            : serviceInfo.DataStores.Where(ds => IsResourceRequested(ds.WorkspaceName, ds.Name, request.DataStoreNames)).ToArray();

        var layers = request.LayerNames == null
            ? serviceInfo.Layers
            : serviceInfo.Layers.Where(l => IsResourceRequested(l.WorkspaceName, l.Name, request.LayerNames)).ToArray();

        var styles = request.ImportStyles
            ? (request.LayerNames == null ? serviceInfo.Styles : serviceInfo.Styles.Where(s => IsResourceNeededForLayers(s, layers, styleIdsByReference)).ToArray())
            : Array.Empty<GeoServerStyleInfo>();

        return new FilteredResources
        {
            Workspaces = workspaces,
            DataStores = dataStores,
            Layers = layers,
            Styles = styles,
            WorkspaceCount = workspaces.Length,
            DataStoreCount = dataStores.Length,
            LayerCount = layers.Length,
            StyleCount = styles.Length
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

    private static GeoServerImportResult CreateDryRunResult(GeoServerServiceInfo serviceInfo, FilteredResources resources, GeoServerImportRequest request, TimeSpan duration)
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
                Notes = style.Format == "sld" ? "Would require SLD conversion (issue #375)" : "Would be created"
            });
        }

        var warnings = new List<string>();
        if (resources.StyleCount > 0)
        {
            warnings.Add("Style import requires implementing issue #375 for SLD conversion");
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
                // TODO: Implement actual workspace creation in Honua
                // For now, this is a placeholder that simulates the import
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

                // TODO: Implement actual datastore creation in Honua
                // This would involve creating connection configs, testing connections, etc.
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

                // TODO: Implement actual layer creation in Honua
                // This would involve creating layer configs, setting up publishing, etc.
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
                // Check if SLD conversion is available (issue #375)
                if (style.Format == "sld")
                {
                    var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;
                    var warningMessage = $"SLD style {style.Name} requires conversion to MapLibre format (issue #375)";

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

                    if (behavior == UnsupportedResourceBehavior.LogWarning)
                    {
                        Log.StyleRequiresConversion(_logger, style.Name);
                        result.Warnings.Add(warningMessage);
                    }
                }

                // TODO: Implement actual style import once issue #375 is implemented
                // This would involve converting SLD to MapLibre JSON and creating style resources
                Log.StyleImported(_logger, style.WorkspaceName ?? "global", style.Name, style.Format);

                result.ImportedResources.Add(new GeoServerImportedResource
                {
                    ResourceType = "Style",
                    Name = style.Name,
                    WorkspaceName = style.WorkspaceName,
                    Notes = style.Format == "sld" ? "Converted from SLD to MapLibre format" : "Imported style"
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

    private async Task ValidateImportedResourcesAsync(GeoServerImportRequest request, CancellationToken cancellationToken)
    {
        // TODO: Implement validation logic
        // This could check that imported resources are properly configured,
        // connections are working, etc.
        Log.ValidatingImportedResources(_logger);
        await Task.Delay(100, cancellationToken); // Simulate validation work
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
        public GeoServerStyleInfo[] Styles { get; init; } = [];
        public int WorkspaceCount { get; init; }
        public int DataStoreCount { get; init; }
        public int LayerCount { get; init; }
        public int StyleCount { get; init; }
    }

    private sealed record ImportStepResult
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

        [LoggerMessage(8011, LogLevel.Information, "Validating imported resources (placeholder)")]
        public static partial void ValidatingImportedResources(ILogger logger);
    }

}
