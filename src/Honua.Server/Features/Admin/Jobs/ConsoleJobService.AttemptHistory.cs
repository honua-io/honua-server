// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.Jobs;

// Retried jobs keep one record across attempts: the claim fields describe the latest attempt only,
// and earlier attempts survive as job events. Timings and retry text say so instead of
// presenting cross-attempt spans as one measured queue wait.
internal sealed partial class ConsoleJobService
{
    // Written by the job reconciler on a lost attempt; withheld like the other worker identities.
    private const string LostAttemptWorkerMetadataKey = "worker.id";

    private static long? MeasureQueueDelayMs(ExecutionJobRecord job, List<string> missing)
    {
        var claimedAt = job.ClaimedAt;
        if (!claimedAt.HasValue || claimedAt.Value < job.CreatedAt)
        {
            return null;
        }

        if (job.AttemptCount > 1)
        {
            missing.Add($"lifecycle.queueDelayMs: attempt {job.AttemptCount} began at claimedAt, and the time since createdAt includes {job.AttemptCount - 1} earlier attempt(s), so no queue delay is measured; executionMs covers the latest attempt, and an attempt lost to an expired worker heartbeat is recorded as a worker-lost event");
            return null;
        }

        return (long)(claimedAt.Value - job.CreatedAt).TotalMilliseconds;
    }

    private static string DescribeWouldRepeat(ExecutionJobRecord job, JobRetryPolicy policy, string effects)
    {
        return ResolveRetryPolicyDisabledReason(policy, job.AttemptCount) switch
        {
            RetryDisabledReasons.NotRetryable => $"nothing is re-dispatched: the retry policy allows a single attempt; {effects}",
            RetryDisabledReasons.RetryBudgetExhausted => $"nothing is re-dispatched: all {policy.MaxAttempts} attempts allowed by the retry policy have been used; {effects}",
            _ => $"re-dispatches the whole job as attempt {Math.Max(1, job.AttemptCount) + 1} of {policy.MaxAttempts} on backend '{job.Spec.Backend}' with the same spec; {effects}"
        };
    }

    // An execute-scoped principal gets the operator view; every other reader gets the owner
    // view, which withholds the lost-attempt worker identity on the log, steps and diagnostics reads.
    private async Task<bool> IsOperatorViewAsync(ClaimsPrincipal user, ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        var executeDecision = await approvalGate.CheckAuthorizationAsync(
            user,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                ResourceId = job.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        return executeDecision.IsAllowed;
    }

    private async Task<ConsoleJobLogPageResponse> BuildLogPageAsync(HttpContext context, ExecutionJobRecord job, ExecutionLogPage page)
    {
        var items = page.Items.Select(MapLogEntry).ToArray();
        if (!await IsOperatorViewAsync(context.User, job, context.RequestAborted).ConfigureAwait(false))
        {
            items = [.. items.Select(item => item with { Metadata = WithoutWorker(item.Metadata) })];
        }

        return new ConsoleJobLogPageResponse
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            State = "available",
            Items = items,
            NextCursor = page.NextCursor
        };
    }

    private static ConsoleJobDiagnosticEvents WithholdLostAttemptWorker(ConsoleJobDiagnosticEvents events)
        => events with { Items = [.. events.Items.Select(item => item with { Metadata = WithoutWorker(item.Metadata) })] };

    private static ConsoleJobStep[] WithholdLostAttemptWorker(ConsoleJobStep[] steps)
        => [.. steps.Select(step => step with { Metadata = WithoutWorker(step.Metadata) })];

    private static Dictionary<string, string>? WithoutWorker(Dictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.ContainsKey(LostAttemptWorkerMetadataKey))
        {
            return metadata;
        }

        var copy = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        copy.Remove(LostAttemptWorkerMetadataKey);
        return copy;
    }
}
