// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Billing;

public readonly record struct BillingUsageRecord(string TenantId, long RequestCount, DateTimeOffset CapturedAt);

// A sink failure must not throw on the caller's path.
public interface IBillingUsageSink
{
    Task PublishAsync(IReadOnlyList<BillingUsageRecord> records, CancellationToken cancellationToken = default);
}
