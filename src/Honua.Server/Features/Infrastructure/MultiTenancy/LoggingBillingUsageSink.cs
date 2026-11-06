// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Billing;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.MultiTenancy;

internal sealed partial class LoggingBillingUsageSink : IBillingUsageSink
{
    private readonly ILogger<LoggingBillingUsageSink> _logger;

    public LoggingBillingUsageSink(ILogger<LoggingBillingUsageSink> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(IReadOnlyList<BillingUsageRecord> records, CancellationToken cancellationToken = default)
    {
        if (records is null || records.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var record in records)
        {
            Log.BillingUsagePublished(_logger, record.TenantId, record.RequestCount, record.CapturedAt);
        }

        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4560, Level = LogLevel.Information,
            Message = "Billing usage for tenant '{TenantId}': {RequestCount} requests captured at {CapturedAt:O}")]
        public static partial void BillingUsagePublished(ILogger logger, string tenantId, long requestCount, DateTimeOffset capturedAt);
    }
}
