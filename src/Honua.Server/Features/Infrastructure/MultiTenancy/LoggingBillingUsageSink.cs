// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Billing;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Default <see cref="IBillingUsageSink"/> that records per-tenant usage to structured logs
/// (issue #2156). Cloud-marketplace metering connectors replace this with a sink that forwards
/// usage to the billing provider. Best-effort: never throws into the caller's path.
/// </summary>
internal sealed partial class LoggingBillingUsageSink : IBillingUsageSink
{
    private readonly ILogger<LoggingBillingUsageSink> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingBillingUsageSink"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
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
