// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure;
using Microsoft.AspNetCore.Mvc;

using Honua.Server.Features.Admin.TileOperations;

namespace Honua.Server.Features.Admin;

internal static partial class OperationsProgressEndpoints
{
    private static async Task<IResult> AlreadyCancelledAsync(
        HttpContext httpContext,
        IOperationProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress is GeoprocessingProgress)
        {
            var jobStore = httpContext.RequestServices.GetService<IExecutionJobStore>();
            var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
            var job = jobStore == null ? null : await jobStore.GetAsync(progress.OperationId, cancellationToken).ConfigureAwait(false);
            var result = await TryReconcileDurableCancelAsync(httpContext, job, progressStore, cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                return result;
            }
        }

        return Results.Json(new CancelOperationResponse
        {
            OperationId = progress.OperationId,
            Message = "Operation cancellation requested",
            Type = progress.Type
        }, OperationsProgressJsonContext.Default.CancelOperationResponse);
    }

    private static async Task BridgeCancelledExecutionAsync(
        HttpContext httpContext,
        ExecutionJobCancellationResult cancelOutcome,
        CancellationToken cancellationToken)
    {
        if (cancelOutcome.TerminalTransition is { Spec.Kind: ExecutionJobKind.TileCache } cancelledTileJob)
        {
            TileOperationMetrics.RecordJobEnded(cancelledTileJob);
        }

        if (cancelOutcome.Job is { Spec.Kind: ExecutionJobKind.Geoprocessing } cancelledJob)
        {
            var progressStore = httpContext.RequestServices.GetRequiredService<IUniversalProgressStore>();
            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                progressStore, cancelledJob, TimeSpan.FromDays(7), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
    private static async Task<IResult> CancelLocalExecutionAsync(
        HttpContext context,
        (ExecutionJobRecord Job, IOperationProgress Progress) cancellation,
        CancellationToken cancellationToken)
    {
        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var result = await TryReconcileDurableCancelAsync(
            context, cancellation.Job, progressStore, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            return result;
        }

        if (cancellation.Job.ClaimedBy != null)
        {
            var notifiers = context.RequestServices.GetServices<IJobCancellationNotifier>();
            ExecutionJobCancellationTokens.CancelAttempt(notifiers, cancellation.Job);
        }

        return Results.Json(new CancelOperationResponse
        {
            OperationId = cancellation.Job.OperationId,
            Message = "Operation cancellation requested",
            Type = cancellation.Progress.Type
        }, OperationsProgressJsonContext.Default.CancelOperationResponse);
    }

}
