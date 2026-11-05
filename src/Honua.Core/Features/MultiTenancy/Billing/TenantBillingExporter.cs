// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Core.Features.MultiTenancy.Billing;

public sealed class TenantBillingExporter
{
    private readonly ITenantUsageMeter _usageMeter;
    private readonly IBillingUsageSink _sink;
    private readonly TimeProvider _timeProvider;

    public TenantBillingExporter(
        ITenantUsageMeter usageMeter,
        IBillingUsageSink sink,
        TimeProvider? timeProvider = null)
    {
        _usageMeter = usageMeter;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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

    public async Task<IReadOnlyList<BillingUsageRecord>> ExportAsync(CancellationToken cancellationToken = default)
    {
        var records = BuildRecords();
        await _sink.PublishAsync(records, cancellationToken).ConfigureAwait(false);
        return records;
    }
}
