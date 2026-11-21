// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

internal sealed record FolderMutation(MetadataV2Graph Graph, MetadataV2Folder? Folder, int Status = 200)
{
    internal IReadOnlyList<string> ServiceIds { get; init; } = [];

    internal string? MovedServiceName { get; init; }

    internal IReadOnlyList<ServiceFolderAssignment> PriorAssignments { get; init; } = [];

    internal static FolderMutation Create(MetadataV2Graph graph, FolderMutationCommand.Create command, string? tenant)
    {
        var folder = new MetadataV2Folder
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = command.Name ?? string.Empty,
                Title = command.Title.Value,
                Tenant = tenant
            }
        };
        return new(MetadataV2FolderMutations.Create(graph, folder), folder, StatusCodes.Status201Created);
    }

    internal static FolderMutation Update(MetadataV2Graph graph, FolderMutationCommand.Update command, string? tenant)
    {
        var folder = FindFolder(graph, command.Id, tenant);
        var updated = folder with
        {
            Metadata = folder.Metadata with
            {
                Name = command.Name ?? folder.Metadata.Name,
                Title = command.Title.IsSpecified ? command.Title.Value : folder.Metadata.Title
            }
        };
        return new(MetadataV2FolderMutations.Update(graph, updated), updated);
    }

    internal static FolderMutation Delete(MetadataV2Graph graph, string id, string? tenant)
    {
        var folder = FindFolder(graph, id, tenant);
        return new(MetadataV2FolderMutations.Delete(graph, id), folder, StatusCodes.Status204NoContent);
    }

    internal static FolderMutation Move(MetadataV2Graph graph, FolderMutationCommand.Move command, string? tenant)
    {
        var services = graph.Services.Where(service => string.Equals(service.Metadata.Name, command.ServiceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (services.Length == 0 || services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, tenant)))
            throw new KeyNotFoundException();
        if (!command.FolderId.IsSpecified) throw new ArgumentException("folderId is required; null selects root.");
        if (command.FolderId.Value is not null) FindFolder(graph, command.FolderId.Value, tenant);
        var prior = services.Select(service => new ServiceFolderAssignment(service.Metadata.Id, service.FolderId)).ToArray();
        return new(MetadataV2FolderMutations.Move(graph, command.ServiceName, command.FolderId.Value), null, StatusCodes.Status204NoContent)
        {
            ServiceIds = prior.Select(assignment => assignment.ServiceId).ToArray(),
            MovedServiceName = command.ServiceName,
            PriorAssignments = prior
        };
    }

    internal static FolderMutation Restore(MetadataV2Graph graph, FolderMutationCommand.Restore command, string? tenant)
    {
        if (command.Assignments.Count == 0)
            throw new ArgumentException("A service move restore requires the recorded prior assignments.");
        var services = command.Assignments.Select(assignment => graph.Services.FirstOrDefault(service => service.Metadata.Id == assignment.ServiceId)
            ?? throw new KeyNotFoundException()).ToArray();
        if (services.Any(service => !string.Equals(service.Metadata.Name, command.ServiceName, StringComparison.OrdinalIgnoreCase) ||
            !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, tenant)))
            throw new KeyNotFoundException();
        foreach (var assignment in command.Assignments)
        {
            if (assignment.PriorFolderId is not null) FindFolder(graph, assignment.PriorFolderId, tenant);
        }

        var restored = MetadataV2FolderMutations.AssignFolders(graph, command.Assignments
            .Select(assignment => new MetadataV2FolderAssignment(assignment.ServiceId, assignment.PriorFolderId)).ToArray());
        return new(restored, null, StatusCodes.Status204NoContent)
        {
            ServiceIds = command.Assignments.Select(assignment => assignment.ServiceId).ToArray(),
            MovedServiceName = command.ServiceName
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
