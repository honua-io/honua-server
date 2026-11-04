// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2ServicePaths
{
    public static IReadOnlySet<string> ServiceTypes { get; } = new HashSet<string>(
        ["FeatureServer", "MapServer", "ImageServer", "GPServer", "VectorTileServer",
         "SceneServer", "GeocodeServer", "NAServer", "VersionManagementServer"], StringComparer.OrdinalIgnoreCase);

    public static string? NormalizeTenant(string? tenant) => string.IsNullOrWhiteSpace(tenant) ? null : tenant;

    public static bool ScopesOverlap(string? first, string? second)
        => NormalizeTenant(first) is null || NormalizeTenant(second) is null || first == second;

    public static bool IsIdentifier(string name)
        => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool IsFolderName(string name)
        => IsIdentifier(name) && char.IsAsciiLetter(name[0]) && !IsReserved(name);

    public static bool IsReserved(string name)
        => ServiceTypes.Contains(name) || string.Equals(name, "Utilities", StringComparison.OrdinalIgnoreCase);

    public static string? FolderName(MetadataV2Graph graph, MetadataV2Service service)
        => service.FolderId is null ? null : graph.Folders.Single(f => f.Metadata.Id == service.FolderId).Metadata.Name;

    public static string QualifiedName(MetadataV2Graph graph, MetadataV2Service service)
        => FolderName(graph, service) is { } folder ? $"{folder}/{service.EffectivePublicName}" : service.EffectivePublicName;

    public static bool SamePath(MetadataV2ServicePathAlias first, MetadataV2ServicePathAlias second)
        => ScopesOverlap(first.Tenant, second.Tenant) &&
           string.Equals(first.FolderName, second.FolderName, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(first.PublicName, second.PublicName, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MetadataV2ServicePathAlias> CanonicalPaths(MetadataV2Graph graph)
        => graph.Services.GroupBy(s => (s.Metadata.Tenant, s.Metadata.Name))
            .Select(group => new MetadataV2ServicePathAlias
            {
                Tenant = NormalizeTenant(group.Key.Tenant),
                FolderName = FolderName(graph, group.First()),
                PublicName = group.First().EffectivePublicName,
                TargetFacetIds = group.Select(s => s.Metadata.Id).Order(StringComparer.Ordinal).ToArray()
            }).ToArray();

    public static IReadOnlyList<MetadataV2Service> Resolve(
        MetadataV2Graph graph, MetadataV2ServicePathAlias path)
    {
        var canonical = CanonicalPaths(graph).Where(candidate => SamePath(candidate, path)).ToArray();
        var aliases = graph.ServicePathAliases.Where(candidate => SamePath(candidate, path) &&
            MetadataV2TenantVisibility.IsVisibleToTenant(candidate.Tenant, path.Tenant)).ToArray();
        if (aliases.Any(a => a.Retired)) return [];
        var matches = canonical.Concat(aliases).Where(candidate =>
            MetadataV2TenantVisibility.IsVisibleToTenant(candidate.Tenant, path.Tenant)).ToArray();
        var ids = matches.SelectMany(a => a.TargetFacetIds).ToHashSet(StringComparer.Ordinal);
        return graph.Services.Where(s => ids.Contains(s.Metadata.Id) &&
            MetadataV2TenantVisibility.IsVisibleToTenant(s.Metadata.Tenant, path.Tenant)).ToArray();
    }
}
