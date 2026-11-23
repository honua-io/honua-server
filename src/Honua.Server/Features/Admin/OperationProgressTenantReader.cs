// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Progress;

namespace Honua.Server.Features.Admin;

internal static class OperationProgressTenantReader
{
    // Cross-tenant administrators see every operation; other administrators see only their tenant's,
    // and with tenancy enabled unowned operations are hidden. Each listed operation is repaired from
    // its durable job before the tenant check and the caller's filter.
    internal static async Task<List<IOperationProgress>> ReadVisibleOperationsAsync(
        HttpContext context,
        OperationType? operationType,
        CancellationToken cancellationToken)
    {
        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var reader = new ExecutionJobProgressReader(progressStore, context.RequestServices.GetService<IExecutionJobStore>());
        var operationIds = await progressStore.GetActiveOperationIdsAsync(operationType, cancellationToken).ConfigureAwait(false);
        var visible = new List<IOperationProgress>(operationIds.Count);
        await foreach (var stored in ProgressStoreReader.ReadAvailableAsync(progressStore, operationIds, cancellationToken))
        {
            var progress = await reader.RepairAsync(stored, cancellationToken).ConfigureAwait(false);
            if (await OperationTenantAuthorization.CanAccessOperationAsync(context, progress, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(progress);
            }
        }

        return visible;
    }
}
