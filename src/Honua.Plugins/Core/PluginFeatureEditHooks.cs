// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Plugins;

// Runs the plugin edit pipeline for edit protocols that work on Core edit batches.
internal sealed partial class PluginFeatureEditHooks(
    IPluginEditPipeline pipeline,
    ILogger<PluginFeatureEditHooks> logger) : IFeatureEditHooks
{
    private static readonly TimeSpan AfterHookTimeout = TimeSpan.FromSeconds(10);

    public bool HasHooks => pipeline.HasPlugins;

    public async Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
        FeatureEditHookScope scope,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var rows = FeatureEditRows.Enumerate(batch);
        if (!pipeline.HasPlugins || rows.Count == 0)
        {
            return [];
        }

        var hookFeatures = rows.Select((row, position) => ToHookFeature(row, position, createdObjectId: null));
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(ToContext(scope, hookFeatures), cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.HasRejections)
        {
            return [];
        }

        return outcome.Rejections
            .Where(rejection => rejection.RequestIndex >= 0 && rejection.RequestIndex < rows.Count)
            .Select(rejection => new FeatureEditHookRejection(
                rows[rejection.RequestIndex].Kind,
                rows[rejection.RequestIndex].Index,
                rejection.Message))
            .DistinctBy(static rejection => (rejection.Kind, rejection.Index))
            .ToList();
    }

    public async Task RunAfterAsync(
        FeatureEditHookScope scope,
        FeatureEditBatch batch,
        FeatureEditResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!pipeline.HasPlugins)
        {
            return;
        }

        var committed = FeatureEditRows.Enumerate(batch)
            .Select((row, position) => (Row: row, Position: position))
            .Where(item => FeatureEditRows.IsCommitted(result, item.Row))
            .Select(item => ToHookFeature(item.Row, item.Position, FeatureEditRows.ResultOf(result, item.Row)?.ObjectId))
            .ToList();
        if (committed.Count == 0)
        {
            return;
        }

        await RunAfterHooksSafelyAsync(ToContext(scope, committed), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAfterHooksSafelyAsync(EditHookContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AfterHookTimeout);
        try
        {
            await pipeline.RunAfterHooksAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The edit has already committed, so a failing or slow after-hook must not fail the request.
            Log.AfterHooksFailed(logger, context.ServiceId, context.LayerId, ex);
        }
    }

    private static EditHookContext ToContext(FeatureEditHookScope scope, IEnumerable<EditHookFeature> features)
        => new(scope.ServiceId, scope.LayerId, scope.LayerName, scope.UserName, scope.TraceId, [.. features]);

    private static EditHookFeature ToHookFeature(FeatureEditRow row, int position, long? createdObjectId)
    {
        var kind = row.Kind switch
        {
            FeatureEditOperationKind.Create => EditKind.Create,
            FeatureEditOperationKind.Update => EditKind.Update,
            _ => EditKind.Delete,
        };
        var objectId = row.Kind == FeatureEditOperationKind.Create ? createdObjectId : row.ObjectId;
        var feature = row.Feature ?? Feature.Create(objectId ?? 0, geometry: null);
        return new EditHookFeature(kind, position, objectId, objectId is { } id ? feature with { Id = id } : feature);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9304, Level = LogLevel.Error,
            Message = "Plugin after-edit hooks failed for service {ServiceId} layer {LayerId}; edit already committed.")]
        public static partial void AfterHooksFailed(ILogger logger, string serviceId, int layerId, Exception exception);
    }
}
