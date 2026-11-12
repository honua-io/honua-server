// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;

namespace Honua.ControlPlane;

// After a release is promoted or rolled back the CDN may hold the other release's responses. The operation is already terminal when this runs, so a failed invalidation is recorded on it and cannot block it.
internal sealed class CdnInvalidationTransitionListener(
    CdnInvalidationService invalidation,
    IServiceProvider services) : IWorkflowOperationTransitionListener
{
    internal const string WarningPrefix = "CDN invalidation failed";

    // One operation can be promoted and then rolled back; each changes what is served, and each is invalidated once.
    private readonly ConcurrentDictionary<(string OperationId, WorkflowOperationTransitionKind Kind), byte> _invalidated = new();

    public async Task OnTransitionAsync(WorkflowOperationTransition transition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!ChangesWhatIsServed(transition) || !_invalidated.TryAdd((transition.OperationId, transition.Kind), 0))
        {
            return;
        }

        var result = await invalidation.InvalidateNowAsync(
            CdnPaths.Everything, cancellationToken, CdnPurgeCause.Operation(transition.OperationId)).ConfigureAwait(false);
        if (result.Failed)
        {
            await RecordWarningAsync(transition.Operation, result, cancellationToken).ConfigureAwait(false);
        }
    }

    // A rollback that is only requested still serves the release being rolled back.
    private static bool ChangesWhatIsServed(WorkflowOperationTransition transition)
        => transition.Kind is WorkflowOperationTransitionKind.Promoted
           || transition.Operation.Status is WorkflowOperationStatus.RolledBack;

    // The store is resolved here because it is built from its listeners. Writing the warning raises this transition again, which the first-time check above ignores.
    private async Task RecordWarningAsync(
        WorkflowOperationRecord operation, CdnInvalidationResult result, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IWorkflowOperationStore>();
        var warning = $"{WarningPrefix} ({result.Detail}); cached responses expire with their lifetime, or purge the CDN.";
        await store.TrySetAsync(
            operation with { Warnings = [.. operation.Warnings, warning] },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
