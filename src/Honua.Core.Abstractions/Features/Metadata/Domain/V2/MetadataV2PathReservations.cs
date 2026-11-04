// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2PathReservations
{
    public static MetadataV2Graph Reconcile(MetadataV2Graph previous, MetadataV2Graph candidate)
    {
        if (!HasOrganization(previous) && !HasOrganization(candidate)) return candidate;
        ValidatePublicNames(previous, candidate);
        candidate = candidate with { Folders = ReconcileFolders(previous, candidate) };
        var aliases = previous.ServicePathAliases.ToList();
        foreach (var path in MetadataV2ServicePaths.CanonicalPaths(previous)) AddPath(aliases, path);
        foreach (var path in candidate.ServicePathAliases) AddPath(aliases, path);
        foreach (var path in MetadataV2ServicePaths.CanonicalPaths(candidate)) AddPath(aliases, path);
        var liveIds = candidate.Services.Select(s => s.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        candidate = candidate with
        {
            ServicePathAliases = aliases.Select(a => a with
            {
                Retired = a.Retired || !a.TargetFacetIds.Any(liveIds.Contains)
            }).ToArray()
        };
        var errors = MetadataV2FolderValidation.Validate(candidate);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        return candidate;
    }

    public static void EnsureActivationPreservesReservations(MetadataV2Graph current, MetadataV2Graph target)
    {
        if (!HasOrganization(current) && !HasOrganization(target)) return;
        MetadataV2Graph reconciled;
        try
        {
            reconciled = Reconcile(current, target);
        }
        catch (InvalidOperationException)
        {
            throw new MetadataV2PathReservationException();
        }
        var aliasesPreserved = reconciled.ServicePathAliases.All(expected => target.ServicePathAliases.Any(actual =>
            MetadataV2ServicePaths.SamePath(expected, actual) && MetadataV2ServicePaths.SameTenant(expected.Tenant, actual.Tenant) &&
            expected.Retired == actual.Retired && expected.TargetFacetIds.ToHashSet(StringComparer.Ordinal).SetEquals(actual.TargetFacetIds)));
        var foldersPreserved = reconciled.Folders.All(expected => target.Folders.Any(actual =>
            expected.Metadata.Id == actual.Metadata.Id && expected.Deleted == actual.Deleted &&
            expected.PreviousNames.Append(expected.Metadata.Name).All(name =>
                actual.PreviousNames.Append(actual.Metadata.Name).Contains(name, StringComparer.OrdinalIgnoreCase))));
        if (!aliasesPreserved || !foldersPreserved)
            throw new MetadataV2PathReservationException();
    }

    private static bool HasOrganization(MetadataV2Graph graph)
        => graph.Folders.Count > 0 || graph.ServicePathAliases.Count > 0 ||
           graph.Services.Any(s => s.PublicName is not null || s.FolderId is not null);

    private static void ValidatePublicNames(MetadataV2Graph previous, MetadataV2Graph candidate)
    {
        foreach (var service in previous.Services)
        {
            var replacement = candidate.Services.FirstOrDefault(s => s.Metadata.Id == service.Metadata.Id);
            if (replacement is not null && (replacement.EffectivePublicName != service.EffectivePublicName ||
                replacement.Metadata.Name != service.Metadata.Name ||
                MetadataV2ServicePaths.NormalizeTenant(replacement.Metadata.Tenant) != MetadataV2ServicePaths.NormalizeTenant(service.Metadata.Tenant)))
                throw new InvalidOperationException("Service public names, logical identities and tenants are immutable.");
        }
    }

    private static List<MetadataV2Folder> ReconcileFolders(MetadataV2Graph previous, MetadataV2Graph candidate)
    {
        var folders = candidate.Folders.ToList();
        foreach (var oldFolder in previous.Folders)
        {
            var index = folders.FindIndex(f => f.Metadata.Id == oldFolder.Metadata.Id);
            if (index < 0)
            {
                folders.Add(oldFolder with { Deleted = true });
                continue;
            }
            var folder = folders[index];
            if (MetadataV2ServicePaths.NormalizeTenant(oldFolder.Metadata.Tenant) != MetadataV2ServicePaths.NormalizeTenant(folder.Metadata.Tenant) ||
                (oldFolder.Deleted && !folder.Deleted))
                throw new InvalidOperationException("Folder tenants are immutable and deleted folder ids cannot be reused.");
            folders[index] = folder with
            {
                PreviousNames = oldFolder.PreviousNames.Append(oldFolder.Metadata.Name)
                    .Concat(folder.PreviousNames).Where(n => !string.Equals(n, folder.Metadata.Name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }
        return folders;
    }

    private static void AddPath(List<MetadataV2ServicePathAlias> aliases, MetadataV2ServicePathAlias path)
    {
        var index = aliases.FindIndex(a => MetadataV2ServicePaths.SamePath(a, path));
        if (index < 0)
        {
            aliases.Add(path);
            return;
        }
        var reserved = aliases[index];
        if (!MetadataV2ServicePaths.SameTenant(reserved.Tenant, path.Tenant) ||
            !reserved.TargetFacetIds.Intersect(path.TargetFacetIds, StringComparer.Ordinal).Any() ||
            (reserved.Retired && !path.Retired))
            throw new InvalidOperationException("A permanent public path reservation cannot be rebound or reactivated.");
        aliases[index] = reserved with
        {
            TargetFacetIds = reserved.TargetFacetIds.Concat(path.TargetFacetIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Retired = reserved.Retired || path.Retired
        };
    }
}
