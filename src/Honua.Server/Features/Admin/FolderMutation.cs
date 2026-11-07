// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

internal sealed record FolderMutation(MetadataV2Graph Graph, MetadataV2Folder? Folder, int Status = 200)
{
    internal IReadOnlyList<string> ServiceIds { get; init; } = [];

    internal static FolderMutation Create(MetadataV2Graph graph, FolderRequest request, string? tenant)
    {
        var folder = new MetadataV2Folder
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = Guid.NewGuid().ToString("N"), Name = request.Name ?? string.Empty,
                Title = request.Title, Tenant = tenant
            }
        };
        return new(MetadataV2FolderMutations.Create(graph, folder), folder, StatusCodes.Status201Created);
    }

    internal static FolderMutation Update(MetadataV2Graph graph, string id, FolderRequest request, string? tenant)
    {
        var folder = FindFolder(graph, id, tenant);
        var updated = folder with
        {
            Metadata = folder.Metadata with
            {
                Name = request.Name ?? folder.Metadata.Name,
                Title = request.TitleSpecified ? request.Title : folder.Metadata.Title
            }
        };
        return new(MetadataV2FolderMutations.Update(graph, updated), updated);
    }

    internal static FolderMutation Delete(MetadataV2Graph graph, string id, string? tenant)
    {
        var folder = FindFolder(graph, id, tenant);
        return new(MetadataV2FolderMutations.Delete(graph, id), folder, StatusCodes.Status204NoContent);
    }

    internal static FolderMutation Move(MetadataV2Graph graph, string serviceName, MetadataV2FolderSelection folderId, string? tenant)
    {
        var services = graph.Services.Where(service => string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (services.Length == 0 || services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, tenant)))
            throw new KeyNotFoundException();
        if (!folderId.IsSpecified) throw new ArgumentException("folderId is required; null selects root.");
        if (folderId.Value is not null) FindFolder(graph, folderId.Value, tenant);
        return new(MetadataV2FolderMutations.Move(graph, serviceName, folderId.Value), null, StatusCodes.Status204NoContent)
        {
            ServiceIds = services.Select(service => service.Metadata.Id).ToArray()
        };
    }

    internal MetadataV2Service[] AffectedServices(MetadataV2Graph original)
    {
        var changedFolders = original.Folders.Where(folder => !Graph.Folders.Any(candidate => SameFolder(folder, candidate)))
            .Select(folder => folder.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        return original.Services.Where(service => ServiceIds.Contains(service.Metadata.Id) ||
            !Graph.Services.Contains(service) || (service.FolderId is not null && changedFolders.Contains(service.FolderId))).ToArray();
    }

    private static MetadataV2Folder FindFolder(MetadataV2Graph graph, string id, string? tenant)
        => graph.Folders.FirstOrDefault(folder => !folder.Deleted && folder.Metadata.Id == id &&
            MetadataV2ServicePaths.SameTenant(folder.Metadata.Tenant, tenant)) ?? throw new KeyNotFoundException();

    private static bool SameFolder(MetadataV2Folder first, MetadataV2Folder second)
        => first.Metadata.Id == second.Metadata.Id && first.Metadata.Name == second.Metadata.Name &&
           first.Metadata.Title == second.Metadata.Title && MetadataV2ServicePaths.SameTenant(first.Metadata.Tenant, second.Metadata.Tenant) &&
           first.Deleted == second.Deleted && first.PreviousNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(second.PreviousNames);
}
