// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Shared selection logic for GeoServer discovery-driven workflows.
/// </summary>
internal static class GeoServerSelectionPlanner
{
    public static GeoServerSelectedResources Filter(
        GeoServerServiceInfo serviceInfo,
        string[]? workspaceNames,
        string[]? dataStoreNames,
        string[]? layerNames,
        bool includeStyles)
    {
        ArgumentNullException.ThrowIfNull(serviceInfo);

        var workspaces = (workspaceNames == null
                ? serviceInfo.Workspaces
                : serviceInfo.Workspaces.Where(workspace => workspaceNames.Contains(workspace.Name, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var workspaceSet = new HashSet<string>(workspaces.Select(workspace => workspace.Name), StringComparer.OrdinalIgnoreCase);

        var dataStores = serviceInfo.DataStores
            .Where(dataStore => workspaceSet.Contains(dataStore.WorkspaceName))
            .Where(dataStore => dataStoreNames == null || MatchesQualifiedName(dataStore.WorkspaceName, dataStore.Name, dataStoreNames))
            .OrderBy(dataStore => dataStore.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(dataStore => dataStore.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var coverageStores = serviceInfo.CoverageStores
            .Where(store => workspaceSet.Contains(store.WorkspaceName))
            .Where(store => dataStoreNames == null || MatchesQualifiedName(store.WorkspaceName, store.Name, dataStoreNames))
            .OrderBy(store => store.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(store => store.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedStoreKeys = new HashSet<string>(
            dataStores.Select(static dataStore => GetQualifiedKey(dataStore.WorkspaceName, dataStore.Name))
                .Concat(coverageStores.Select(static store => GetQualifiedKey(store.WorkspaceName, store.Name))),
            StringComparer.OrdinalIgnoreCase);

        var layers = serviceInfo.Layers
            .Where(layer => workspaceSet.Contains(layer.WorkspaceName))
            .Where(layer =>
                layerNames != null
                    ? MatchesQualifiedName(layer.WorkspaceName, layer.Name, layerNames)
                    : dataStoreNames == null ||
                      (!string.IsNullOrWhiteSpace(layer.DataStoreName) &&
                       selectedStoreKeys.Contains(GetQualifiedKey(layer.WorkspaceName, layer.DataStoreName))) ||
                      (!string.IsNullOrWhiteSpace(layer.CoverageStoreName) &&
                       selectedStoreKeys.Contains(GetQualifiedKey(layer.WorkspaceName, layer.CoverageStoreName))))
            .OrderBy(layer => layer.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedLayerKeys = new HashSet<string>(
            layers.Select(static layer => GetQualifiedKey(layer.WorkspaceName, layer.Name)),
            StringComparer.OrdinalIgnoreCase);

        var layerGroups = serviceInfo.LayerGroups
            .Where(group =>
                string.IsNullOrWhiteSpace(group.WorkspaceName)
                    ? group.Layers.Any(layer => selectedLayerKeys.Contains(layer.Name))
                    : workspaceSet.Contains(group.WorkspaceName))
            .OrderBy(group => group.WorkspaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var styles = includeStyles
            ? serviceInfo.Styles
                .Where(style => IsStyleReferenced(style, layers))
                .OrderBy(style => style.WorkspaceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(style => style.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return new GeoServerSelectedResources
        {
            Workspaces = workspaces,
            DataStores = dataStores,
            CoverageStores = coverageStores,
            Layers = layers,
            LayerGroups = layerGroups,
            Styles = styles
        };
    }

    public static string GetQualifiedKey(string? workspaceName, string resourceName)
        => string.IsNullOrWhiteSpace(workspaceName)
            ? resourceName
            : $"{workspaceName}:{resourceName}";

    private static bool MatchesQualifiedName(string workspaceName, string resourceName, IReadOnlyList<string> requestedNames)
        => requestedNames.Any(requestedName =>
            requestedName.Equals(resourceName, StringComparison.OrdinalIgnoreCase) ||
            requestedName.Equals(GetQualifiedKey(workspaceName, resourceName), StringComparison.OrdinalIgnoreCase));

    private static bool IsStyleReferenced(GeoServerStyleInfo style, IReadOnlyList<GeoServerLayerInfo> layers)
    {
        foreach (var layer in layers)
        {
            if (MatchesStyleReference(layer.DefaultStyle, style, layer.WorkspaceName))
            {
                return true;
            }

            if (layer.AlternativeStyles.Any(reference => MatchesStyleReference(reference, style, layer.WorkspaceName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesStyleReference(string? reference, GeoServerStyleInfo style, string layerWorkspace)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (reference.Equals(style.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return reference.Equals(
            GetQualifiedKey(style.WorkspaceName ?? layerWorkspace, style.Name),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record GeoServerSelectedResources
{
    public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];
    public GeoServerDataStoreInfo[] DataStores { get; init; } = [];
    public GeoServerCoverageStoreInfo[] CoverageStores { get; init; } = [];
    public GeoServerLayerInfo[] Layers { get; init; } = [];
    public GeoServerLayerGroupInfo[] LayerGroups { get; init; } = [];
    public GeoServerStyleInfo[] Styles { get; init; } = [];
}
