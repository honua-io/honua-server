// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2FolderMutations
{
    public static MetadataV2Graph Create(MetadataV2Graph graph, MetadataV2Folder folder)
    {
        ValidateName(folder.Metadata.Name);
        return MetadataV2PathReservations.Reconcile(graph, graph with { Folders = [.. graph.Folders, folder] });
    }

    public static MetadataV2Graph Update(MetadataV2Graph graph, MetadataV2Folder folder)
    {
        ValidateName(folder.Metadata.Name);
        var candidate = graph with
        {
            Folders = graph.Folders.Select(f => f.Metadata.Id == folder.Metadata.Id ? folder : f).ToArray()
        };
        return MetadataV2PathReservations.Reconcile(graph, candidate);
    }

    public static MetadataV2Graph Delete(MetadataV2Graph graph, string folderId)
    {
        if (graph.Services.Any(s => s.FolderId == folderId))
            throw new InvalidOperationException("Only empty folders can be deleted. Move their services first.");
        var candidate = graph with
        {
            Folders = graph.Folders.Select(f => f.Metadata.Id == folderId ? f with { Deleted = true } : f).ToArray()
        };
        return MetadataV2PathReservations.Reconcile(graph, candidate);
    }

    private static void ValidateName(string name)
    {
        if (!MetadataV2ServicePaths.IsFolderName(name))
            throw new ArgumentException("Folder name must be an unreserved ASCII identifier beginning with a letter.");
    }

    public static MetadataV2Graph Move(MetadataV2Graph graph, string serviceName, string? folderId)
    {
        var candidate = graph with
        {
            Services = graph.Services.Select(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase)
                ? s with { FolderId = folderId } : s).ToArray()
        };
        return MetadataV2PathReservations.Reconcile(graph, candidate);
    }
}
