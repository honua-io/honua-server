// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed record CreateMetadataReleaseOperationRequest
{
    public string? PackageId { get; init; }

    public string? TargetEnvironment { get; init; }

    public string? DesiredRevision { get; init; }

    public string? ResourceSemanticId { get; init; }

    public string? NewFieldName { get; init; }

    public string? NewFieldType { get; init; }

    public string? DataPopulateWorkloadId { get; init; }

    // The release is rejected before any mutation unless every populated field is the new nullable field.
    public IReadOnlyList<string>? DataPopulateFields { get; init; }

    public string? ScriptId { get; init; }

    public string? Reason { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }

    public string? RootProvisioningOperationId { get; init; }
}
