// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Unified read-side feed of normalized operational events for the Console
/// Operate view (#1168).
/// </summary>
/// <remarks>
/// Implementations fan out across one or more upstream stores (alerts, audit,
/// jobs, releases, sync conflicts, temporal ops, logs) and merge results into
/// a single timeline ordered newest-first.
/// </remarks>
public interface IOperateEventFeed
{
    /// <summary>
    /// Returns a merged page of events from all enabled sources, ordered
    /// newest-first by <c>OccurredAt</c>.
    /// </summary>
    /// <param name="filter">Query criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperateEventPage> ListAsync(OperateEventFilter filter, CancellationToken cancellationToken = default);
}
