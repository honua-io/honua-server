// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Core.Features.MultiTenancy.Billing;

/// <summary>
/// Maps the per-tenant usage-metering snapshot onto billing usage records and publishes them to
/// the configured <see cref="IBillingUsageSink"/> (issue #2156). This is the metering-to-billing
/// attribution path: each <see cref="TenantUsageSnapshot"/> becomes one
/// <see cref="BillingUsageRecord"/> keyed by the same tenant id, so usage is never cross-attributed.
/// </summary>
public sealed class TenantBillingExporter
{
    private readonly ITenantUsageMeter _usageMeter;
    private readonly IBillingUsageSink _sink;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantBillingExporter"/> class.
    /// </summary>
    /// <param name="usageMeter">The usage-metering seam to read attributed usage from.</param>
    /// <param name="sink">The billing sink to publish records to.</param>
    /// <param name="timeProvider">Clock used to stamp the capture time; defaults to the system clock.</param>
    public TenantBillingExporter(
        ITenantUsageMeter usageMeter,
        IBillingUsageSink sink,
        TimeProvider? timeProvider = null)
    {
        _usageMeter = usageMeter;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds the current per-tenant billing records from the usage snapshot. Pure with respect to
    /// the sink so it can be inspected (and unit tested) before publishing.
    /// </summary>
    public IReadOnlyList<BillingUsageRecord> BuildRecords()
    {
        var capturedAt = _timeProvider.GetUtcNow();
        var snapshot = _usageMeter.Snapshot();
        var records = new List<BillingUsageRecord>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            records.Add(new BillingUsageRecord(entry.TenantId, entry.RequestCount, capturedAt));
        }

        return records;
    }

    /// <summary>
    /// Builds the current per-tenant billing records and publishes them to the sink, returning the
    /// published records for inspection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<BillingUsageRecord>> ExportAsync(CancellationToken cancellationToken = default)
    {
        var records = BuildRecords();
        await _sink.PublishAsync(records, cancellationToken).ConfigureAwait(false);
        return records;
    }
}
