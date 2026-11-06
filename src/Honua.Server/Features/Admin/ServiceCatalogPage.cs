// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static class ServiceCatalogPage
{
    internal static bool FolderIsMissing(MetadataV2GraphSnapshot snapshot, string folderId, string? tenantId)
    {
        if (folderId.Length == 0 || folderId == "root")
        {
            return false;
        }

        return !snapshot.Graph.Folders.Any(folder =>
            !folder.Deleted
            && folder.Metadata.Id == folderId
            && MetadataV2TenantVisibility.IsVisibleToTenant(folder.Metadata.Tenant, tenantId));
    }

    internal static ServiceListPage Create(MetadataV2GraphSnapshot snapshot, AdminListQuery query, string? tenantId = null)
    {
        var matches = MatchingServices(snapshot, query, tenantId)
            .GroupBy(service => service.Metadata.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToSummary(snapshot, group))
            .ToList();
        matches.Sort(static (left, right) => string.Compare(
            left.ServiceName, right.ServiceName, StringComparison.OrdinalIgnoreCase));

        return new ServiceListPage
        {
            Items = TakePage(matches, query),
            Page = query.Page,
            PageSize = query.PageSize,
            Total = matches.Count,
            LayerTotal = matches.Sum(match => match.LayerCount),
        };
    }

    private static IEnumerable<MetadataV2Service> MatchingServices(
        MetadataV2GraphSnapshot snapshot,
        AdminListQuery query,
        string? tenantId)
    {
        var services = snapshot.Graph.Services.Where(service =>
            MetadataV2TenantVisibility.IsVisibleToTenant(service.Metadata.Tenant, tenantId));
        if (query.FolderId.Length > 0)
        {
            services = query.FolderId == "root"
                ? services.Where(service => service.FolderId is null)
                : services.Where(service => service.FolderId == query.FolderId);
        }

        if (string.IsNullOrEmpty(query.NameQuery))
        {
            return services;
        }

        return services.Where(service =>
            service.Metadata.Name.Contains(query.NameQuery, StringComparison.OrdinalIgnoreCase));
    }

    // Protocol entries for one logical service share Metadata.Name.
    private static ServiceSummary ToSummary(
        MetadataV2GraphSnapshot snapshot,
        IGrouping<string, MetadataV2Service> sameName)
    {
        var representative = sameName.First();
        var enabled = sameName
            .SelectMany(service => service.Protocols)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var layerCount = sameName
            .SelectMany(service => snapshot.Index.PublicationsByService[service.Metadata.Id])
            .Select(publication => publication.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return new ServiceSummary
        {
            ServiceName = representative.Metadata.Name,
            FolderMutationIssues = MetadataV2FolderPreflight.Inspect(snapshot.Graph, representative),
            FolderId = representative.FolderId,
            FolderName = MetadataV2ServicePaths.FolderName(snapshot.Graph, representative),
            PublicName = representative.EffectivePublicName,
            Title = representative.Metadata.Title,
            Description = representative.Metadata.Description ?? string.Empty,
            LayerCount = layerCount,
            EnabledProtocols = enabled.Length == 0 ? ServiceProtocols.All : enabled,
        };
    }

    private static ServiceSummary[] TakePage(List<ServiceSummary> matches, AdminListQuery query)
    {
        var start = (query.Page - 1) * query.PageSize;
        if (start >= matches.Count)
        {
            return [];
        }

        return matches.Skip(start).Take(query.PageSize).ToArray();
    }
}
