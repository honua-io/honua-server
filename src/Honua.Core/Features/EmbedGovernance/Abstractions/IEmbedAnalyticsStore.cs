// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance.Abstractions;

/// <summary>
/// Ingestion and aggregation of redacted embed analytics events.
/// </summary>
public interface IEmbedAnalyticsStore
{
    /// <summary>Ingests a single validated, redacted analytics event.</summary>
    /// <param name="analyticsEvent">The event to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task IngestAsync(EmbedAnalyticsEvent analyticsEvent, CancellationToken cancellationToken);

    /// <summary>Aggregates recorded events for operator/Console reporting.</summary>
    /// <param name="query">Filter and grouping inputs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmbedUsageReport> QueryAsync(EmbedUsageQuery query, CancellationToken cancellationToken);
}
