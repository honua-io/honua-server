// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed class SupportTelemetrySnapshotCollector(
    IServerInstanceIdentity instanceIdentity,
    RecentErrorBuffer recentErrorBuffer,
    IOptionsMonitor<SupportTelemetryPushOptions> optionsMonitor,
    IServiceScopeFactory scopeFactory)
{
    public async Task<SupportTelemetrySnapshotPayload> CollectAsync(CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var configuration = sp.GetService<IConfiguration>();
        var migrationState = sp.GetService<MigrationState>();
        var readiness = await CheckReadinessSafeAsync(sp, cancellationToken).ConfigureAwait(false);
        var migrationLabel = migrationState is null
            ? "unknown"
            : DeployPreflightProbe.GetMigrationStatusLabel(migrationState.Status);

        var overallStatus = readiness
            ? "healthy"
            : migrationState?.IsFailed == true
                ? "unhealthy"
                : migrationState is not null && (migrationState.IsRunning || !migrationState.IsReady)
                    ? "initializing"
                    : "unhealthy";

        var health = new SupportTelemetryHealthPayload
        {
            Readiness = readiness ? "healthy" : "unhealthy",
            Liveness = "healthy",
            Version = ServerArtifactMetadata.GetServerVersion(typeof(SupportTelemetrySnapshotCollector).Assembly),
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            OverallStatus = overallStatus,
            MigrationStatus = migrationLabel,
            ArtifactDigest = ServerArtifactMetadata.ResolveArtifactDigest(configuration, options.ArtifactDigest),
            PlatformLockDigest = ServerArtifactMetadata.ResolvePlatformLockDigest(configuration, options.PlatformLockDigest),
        };

        var metrics = CollectMetrics(sp);
        var recentErrors = CollectSanitizedErrors(options.EffectiveMaxRecentErrors);
        var findings = options.IncludeOpsFindings
            ? await CollectOpsFindingsSafeAsync(sp, cancellationToken).ConfigureAwait(false)
            : [];

        var resolvedInstanceId = !string.IsNullOrWhiteSpace(options.InstanceId)
            ? options.InstanceId.Trim()
            : instanceIdentity.InstanceId;

        return new SupportTelemetrySnapshotPayload
        {
            InstanceId = resolvedInstanceId,
            InstanceUrl = string.IsNullOrWhiteSpace(options.InstanceUrl) ? null : options.InstanceUrl.Trim(),
            Health = health,
            RecentErrors = recentErrors,
            Metrics = metrics,
            OpsFindings = findings,
        };
    }

    private static async Task<bool> CheckReadinessSafeAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var readinessCheck = sp.GetService<IReadinessCheckService>();
        if (readinessCheck is null)
        {
            return true;
        }

        try
        {
            var result = await readinessCheck.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
            return result.IsReady;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static SupportTelemetryMetricsPayload CollectMetrics(IServiceProvider sp)
    {
        var memory = MemoryMonitor.GetMemoryUsage();
        var httpProvider = sp.GetService<IHttpRequestMetricsSnapshotProvider>();
        var dbProvider = sp.GetService<IDatabasePerformanceMetricsProvider>();

        double? httpP95 = null;
        double? httpAvg = null;
        double? httpErrorRate = null;
        long? httpTotal = null;
        long? httpSlow = null;

        if (httpProvider is not null)
        {
            var http = httpProvider.GetHttpRequestMetricsSnapshot();
            httpP95 = http.P95DurationMs;
            httpAvg = http.AvgDurationMs;
            httpTotal = http.TotalRequests;
            httpSlow = http.SlowRequestCount;
            httpErrorRate = http.TotalRequests > 0
                ? (double)http.TotalServerErrors / http.TotalRequests
                : 0.0;
        }

        double? dbCacheHitRate = null;
        long? dbCacheHits = null;
        long? dbCacheMisses = null;

        if (dbProvider is not null)
        {
            var db = dbProvider.GetMetrics();
            dbCacheHitRate = db.CacheHitRate;
            dbCacheHits = db.CacheHits;
            dbCacheMisses = db.CacheMisses;
        }

        return new SupportTelemetryMetricsPayload
        {
            HttpP95DurationMs = httpP95,
            HttpAvgDurationMs = httpAvg,
            HttpServerErrorRate = httpErrorRate,
            HttpTotalRequests = httpTotal,
            HttpSlowRequests = httpSlow,
            MemoryUsageMB = memory.AllocatedBytes / (1024.0 * 1024.0),
            MemoryPressurePercent = memory.MemoryPressurePercentage,
            DbCacheHitRate = dbCacheHitRate,
            DbCacheHits = dbCacheHits,
            DbCacheMisses = dbCacheMisses,
        };
    }

    private List<SupportTelemetryErrorPayload> CollectSanitizedErrors(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        var snapshot = recentErrorBuffer.Snapshot();
        var count = Math.Min(snapshot.Count, maxEntries);
        if (count == 0)
        {
            return [];
        }

        var results = new List<SupportTelemetryErrorPayload>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = snapshot[i];
            results.Add(new SupportTelemetryErrorPayload
            {
                Timestamp = entry.Timestamp,
                Message = RecentErrorSanitizer.Sanitize(entry.Message),
                CorrelationId = entry.CorrelationId,
                Path = entry.Path,
                StatusCode = entry.StatusCode,
            });
        }

        return results;
    }

    private static async Task<List<SupportTelemetryOpsFindingPayload>> CollectOpsFindingsSafeAsync(
        IServiceProvider sp,
        CancellationToken cancellationToken)
    {
        var findingsService = sp.GetService<IOpsFindingsService>();
        if (findingsService is null)
        {
            return [];
        }

        try
        {
            var findings = await findingsService.EvaluateAsync(cancellationToken).ConfigureAwait(false);
            if (findings.Count == 0)
            {
                return [];
            }

            var mapped = new List<SupportTelemetryOpsFindingPayload>(findings.Count);
            foreach (var finding in findings)
            {
                mapped.Add(new SupportTelemetryOpsFindingPayload
                {
                    FindingId = finding.Id,
                    Rule = finding.Rule,
                    Severity = finding.Severity.ToString(),
                    Summary = RecentErrorSanitizer.Sanitize(finding.Title),
                    RecommendedAction = finding.RecommendedAction?.Kind.ToString(),
                    DetectedAt = finding.DetectedAt,
                });
            }

            return mapped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Non-fatal: health, metrics, and recent errors must still be pushed when findings evaluation degrades.
            return [];
        }
    }
}
