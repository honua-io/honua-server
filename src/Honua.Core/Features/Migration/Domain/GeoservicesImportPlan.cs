// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesImportPlan
{
    public required string ServiceUrl { get; init; }
    public required string ServiceName { get; init; }
    public required string ServiceId { get; init; }
    public required string PublicName { get; init; }
    public bool ExistingService { get; init; }
    public string? FolderId { get; init; }
    public string? FolderName { get; init; }
    public string? Namespace { get; init; }

    public GeoservicesImportTarget BindTenant(string? tenant) => new()
    {
        ServiceName = ServiceName, ServiceId = ServiceId, PublicName = PublicName,
        ExistingService = ExistingService, FolderId = FolderId, FolderName = FolderName,
        Namespace = Namespace, TenantId = tenant
    };
}
