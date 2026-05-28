// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Inventory-artifact construction for the GeoServer source scan: builds
/// containers, resources, styles, and external dependencies from the discovered
/// <c>GeoServerServiceInfo</c>, then assembles a single
/// <c>MigrationSourceInventoryArtifact</c> with summary/compatibility metadata.
/// </summary>
internal sealed partial class GeoServerImportService
{
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

}
