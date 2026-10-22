// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Runs an accepted large-table publish to completion on its own budget. Used by the in-process
/// task and by <see cref="LayerPublishJobExecutor"/>.
/// </summary>
internal static partial class LayerPublishJobWork
{
    internal const string SafeFailureMessage = "Layer publish failed.";
    internal static readonly TimeSpan ProgressRetention = TimeSpan.FromDays(1);

    internal static async Task RunInProcessAsync(
        IServiceScopeFactory scopeFactory,
        IUniversalProgressStore progressStore,
        ILogger logger,
        LayerPublishJobPayload payload,
        string jobId,
        CancellationToken stoppingToken)
    {
        try
        {
            _ = await ExecuteAsync(scopeFactory, progressStore, logger, payload, jobId, context: null, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await TryWriteTerminalAsync(
                logger, progressStore, jobId, OperationStatus.Cancelled, null, "Layer publish was canceled.")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.InProcessFaulted(logger, jobId, exception);
            await TryWriteTerminalAsync(
                logger, progressStore, jobId, OperationStatus.Failed, null, SafeFailureMessage)
                .ConfigureAwait(false);
        }
    }

    internal static async Task<JobExecutionResult> ExecuteAsync(
        IServiceScopeFactory scopeFactory,
        IUniversalProgressStore progressStore,
        ILogger logger,
        LayerPublishJobPayload payload,
        string jobId,
        IJobExecutionContext? context,
        CancellationToken jobToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(progressStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(payload);

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // The publisher captures the scoped tenant and schema at resolution. Populate both first
        // or a namespaced / operational-schema publish runs against the wrong catalog.
        services.GetRequiredService<RequestTenantContext>().Set(payload.TenantId, payload.TenantSource);
        if (services.GetService<ISchemaContext>() is SchemaContext schema)
        {
            schema.CurrentSchema = payload.CurrentSchema;
        }

        var publishing = services.GetRequiredService<ILayerPublishingService>();
        var resolver = services.GetRequiredService<ISecureConnectionResolver>();
        var options = services.GetRequiredService<IOptions<LayerPublishingOptions>>().Value;
        var timeoutSeconds = options.GetValidatedMaterializationTimeoutSeconds();

        LayerPublishRequest publishRequest;
        try
        {
            publishRequest = LayerPublishRequestMapper.CreateDomain(
                payload.Request, payload.ConnectionId, payload.TenantId);
            publishRequest.ConnectionEndpointFingerprint = payload.ConnectionEndpointFingerprint;
            if (payload.ConnectionEndpointFingerprint is not null)
            {
                var source = payload.CommittedSinkPublication
                    ?? throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                        "Committed sink source evidence is unavailable.");
                if (payload.ConnectionId != source.ConnectionId ||
                    !string.Equals(publishRequest.Schema, source.Schema, StringComparison.Ordinal) ||
                    !string.Equals(publishRequest.Table, source.Table, StringComparison.Ordinal) ||
                    !string.Equals(payload.TenantId, source.TenantId, StringComparison.Ordinal) ||
                    !string.Equals(payload.ConnectionEndpointFingerprint, source.EndpointFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                        "Committed sink source identity changed before execution.");
                }

                publishRequest.CommittedSinkPublication = await CommittedSinkPublicationVerifier.RevalidateAsync(
                    services.GetRequiredService<IExecutionJobStore>(),
                    services.GetRequiredService<IProcessCatalog>(),
                    source, payload.ConnectionId, publishRequest.Schema, publishRequest.Table,
                    payload.ConnectionEndpointFingerprint, payload.TenantId, jobToken).ConfigureAwait(false);
            }
        }
        catch (LayerPublishingException exception)
        {
            await WriteTerminalAsync(
                progressStore, jobId, OperationStatus.Failed, null, exception.Message).ConfigureAwait(false);
            return NonRetryable(exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            Log.PublishFaulted(logger, jobId, exception);
            await WriteTerminalAsync(
                progressStore, jobId, OperationStatus.Failed, null, SafeFailureMessage).ConfigureAwait(false);
            return NonRetryable(SafeFailureMessage);
        }

        await WritePhaseAsync(progressStore, jobId, OperationStatus.Processing, "Materializing", null)
            .ConfigureAwait(false);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(jobToken);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var observedProgress = new StoreProgress(progressStore, jobId);
        using var progress = LayerSnapshotProgressScope.Begin(observedProgress);

        PublishedLayerSummary summary;
        try
        {
            var connectionString = payload.ConnectionId != Guid.Empty
                ? await resolver.ResolveConnectionStringAsync(payload.ConnectionId, budget.Token).ConfigureAwait(false)
                : await resolver.ResolveConnectionStringAsync(payload.ConnectionKey, budget.Token).ConfigureAwait(false);
            summary = await publishing.PublishLayerAsync(connectionString, publishRequest, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var message = BudgetMessage(timeoutSeconds);
            await WriteTerminalAsync(progressStore, jobId, OperationStatus.Failed, null, message)
                .ConfigureAwait(false);
            return NonRetryable(message);
        }
        catch (TimeoutException exception)
        {
            Log.PublishFaulted(logger, jobId, exception);
            await WriteTerminalAsync(progressStore, jobId, OperationStatus.Failed, null, exception.Message)
                .ConfigureAwait(false);
            return NonRetryable(exception.Message);
        }
        catch (LayerPublishingException exception)
        {
            Log.PublishFaulted(logger, jobId, exception);
            await WriteTerminalAsync(
                progressStore, jobId, OperationStatus.Failed, exception.LayerId, exception.Message)
                .ConfigureAwait(false);
            return NonRetryable(exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.PublishFaulted(logger, jobId, exception);
            await WriteTerminalAsync(progressStore, jobId, OperationStatus.Failed, null, SafeFailureMessage)
                .ConfigureAwait(false);
            return NonRetryable(SafeFailureMessage);
        }

        if (context is not null)
        {
            try
            {
                await context.RecordCommittedEffectAsync(
                    LayerPublishCompletionReceipt.Create(
                        summary.LayerId, observedProgress.LastSnapshot, summary.MetadataRevision),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The publication transaction already committed. A rejected receipt must not retry it.
                Log.ReceiptFailed(logger, jobId, summary.LayerId, exception);
            }
        }

        // The database transaction has committed. A progress projection failure must not turn
        // this into a retry of the publication; the durable receipt also lets polling recover
        // the layer id from the execution record after a transient terminal-write failure.
        try
        {
            await WriteTerminalAsync(progressStore, jobId, OperationStatus.Completed, summary.LayerId,
                errorMessage: null, observedProgress.LastSnapshot, summary.MetadataRevision).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.TerminalWriteFailed(logger, jobId, exception);
        }

        await InvalidateCatalogAsync(services, logger, summary.ServiceName, summary.LayerId).ConfigureAwait(false);
        try
        {
            var notifications = services.GetService<IMcpNotificationPublisher>();
            notifications?.BroadcastResourcesListChanged();
            notifications?.BroadcastToolsListChanged();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.NotificationFailed(logger, jobId, exception);
        }

        return JobExecutionResult.Succeeded() with { CompletedWithCommittedEffects = true };
    }

    internal static async Task WritePhaseAsync(
        IUniversalProgressStore progressStore,
        string jobId,
        OperationStatus status,
        string phase,
        string? errorMessage)
    {
        var current = await progressStore.GetProgressAsync<LayerPublishJobProgress>(jobId, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null || IsTerminal(current.Status))
        {
            return;
        }

        await progressStore.SetProgressAsync(jobId, current with
        {
            Status = status,
            CurrentPhase = phase,
            ErrorMessage = errorMessage,
        }, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WriteTerminalAsync(
        IUniversalProgressStore progressStore,
        string jobId,
        OperationStatus status,
        int? layerId,
        string? errorMessage,
        (long RowsMaterialized, long RowsTotal)? snapshot = null,
        long? metadataRevision = null)
    {
        var current = await progressStore.GetProgressAsync<LayerPublishJobProgress>(jobId, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        if (IsTerminal(current.Status) && current.Status != status)
        {
            return;
        }

        await progressStore.SetProgressAsync(jobId, current with
        {
            Status = status,
            LayerId = layerId ?? current.LayerId,
            MetadataRevision = metadataRevision ?? current.MetadataRevision,
            RowsMaterialized = snapshot?.RowsMaterialized ?? current.RowsMaterialized,
            RowsTotal = snapshot?.RowsTotal ?? current.RowsTotal,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            CurrentPhase = status.ToString(),
        }, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task TryWriteTerminalAsync(
        ILogger logger,
        IUniversalProgressStore progressStore,
        string jobId,
        OperationStatus status,
        int? layerId,
        string? errorMessage)
    {
        try
        {
            await WriteTerminalAsync(progressStore, jobId, status, layerId, errorMessage).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.TerminalWriteFailed(logger, jobId, exception);
        }
    }

    private static async Task InvalidateCatalogAsync(
        IServiceProvider services,
        ILogger logger,
        string? serviceName,
        int layerId)
    {
        try
        {
            if (layerId > 0)
            {
                services.GetService<IFeatureCacheManager>()?.InvalidateLayerCache(layerId);
            }

            var cacheInvalidator = services.GetService<OutputCacheInvalidationService>();
            if (cacheInvalidator is null)
            {
                return;
            }

            await cacheInvalidator.InvalidateServiceCatalogAsync(
                serviceName,
                layerId > 0 ? [layerId] : [],
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.CacheInvalidationFailed(logger, serviceName, exception);
        }
    }

    private static bool IsTerminal(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    private static string BudgetMessage(int timeoutSeconds)
        => $"Layer publish exceeded its {timeoutSeconds.ToString(CultureInfo.InvariantCulture)}-second budget "
            + $"({LayerPublishingOptions.SectionName}:MaterializationTimeoutSeconds).";

    private static JobExecutionResult NonRetryable(string message)
        => JobExecutionResult.Failed(message) with { IsRetryable = false };

    private sealed class StoreProgress(IUniversalProgressStore store, string jobId) : ILayerSnapshotProgress
    {
        internal (long RowsMaterialized, long RowsTotal)? LastSnapshot { get; private set; }

        public async Task ReportAsync(long rowsMaterialized, long rowsTotal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the actual page observation even if the best-effort progress store is down.
            LastSnapshot = (rowsMaterialized, rowsTotal);
            var current = await store.GetProgressAsync<LayerPublishJobProgress>(jobId, CancellationToken.None)
                .ConfigureAwait(false);
            if (current is null || IsTerminal(current.Status))
            {
                return;
            }

            await store.SetProgressAsync(jobId, current with
            {
                Status = OperationStatus.Processing,
                RowsMaterialized = rowsMaterialized,
                RowsTotal = rowsTotal,
                CurrentPhase = "Materializing",
            }, ProgressRetention, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 8217, Level = LogLevel.Warning, Message = "Layer publish {JobId} committed but catalog notification failed")]
        public static partial void NotificationFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(EventId = 8210, Level = LogLevel.Error, Message = "In-process layer publish {JobId} faulted")]
        public static partial void InProcessFaulted(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(EventId = 8211, Level = LogLevel.Warning, Message = "Layer publish {JobId} failed")]
        public static partial void PublishFaulted(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(EventId = 8212, Level = LogLevel.Warning, Message = "Layer publish {JobId} committed layer {LayerId} but the execution receipt was rejected")]
        public static partial void ReceiptFailed(ILogger logger, string jobId, int layerId, Exception exception);

        [LoggerMessage(EventId = 8213, Level = LogLevel.Warning, Message = "Layer publish cache invalidation failed for service {ServiceName}")]
        public static partial void CacheInvalidationFailed(ILogger logger, string? serviceName, Exception exception);

        [LoggerMessage(EventId = 8214, Level = LogLevel.Warning, Message = "Layer publish {JobId} terminal progress was not recorded")]
        public static partial void TerminalWriteFailed(ILogger logger, string jobId, Exception exception);
    }
}
