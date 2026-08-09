// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Identifier, URL, capability, and style-resource link helpers used by the
/// GeoServer import pipeline. These are pure-static and have no instance
/// dependencies; they live in their own partial so the main file can focus on
/// orchestration.
/// </summary>
internal sealed partial class GeoServerImportService
{
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
}
