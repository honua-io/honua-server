// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Billing;

namespace Honua.Core.Tests.Features.MultiTenancy;

/// <summary>
/// Unit tests for the metering-to-billing attribution path (issue #2156): each tenant's metered
/// usage must map to a billing record keyed by the same tenant id.
/// </summary>
public sealed class TenantBillingExporterTests
{
    [Fact]
    public async Task ExportAsync_AttributesUsageToTheCorrectTenant()
    {
        var meter = new StubUsageMeter(
            new TenantUsageSnapshot("acme", 42),
            new TenantUsageSnapshot("globex", 7));
        var sink = new CapturingBillingSink();
        var exporter = new TenantBillingExporter(meter, sink);

        var records = await exporter.ExportAsync();

        records.Should().HaveCount(2);
        records.Single(r => r.TenantId == "acme").RequestCount.Should().Be(42);
        records.Single(r => r.TenantId == "globex").RequestCount.Should().Be(7);

        sink.Published.Should().HaveCount(1);
        sink.Published[0].Should().BeEquivalentTo(records);
    }

    [Fact]
    public async Task ExportAsync_NoUsage_PublishesEmptyBatch()
    {
        var exporter = new TenantBillingExporter(new StubUsageMeter(), new CapturingBillingSink());

        var records = await exporter.ExportAsync();

        records.Should().BeEmpty();
    }

    private sealed class StubUsageMeter(params TenantUsageSnapshot[] snapshots) : ITenantUsageMeter
    {
        private readonly IReadOnlyList<TenantUsageSnapshot> _snapshots = snapshots;

        public long RecordRequest(string tenantId) => 0;

        public IReadOnlyList<TenantUsageSnapshot> Snapshot() => _snapshots;
    }

    private sealed class CapturingBillingSink : IBillingUsageSink
    {
        public List<IReadOnlyList<BillingUsageRecord>> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<BillingUsageRecord> records, CancellationToken cancellationToken = default)
        {
            Published.Add(records);
            return Task.CompletedTask;
        }
    }
}
