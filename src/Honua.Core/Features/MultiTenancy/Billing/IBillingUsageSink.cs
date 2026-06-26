// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Billing;

/// <summary>
/// A single per-tenant usage measurement ready for billing attribution (issue #2156).
/// </summary>
/// <param name="TenantId">The tenant the usage is attributed to.</param>
/// <param name="RequestCount">Metered billable request count for the tenant.</param>
/// <param name="CapturedAt">When the measurement snapshot was taken (UTC).</param>
public readonly record struct BillingUsageRecord(string TenantId, long RequestCount, DateTimeOffset CapturedAt);

/// <summary>
/// Export sink for per-tenant billing usage records. Wires the existing usage-metering seam
/// (<see cref="Abstractions.ITenantUsageMeter"/>) to an external metering/billing pipeline.
/// </summary>
/// <remarks>
/// The shipped default sink logs records; cloud-marketplace metering connectors implement this
/// interface to forward usage to the billing provider. Implementations should be best-effort and
/// must not throw into the caller's critical path.
/// </remarks>
public interface IBillingUsageSink
{
    /// <summary>Publishes a batch of per-tenant usage records to the billing pipeline.</summary>
    /// <param name="records">The usage records to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(IReadOnlyList<BillingUsageRecord> records, CancellationToken cancellationToken = default);
}
