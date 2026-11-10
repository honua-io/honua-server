// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesImportTarget
{
    public required string ServiceName { get; init; }

    public string? ServiceId { get; init; }

    public bool ExistingService { get; init; }

    public required string PublicName { get; init; }

    public string? FolderId { get; init; }

    public string? FolderName { get; init; }

    public string? Namespace { get; init; }

    public string? TenantId { get; init; }

    public TenantContextSource TenantSource { get; init; }

    public GeoservicesImportPlan ToPlan(string serviceUrl) => new()
    {
        ServiceUrl = serviceUrl,
        ServiceName = ServiceName,
        ServiceId = ServiceId ?? throw new InvalidOperationException("The import target has no bound service identity."),
        PublicName = PublicName,
        ExistingService = ExistingService,
        FolderId = FolderId,
        FolderName = FolderName,
        Namespace = Namespace
    };

    public MetadataV2Folder? CreateFolderDefinition() => FolderName is null ? null : new()
    {
        Metadata = new() { Id = FolderId!, Name = FolderName, Title = FolderName, Tenant = TenantId }
    };

    public MetadataV2Graph PrepareFolder(MetadataV2Graph graph)
    {
        if ((FolderId is null) != (FolderName is null))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "The import folder identity is incomplete.");
        if (CreateFolderDefinition() is not { } folder) return graph;
        if (!MetadataV2ServicePaths.IsFolderName(folder.Metadata.Name))
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "The planned folder name is invalid. Select an explicit target mapping.");
        var existing = graph.Folders.FirstOrDefault(candidate => candidate.Metadata.Id == FolderId);
        if (existing is not null)
        {
            if (existing.Deleted || !MetadataV2ServicePaths.SameTenant(existing.Metadata.Tenant, TenantId) ||
                !string.Equals(existing.Metadata.Name, FolderName, StringComparison.OrdinalIgnoreCase))
                throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The planned import folder changed. Select a new target mapping.");
            return graph;
        }
        try
        {
            return MetadataV2FolderMutations.Create(graph, folder);
        }
        catch (InvalidOperationException)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict, "The planned import folder is reserved. Select a new target mapping.");
        }
    }

    public MetadataV2Service[] ValidateServiceIdentity(MetadataV2Graph graph)
    {
        var services = graph.Services.Where(service =>
            string.Equals(service.Metadata.Name, ServiceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (string.IsNullOrWhiteSpace(ServiceId) ||
            (services.Length > 0 && !services.Any(service => service.Metadata.Id == ServiceId)) ||
            (services.Length == 0 && (ExistingService || graph.ServicePathAliases.Any(alias => alias.TargetFacetIds.Contains(ServiceId)))) ||
            graph.Services.Any(service => service.Metadata.Id == ServiceId &&
                !string.Equals(service.Metadata.Name, ServiceName, StringComparison.OrdinalIgnoreCase)) ||
            services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, TenantId) ||
                service.Metadata.Namespace != Namespace || service.FolderId != FolderId || service.EffectivePublicName != PublicName))
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                "The planned import service identity or scope changed. Select a new target mapping.");
        return services;
    }
}
