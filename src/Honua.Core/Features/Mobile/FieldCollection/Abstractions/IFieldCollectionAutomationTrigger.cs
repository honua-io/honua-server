// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

/// <summary>
/// Post-push hook that evaluates and enqueues server-side automation actions for
/// an applied FieldCollection change (#2121). Invoked from the sync push endpoint
/// only after a change is durably applied. Implementations are best-effort: a
/// failure to evaluate or enqueue actions must never fail the push itself.
/// </summary>
public interface IFieldCollectionAutomationTrigger
{
    /// <summary>
    /// Evaluates configured actions against <paramref name="automationEvent"/> and
    /// enqueues every match for background delivery.
    /// </summary>
    ValueTask OnChangeAppliedAsync(
        FieldCollectionAutomationEvent automationEvent,
        CancellationToken cancellationToken = default);
}
