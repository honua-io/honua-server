// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2FolderValidation
{
    public static IReadOnlyList<string> Validate(MetadataV2Graph graph)
    {
        var errors = new List<string>();
        ValidateFolders(graph, errors);
        ValidateMemberships(graph, errors);
        ValidatePaths(graph, errors);
        return errors;
    }

    private static void ValidateFolders(MetadataV2Graph graph, List<string> errors)
    {
        var folderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in graph.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Metadata.Id) || folder.Metadata.Id == "root" ||
                !folderIds.Add(folder.Metadata.Id)) errors.Add("Folder ids must be unique and cannot be 'root'.");
            if (Names(folder).Any(n => !MetadataV2ServicePaths.IsFolderName(n)))
                errors.Add("Folder names must be unreserved ASCII identifiers beginning with a letter.");
            var others = graph.Folders.Where(f => f.Metadata.Id != folder.Metadata.Id &&
                MetadataV2ServicePaths.ScopesOverlap(f.Metadata.Tenant, folder.Metadata.Tenant));
            if (others.Any(f => Names(f).Intersect(Names(folder), StringComparer.OrdinalIgnoreCase).Any()))
                errors.Add("Folder names and previous names must be unique across overlapping tenant scopes.");
        }
    }

    private static IEnumerable<string> Names(MetadataV2Folder folder)
        => folder.PreviousNames.Append(folder.Metadata.Name);

    private static void ValidateMemberships(MetadataV2Graph graph, List<string> errors)
    {
        foreach (var service in graph.Services.Where(s => s.FolderId is not null || s.PublicName is not null))
        {
            if (!MetadataV2ServicePaths.IsPublicName(service.EffectivePublicName, service.FolderId))
                errors.Add("Public service names must be unreserved ASCII identifiers of at most 64 characters.");
            if (service.FolderId is not null && !graph.Folders.Any(f => !f.Deleted &&
                f.Metadata.Id == service.FolderId &&
                MetadataV2ServicePaths.SameTenant(f.Metadata.Tenant, service.Metadata.Tenant)))
                errors.Add("Service membership must reference a live folder in the same tenant scope.");
            var siblings = graph.Services.Where(s => string.Equals(s.Metadata.Name, service.Metadata.Name,
                StringComparison.OrdinalIgnoreCase));
            if (siblings.Any(s => s.FolderId != service.FolderId ||
                s.EffectivePublicName != service.EffectivePublicName ||
                !MetadataV2ServicePaths.SameTenant(s.Metadata.Tenant, service.Metadata.Tenant)))
                errors.Add("All logical service facets must agree on folder, public name and tenant.");
        }
    }

    private static void ValidatePaths(MetadataV2Graph graph, List<string> errors)
    {
        if (graph.Services.Any(s => s.FolderId is not null && !graph.Folders.Any(f => f.Metadata.Id == s.FolderId))) return;
        var paths = MetadataV2ServicePaths.CanonicalPaths(graph);
        foreach (var path in paths)
        {
            var changed = graph.Services.Any(s => path.TargetFacetIds.Contains(s.Metadata.Id) &&
                (s.FolderId is not null || s.PublicName is not null));
            if (changed && paths.Any(p => !ReferenceEquals(p, path) && MetadataV2ServicePaths.SamePath(p, path)))
                errors.Add("Public service paths must be unique across overlapping tenant scopes.");
            if (RootLeafCollidesWithFolder(graph, path))
                errors.Add("Root public service names cannot occupy reserved folder names.");
            if (graph.ServicePathAliases.Any(a => MetadataV2ServicePaths.SamePath(a, path) &&
                (a.Retired || !a.TargetFacetIds.Intersect(path.TargetFacetIds, StringComparer.Ordinal).Any())))
                errors.Add("A public service path cannot reuse a reservation from another service incarnation.");
        }
        ValidateAliases(graph, errors);
    }

    private static bool RootLeafCollidesWithFolder(MetadataV2Graph graph, MetadataV2ServicePathAlias path)
        => path.FolderName is null && graph.Folders.Any(folder =>
            MetadataV2ServicePaths.ScopesOverlap(folder.Metadata.Tenant, path.Tenant) &&
            Names(folder).Contains(path.PublicName, StringComparer.OrdinalIgnoreCase));

    private static void ValidateAliases(MetadataV2Graph graph, List<string> errors)
    {
        foreach (var alias in graph.ServicePathAliases)
        {
            var targets = graph.Services.Where(s => alias.TargetFacetIds.Contains(s.Metadata.Id, StringComparer.Ordinal)).ToArray();
            if (targets.Select(s => s.Metadata.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
                targets.Any(s => !MetadataV2ServicePaths.SameTenant(s.Metadata.Tenant, alias.Tenant)))
                errors.Add("Path reservation targets must belong to one logical service in the same tenant scope.");
            if (alias.TargetFacetIds.Count == 0 || alias.TargetFacetIds.Distinct(StringComparer.Ordinal).Count() != alias.TargetFacetIds.Count)
                errors.Add("Path reservations require distinct original facet ids.");
            if (graph.ServicePathAliases.Any(a => !ReferenceEquals(a, alias) && MetadataV2ServicePaths.SamePath(a, alias)))
                errors.Add("Path reservations must be unique across overlapping tenant scopes.");
            if (RootLeafCollidesWithFolder(graph, alias))
                errors.Add("Folder names cannot reuse historical root service paths.");
        }
    }
}
