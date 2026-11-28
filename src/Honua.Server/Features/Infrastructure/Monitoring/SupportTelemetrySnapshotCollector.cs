// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Hosting;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed record SupportTelemetrySnapshotSources(
    IServerInstanceIdentity InstanceIdentity,
    RecentErrorBuffer RecentErrorBuffer,
    IServiceScopeFactory ScopeFactory);

internal sealed class SupportTelemetrySnapshotCollector(
    SupportTelemetrySnapshotSources sources,
    IOptionsMonitor<SupportTelemetryPushOptions> optionsMonitor)
{
    public async Task<SupportTelemetrySnapshotPayload> CollectAsync(CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        await using var scope = sources.ScopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var health = await CollectHealthAsync(sp, options, cancellationToken).ConfigureAwait(false);
        var findings = options.IncludeOpsFindings
            ? await CollectOpsFindingsSafeAsync(sp, cancellationToken).ConfigureAwait(false)
            : [];

        return new SupportTelemetrySnapshotPayload
        {
            InstanceId = ResolveInstanceId(options),
            InstanceUrl = string.IsNullOrWhiteSpace(options.InstanceUrl) ? null : options.InstanceUrl.Trim(),
            Health = health,
            RecentErrors = CollectSanitizedErrors(options.EffectiveMaxRecentErrors),
            Metrics = CollectMetrics(sp),
            OpsFindings = findings,
        };
    }

    private string ResolveInstanceId(SupportTelemetryPushOptions options) =>
        !string.IsNullOrWhiteSpace(options.InstanceId)
            ? options.InstanceId.Trim()
            : sources.InstanceIdentity.InstanceId;

    private static async Task<SupportTelemetryHealthPayload> CollectHealthAsync(
        IServiceProvider sp,
        SupportTelemetryPushOptions options,
        CancellationToken cancellationToken)
    {
        var configuration = sp.GetService<IConfiguration>();
        var migrationState = sp.GetService<MigrationState>();
        var readiness = await CheckReadinessSafeAsync(sp, cancellationToken).ConfigureAwait(false);

        return new SupportTelemetryHealthPayload
        {
            Readiness = readiness ? "healthy" : "unhealthy",
            Liveness = "healthy",
            Version = ServerArtifactMetadata.GetServerVersion(typeof(SupportTelemetrySnapshotCollector).Assembly),
            MetadataSchemaVersion = MetadataV2Constants.SchemaVersion,
            OverallStatus = readiness ? "healthy" : ResolveNotReadyStatus(migrationState),
            MigrationStatus = migrationState is null
                ? "unknown"
                : DeployPreflightProbe.GetMigrationStatusLabel(migrationState.Status),
            ArtifactDigest = ServerArtifactMetadata.ResolveArtifactDigest(configuration, options.ArtifactDigest),
            PlatformLockDigest = ServerArtifactMetadata.ResolvePlatformLockDigest(configuration, options.PlatformLockDigest),
        };
    }

    private static string ResolveNotReadyStatus(MigrationState? migrationState)
    {
        if (migrationState?.IsFailed == true)
        {
            return "unhealthy";
        }

        return migrationState is not null && (migrationState.IsRunning || !migrationState.IsReady)
            ? "initializing"
            : "unhealthy";
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
        var http = sp.GetService<IHttpRequestMetricsSnapshotProvider>()?.GetHttpRequestMetricsSnapshot();
        var db = sp.GetService<IDatabasePerformanceMetricsProvider>()?.GetMetrics();

        return new SupportTelemetryMetricsPayload
        {
            HttpP95DurationMs = http?.P95DurationMs,
            HttpAvgDurationMs = http?.AvgDurationMs,
            HttpServerErrorRate = http is null
                ? null
                : http.TotalRequests > 0 ? (double)http.TotalServerErrors / http.TotalRequests : 0.0,
            HttpTotalRequests = http?.TotalRequests,
            HttpSlowRequests = http?.SlowRequestCount,
            MemoryUsageMB = memory.AllocatedBytes / (1024.0 * 1024.0),
            MemoryPressurePercent = memory.MemoryPressurePercentage,
            DbCacheHitRate = db?.CacheHitRate,
            DbCacheHits = db?.CacheHits,
            DbCacheMisses = db?.CacheMisses,
        };
    }

    private List<SupportTelemetryErrorPayload> CollectSanitizedErrors(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        var snapshot = sources.RecentErrorBuffer.Snapshot();
        var count = Math.Min(snapshot.Count, maxEntries);
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
            return findings.Select(MapFinding).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static SupportTelemetryOpsFindingPayload MapFinding(OpsFinding finding) => new()
    {
        FindingId = finding.Id,
        Rule = finding.Rule,
        Severity = finding.Severity.ToString(),
        Summary = RecentErrorSanitizer.Sanitize(finding.Title),
        RecommendedAction = finding.RecommendedAction?.Kind.ToString(),
        DetectedAt = finding.DetectedAt,
    };
}
