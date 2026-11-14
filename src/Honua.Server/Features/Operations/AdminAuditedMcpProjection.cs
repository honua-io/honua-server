// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Server.Features.Operations;

internal sealed class AdminAuditedMcpProjection : IAuditedAdminMcpProjection
{
    private static readonly HashSet<string> InspectionOperationIds = new(StringComparer.Ordinal)
    {
        "admin.import.formats",
        "admin.import.limits",
        "admin.import.preview",
        "admin.import.preview-url",
        "admin.connections.tables.validate",
    };

    public IReadOnlyCollection<string> OperationIds { get; } = AdminApiOperationCatalog.Descriptors
        .Concat(AdminAccessOperationCatalog.Descriptors)
        .Concat(AdminConnectImportOperationCatalog.Descriptors.Where(descriptor =>
            InspectionOperationIds.Contains(descriptor.OperationId)
            && descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly))
        .Select(descriptor => descriptor.OperationId)
        .Where(operationId => !AdminMcpOperationExclusions.ContainsOperation(operationId))
        .ToHashSet(StringComparer.Ordinal);
}
