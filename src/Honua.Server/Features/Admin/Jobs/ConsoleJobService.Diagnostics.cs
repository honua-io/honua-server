// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.ControlPlane;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;

namespace Honua.Server.Features.Admin.Jobs;

/// <summary>
/// The job diagnostics projection (honua-server#6219). It composes the detail, steps,
/// events and artifacts the other admin job routes already expose into one answer and
/// adds what they leave implicit: canonical lifecycle phase, worker liveness judged by
/// the job's own heartbeat policy, release provenance with every absent field named,
/// the failed step's structured error, and an explicit retry disposition. Nothing is
/// inferred silently: measured and inferred timings are labelled, truncation and
/// missing evidence are listed, and historical jobs without provenance say so.
/// </summary>
internal sealed partial class ConsoleJobService
{
    public const int DefaultDiagnosticEventLimit = 50;
    public const int MaxDiagnosticEventLimit = 500;
    private const int MaxStderrExcerptLength = 2000;

    private static readonly string[] ErrorCodeMetadataKeys = ["error.code", "errorCode", "error_code", "code"];
    private static readonly string[] ExitCodeMetadataKeys = ["native.exit_code", "nativeExitCode", "exit.code", "exitCode", "exit_code", "honua.event.exitCode"];
    private static readonly string[] StderrMetadataKeys = ["stderr.excerpt", "stderr_excerpt", "native.stderr", "stderr"];

    private static readonly string ServerVersion = ResolveServerVersion();

    public Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(
        HttpContext context,
        string jobId,
        int? eventLimit,
        string? eventCursor,
        CancellationToken cancellationToken)
        => GetDiagnosticsAsync(context.User, context.Request.PathBase.Value ?? string.Empty, jobId, eventLimit, eventCursor, cancellationToken);

    public async Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(
        ClaimsPrincipal user,
        string pathBase,
        string jobId,
        int? eventLimit,
        string? eventCursor,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(user, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        if (eventCursor is not null && eventCursor != "start"
            && (eventCursor.Length > 128 || !TryDecodeOffsetCursor(eventCursor, out _, out _)))
        {
            throw new ArgumentException("Invalid diagnostic event cursor.", nameof(eventCursor));
        }

        var limit = Math.Clamp(eventLimit ?? DefaultDiagnosticEventLimit, 1, MaxDiagnosticEventLimit);
        var now = timeProvider.GetUtcNow();
        var missing = new List<string>();
        var truncated = new List<string>();
        var notes = new List<string>();

        var executeDecision = await approvalGate.CheckAuthorizationAsync(
            user,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                ResourceId = job.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        var operatorView = executeDecision.IsAllowed;
        if (!operatorView)
        {
            notes.Add("owner view: worker and provider identities are withheld; an execute-scoped principal receives them");
        }

        var workspace = await ResolveWorkspaceAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        var logStore = serviceProvider.GetService<IExecutionLogStore>();
        var tail = logStore is null
            ? new ExecutionLogTail { Items = [], TotalCount = 0 }
            : await logStore.TailAsync(job.OperationId, MaxDiagnosticEventLimit, cancellationToken).ConfigureAwait(false);
        var entries = tail.Items;
        var ordinalOffset = Math.Max(0, tail.TotalCount - entries.Count);
        if (ordinalOffset > 0)
        {
            truncated.Add($"steps and failure details: inspecting the most recent {entries.Count} of {tail.TotalCount} events; page events from firstCursor for earlier evidence");
        }

        var events = await BuildEventsAsync(logStore, job, tail, limit, eventCursor, truncated, missing, cancellationToken).ConfigureAwait(false);
        var steps = await BuildDiagnosticStepsAsync(job, entries, workspace, ordinalOffset, cancellationToken).ConfigureAwait(false);
        if (steps.Length == 0)
        {
            missing.Add("steps: no execution log entries were recorded for this job");
        }
        else
        {
            notes.Add("step timingSource identifies measured harness durations or durations inferred from consecutive log timestamps");
        }

        var artifacts = await BuildDiagnosticArtifactsAsync(job, missing, cancellationToken).ConfigureAwait(false);
        var release = BuildRelease(job, missing, operatorView);
        var lifecycle = BuildLifecycle(job, now, missing);
        var worker = BuildWorker(job, now, operatorView);
        var attempt = await BuildAttemptAsync(user, job, operatorView, cancellationToken).ConfigureAwait(false);
        var failure = BuildFailure(job, entries, workspace, attempt, ordinalOffset);
        if (failure == null && job.Status == ExecutionJobStatus.Failed)
        {
            missing.Add("failure: the job is Failed but recorded no error message");
        }

        var traceId = JobEventIdentity.ResolveTraceId(job);
        if (traceId is null)
        {
            missing.Add("traceId: the job record has no admitted traceparent and no honua.trace_id");
        }

        if (artifacts.ExpiredCount > 0)
        {
            notes.Add("artifacts: expired results cannot be fetched; resubmit the job to produce them again");
        }

        return new ConsoleJobDiagnostics
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            TraceId = traceId,
            GeneratedAt = now,
            View = operatorView ? "operator" : "owner",
            Identity = new ConsoleJobDiagnosticIdentity
            {
                Kind = job.Spec.Kind.ToString(),
                Backend = job.Spec.Backend,
                TargetKind = job.Spec.TargetKind.ToString(),
                WorkloadName = job.Spec.WorkloadName,
                WorkloadId = operatorView ? job.Spec.WorkloadId : null,
                DefinitionId = GetDefinitionId(job),
                Queue = ExecutionJobMetadata.ResolveQueue(job),
                Priority = job.Priority.ToString(),
                RequestedBy = job.Audit.RequestedBy,
                ProviderOperationId = operatorView ? job.ProviderOperationId : null,
                RuntimeProfile = job.Spec.RuntimeProfile,
                ContractVersion = job.Spec.ContractVersion,
                ResourceRefs = GetResourceRefs(job)
            },
            Release = release,
            Lifecycle = lifecycle,
            Attempt = attempt,
            Worker = worker,
            Failure = failure,
            Steps = steps,
            Events = events,
            Artifacts = artifacts,
            Evidence = new ConsoleJobDiagnosticEvidence
            {
                Missing = missing.ToArray(),
                Truncated = truncated.ToArray(),
                Notes = notes.ToArray()
            },
            Links = BuildLinks(pathBase, job)
        };
    }

    private async Task<ExecutionJobRecord?> GetAuthorizedJobAsync(
        ClaimsPrincipal user,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await RequireJobStore().GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        var decision = await approvalGate.CheckAuthorizationAsync(
            user,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read,
                ResourceId = job.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed ? job : null;
    }

    private static ConsoleJobDiagnosticRelease BuildRelease(ExecutionJobRecord job, List<string> missing, bool operatorView)
    {
        var runtime = GetParameter(job, CustomCodeJobContract.RuntimeParam);
        var repository = GetParameter(job, CustomCodeJobContract.RepoUrlParam);
        var sourceRef = GetParameter(job, CustomCodeJobContract.GitRefParam);
        var entrypoint = GetParameter(job, CustomCodeJobContract.EntrypointParam);
        var dependencyManifest = GetParameter(job, CustomCodeJobContract.DepsManifestParam);
        var planId = GetParameter(job, ExecutionJobParameterKeys.GeoprocessingPlanId);
        var processDefinitions = SplitList(GetParameter(job, ExecutionJobParameterKeys.GeoprocessingProcessDefinitions));
        var releaseMissing = new List<string>();

        var isCustomCode = string.Equals(job.Spec.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal)
            || runtime is not null || repository is not null || sourceRef is not null;

        if (isCustomCode)
        {
            if (runtime is null)
            {
                releaseMissing.Add("release.runtime");
            }

            if (repository is null)
            {
                releaseMissing.Add("release.sourceRepository");
            }

            if (sourceRef is null)
            {
                releaseMissing.Add("release.sourceRef");
            }

            if (entrypoint is null)
            {
                releaseMissing.Add("release.entrypoint");
            }

            if (dependencyManifest is null)
            {
                releaseMissing.Add("release.dependencyManifest");
            }
        }
        else if (planId is null && processDefinitions.Length == 0)
        {
            // Managed processes execute the server's own code: the server version is the
            // code identity. The plan and process definitions bind the workflow version.
            releaseMissing.Add("release.planId");
            releaseMissing.Add("release.processDefinitions");
        }

        var provenanceFields = 0;
        foreach (var value in new[] { runtime, repository, sourceRef, entrypoint, dependencyManifest, planId })
        {
            if (value is not null)
            {
                provenanceFields++;
            }
        }

        if (processDefinitions.Length > 0)
        {
            provenanceFields++;
        }

        var state = releaseMissing.Count == 0
            ? "complete"
            : provenanceFields > 0 ? "partial" : "unavailable";

        foreach (var field in releaseMissing)
        {
            missing.Add(field + (state == "unavailable"
                ? ": the job recorded no release provenance (historical job or a submitter that predates provenance)"
                : ": not recorded on the job"));
        }

        return new ConsoleJobDiagnosticRelease
        {
            State = state,
            ServerVersion = ServerVersion,
            Runtime = runtime,
            SourceRepository = SafeIdentity(repository),
            SourceRef = SafeIdentity(sourceRef),
            Entrypoint = SafeIdentity(entrypoint),
            DependencyManifest = SafeIdentity(dependencyManifest),
            ProcessRelease = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.ReleaseProcessParam)),
            ProcessVersion = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.ReleaseVersionParam)),
            LockDigest = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.ReleaseLockDigestParam)),
            BindingVersion = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.ReleaseBindingVersionParam)),
            WorkflowVersion = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.WorkflowVersionParam)),
            DependencyLockDigest = SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.DependencyDigestParam)),
            WorkerImage = operatorView ? SafeIdentity(GetParameter(job, GpProcessReleaseAdmission.WorkerImageParam)) : null,
            PlanId = planId,
            ProcessDefinitions = processDefinitions,
            Environment = GetParameter(job, ExecutionJobParameterKeys.Environment),
            ReleaseId = GetParameter(job, ExecutionJobParameterKeys.ReleaseId),
            ChangeSetId = GetParameter(job, ExecutionJobParameterKeys.ChangeSetId),
            Missing = releaseMissing.ToArray()
        };
    }

    private static ConsoleJobDiagnosticLifecycle BuildLifecycle(ExecutionJobRecord job, DateTimeOffset now, List<string> missing)
    {
        var claimedAt = job.ClaimedAt;
        long? queueDelay = claimedAt.HasValue && claimedAt.Value >= job.CreatedAt
            ? (long)(claimedAt.Value - job.CreatedAt).TotalMilliseconds
            : null;
        long? execution = claimedAt.HasValue && job.CompletedAt.HasValue && job.CompletedAt.Value >= claimedAt.Value
            ? (long)(job.CompletedAt.Value - claimedAt.Value).TotalMilliseconds
            : null;
        if (!claimedAt.HasValue && job.Status is not ExecutionJobStatus.Queued)
        {
            missing.Add("lifecycle.claimedAt: the job record carries no claim timestamp, so queue delay and execution time are not measured");
        }

        return new ConsoleJobDiagnosticLifecycle
        {
            Status = job.Status.ToString(),
            Phase = CanonicalPhase(job, now),
            CurrentPhase = string.IsNullOrWhiteSpace(job.CurrentPhase)
                ? null
                : WorkspacePathSanitizer.SanitizeForClient(job.CurrentPhase, null),
            PercentComplete = job.PercentComplete,
            CreatedAt = job.CreatedAt,
            ClaimedAt = claimedAt,
            UpdatedAt = job.UpdatedAt,
            CompletedAt = job.CompletedAt,
            CancellationRequestedAt = job.CancellationRequestedAt,
            QueueDelayMs = queueDelay,
            ExecutionMs = execution,
            DurationMs = ComputeDurationMs(job),
            TimingSource = claimedAt.HasValue ? "measured" : "inferred"
        };
    }

    private static string CanonicalPhase(ExecutionJobRecord job, DateTimeOffset now)
    {
        var raw = job.CurrentPhase ?? string.Empty;
        return job.Status switch
        {
            ExecutionJobStatus.Queued => job.NextRetryAt.HasValue && job.NextRetryAt.Value > now
                ? "waiting-for-retry"
                : "waiting-for-capacity",
            ExecutionJobStatus.Provisioning => "starting-worker",
            ExecutionJobStatus.Running when ContainsAny(raw, "upload", "publish", "artifact") => "publishing-results",
            ExecutionJobStatus.Running when ContainsAny(raw, "fetch", "clone", "checkout", "resolve") => "fetching-code",
            ExecutionJobStatus.Running => "executing",
            ExecutionJobStatus.Succeeded => "completed",
            ExecutionJobStatus.Failed => "failed",
            ExecutionJobStatus.Cancelled => "cancelled",
            _ => job.Status.ToString().ToLowerInvariant()
        };
    }

    private static ConsoleJobDiagnosticWorker BuildWorker(ExecutionJobRecord job, DateTimeOffset now, bool operatorView)
    {
        var policy = job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default;
        var lastHeartbeat = job.LastHeartbeatAt ?? job.ClaimedAt;
        string liveness;
        string reason;

        if (job.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled)
        {
            liveness = "finished";
            reason = $"job is {job.Status}; no worker is expected to be alive";
        }
        else if (job.Status == ExecutionJobStatus.Queued || string.IsNullOrWhiteSpace(job.ClaimedBy))
        {
            liveness = "not-claimed";
            reason = "no worker has claimed the job yet";
        }
        else if (!lastHeartbeat.HasValue)
        {
            liveness = "unknown";
            reason = "the job is claimed but carries neither a heartbeat nor a claim timestamp";
        }
        else if (policy.IsExpired(lastHeartbeat.Value, now))
        {
            liveness = "lost";
            reason = $"last heartbeat {Age(now, lastHeartbeat.Value)} ago exceeds the {policy.Timeout.TotalSeconds:0}s heartbeat timeout; the reconciler will fail or retry the attempt";
        }
        else if (now - lastHeartbeat.Value > policy.Interval)
        {
            liveness = "stale";
            reason = $"last heartbeat {Age(now, lastHeartbeat.Value)} ago is past the {policy.Interval.TotalSeconds:0}s interval but inside the timeout";
        }
        else
        {
            liveness = "alive";
            reason = $"last heartbeat {Age(now, lastHeartbeat.Value)} ago";
        }

        if (job.CancellationRequestedAt.HasValue && liveness is "alive" or "stale")
        {
            reason += "; cancellation was requested and the worker has not yet acknowledged it";
        }

        return new ConsoleJobDiagnosticWorker
        {
            ClaimedBy = operatorView ? job.ClaimedBy : null,
            LastHeartbeatAt = job.LastHeartbeatAt,
            HeartbeatIntervalSeconds = policy.Interval.TotalSeconds,
            HeartbeatTimeoutSeconds = policy.Timeout.TotalSeconds,
            Liveness = liveness,
            LivenessReason = reason
        };
    }

    private async Task<ConsoleJobDiagnosticAttempt> BuildAttemptAsync(
        ClaimsPrincipal user,
        ExecutionJobRecord job,
        bool canExecute,
        CancellationToken cancellationToken)
    {
        var policy = job.RetryPolicy ?? JobRetryPolicy.Default;
        var number = Math.Max(1, job.AttemptCount);
        var terminalRetryable = job.Status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;
        var allowed = false;
        string? disabledReason;
        var requiresApproval = false;

        if (!terminalRetryable)
        {
            disabledReason = job.Status is ExecutionJobStatus.Succeeded
                ? "job succeeded; nothing to retry"
                : "job is not terminal; cancel it first or wait for its outcome";
        }
        else
        {
            var dispatch = await ResolveRetryDispatchAsync(job, throwOnCapabilityFailure: false, cancellationToken).ConfigureAwait(false);
            allowed = canExecute && dispatch.IsAllowed;
            disabledReason = allowed ? null : ResolveRetryDisabledReason(canExecute, dispatch.DisabledReason);
            requiresApproval = approvalGate.CheckApproval(user, new()
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                ResourceId = job.OperationId
            }).IsRequired;
        }

        var effects = job.CommittedEffectReferences.Count > 0
            ? $"{job.CommittedEffectReferences.Count} committed effect(s) from earlier attempts are not undone"
            : "no committed effects are recorded from earlier attempts";
        var wouldRepeat = $"re-dispatches the whole job as attempt {number + 1} of {policy.MaxAttempts} on backend '{job.Spec.Backend}' with the same spec; {effects}";

        return new ConsoleJobDiagnosticAttempt
        {
            Number = number,
            MaxAttempts = policy.MaxAttempts,
            NextRetryAt = job.NextRetryAt,
            Retry = new ConsoleJobDiagnosticRetry
            {
                Allowed = allowed,
                DisabledReason = disabledReason,
                RequiresApproval = requiresApproval,
                WouldRepeat = wouldRepeat
            }
        };
    }

    private static ConsoleJobDiagnosticFailure? BuildFailure(
        ExecutionJobRecord job,
        IReadOnlyList<ExecutionLogEntry> entries,
        string? workspace,
        ConsoleJobDiagnosticAttempt attempt,
        int ordinalOffset)
    {
        if (string.IsNullOrWhiteSpace(job.ErrorMessage) && job.Status != ExecutionJobStatus.Cancelled)
        {
            return null;
        }

        var classification = ClassifyFailureDetailed(job);
        ExecutionLogEntry? errorEntry = null;
        var ordinal = -1;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Level == ExecutionLogLevel.Error)
            {
                errorEntry = entries[i];
                ordinal = ordinalOffset + i;
                break;
            }
        }

        var metadata = errorEntry?.Metadata;
        var exitCode = ParseExitCode(FirstMetadata(metadata, ExitCodeMetadataKeys));
        var stderr = FirstMetadata(metadata, StderrMetadataKeys);
        var recovery = DescribeRecovery(classification, job, attempt, exitCode, stderr);
        var phase = ResolveFailurePhase(job, errorEntry, classification);

        return new ConsoleJobDiagnosticFailure
        {
            Message = WorkspacePathSanitizer.SanitizeForClient(job.ErrorMessage ?? "Job cancelled.", workspace),
            Classification = classification,
            Phase = string.IsNullOrWhiteSpace(phase) ? null : WorkspacePathSanitizer.SanitizeForClient(phase, workspace),
            StepOrdinal = ordinal >= 0 ? ordinal : null,
            OccurredAt = errorEntry?.Timestamp ?? job.CompletedAt,
            ErrorCode = SafeIdentity(FirstMetadata(metadata, ErrorCodeMetadataKeys)),
            NativeExitCode = exitCode,
            StderrExcerpt = stderr is null ? null : WorkspacePathSanitizer.SanitizeForClient(stderr, workspace, MaxStderrExcerptLength),
            Recovery = recovery
        };
    }

    private static string ClassifyFailureDetailed(ExecutionJobRecord job)
    {
        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            return "cancelled";
        }

        var message = job.ErrorMessage ?? string.Empty;
        if (message.Contains("heartbeat expired", StringComparison.OrdinalIgnoreCase))
        {
            return "worker_lost";
        }

        if (message.Contains("exceeded maximum execution duration", StringComparison.OrdinalIgnoreCase)
            || message.Contains("execution timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        return ClassifyFailure(job);
    }

    /// <summary>
    /// The step phase a reader should open. A recorded step phase is kept. Generic
    /// terminal labels (<c>Failed</c>, submission, timeout, heartbeat) are replaced
    /// from the stored reason so invalid input, startup, upload, and expiry do not
    /// all look like an unnamed failure.
    /// </summary>
    private static string? ResolveFailurePhase(
        ExecutionJobRecord job,
        ExecutionLogEntry? errorEntry,
        string classification)
    {
        var fromLog = string.IsNullOrWhiteSpace(errorEntry?.Phase) ? null : errorEntry!.Phase;
        if (fromLog is not null && !IsGenericFailurePhase(fromLog))
        {
            return fromLog;
        }

        var message = job.ErrorMessage ?? string.Empty;
        if (IsStartupFailure(job, message))
        {
            return "starting-worker";
        }

        if (IsUploadFailure(message) || IsExpiredResults(message))
        {
            return "publishing-results";
        }

        if (IsInvalidInput(message) || classification is "timeout" or "worker_lost")
        {
            return "executing";
        }

        if (fromLog is not null)
        {
            return fromLog;
        }

        return string.IsNullOrWhiteSpace(job.CurrentPhase) ? null : job.CurrentPhase;
    }

    private static string DescribeRecovery(
        string classification,
        ExecutionJobRecord job,
        ConsoleJobDiagnosticAttempt attempt,
        int? exitCode,
        string? stderr)
    {
        var message = job.ErrorMessage ?? string.Empty;
        var retry = attempt.Retry.Allowed;
        return classification switch
        {
            "cancelled" => retry
                ? "the job was cancelled; retry re-runs it as a new attempt"
                : "the job was cancelled",
            "worker_lost" => retry
                ? "the worker stopped heartbeating; check worker capacity and logs, then retry"
                : "the worker stopped heartbeating and the retry budget is exhausted; check worker capacity and logs before resubmitting",
            "timeout" => "the job exceeded its timeout policy; reduce the input scope or raise the timeout before resubmitting",
            _ when IsStartupFailure(job, message) => retry
                ? "the worker did not start; check the backend before retrying, since a retry repeats the same spec"
                : "the worker did not start and this job cannot be retried in place; check the backend and resubmit",
            _ when IsInvalidInput(message) => retry
                ? "the input was rejected; fix the input before retrying, since a retry repeats the same spec"
                : "the input was rejected; fix the input and resubmit, since this job cannot be retried in place",
            _ when IsUploadFailure(message) => retry
                ? "results were not stored; retry publishes the same spec again and does not recover a partial upload"
                : "results were not stored and this job cannot be retried in place; resubmit after the store is healthy",
            _ when IsExpiredResults(message) =>
                "the result retention period has elapsed; resubmit the job. A retry cannot fetch expired results",
            _ => retry
                ? (exitCode.HasValue || stderr is not null
                    ? "a step failed with native output; fix the input or tool before retrying, since a retry repeats the same spec"
                    : "fix the reported error before retrying; a retry repeats the same spec")
                : "fix the reported error and resubmit; this job cannot be retried in place"
        };
    }

    private static bool IsGenericFailurePhase(string phase)
        => phase.Equals("Failed", StringComparison.Ordinal)
            || phase.Equals("Failed (abandoned)", StringComparison.Ordinal)
            || phase.Equals("Failed (submission)", StringComparison.Ordinal)
            || phase.Equals("Failed (timeout)", StringComparison.Ordinal)
            || phase.Equals("Failed (heartbeat expired)", StringComparison.Ordinal);

    private static bool IsStartupFailure(ExecutionJobRecord job, string message)
        => string.Equals(job.CurrentPhase, ExecutionJobSubmissionHelper.SubmissionFailurePhase, StringComparison.Ordinal)
            || string.Equals(message, ExecutionJobSubmissionHelper.SubmissionFailureMessage, StringComparison.Ordinal)
            || message.StartsWith("No batch compute backend registered", StringComparison.Ordinal);

    private static bool IsUploadFailure(string message)
        => message.Contains("Failed to persist", StringComparison.Ordinal);

    private static bool IsInvalidInput(string message)
        => message.Contains("invalid input", StringComparison.OrdinalIgnoreCase)
            || message.Contains("missing required input", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpiredResults(string message)
        => message.Contains("retention period has elapsed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("results expired", StringComparison.OrdinalIgnoreCase);

    private async Task<ConsoleJobStep[]> BuildDiagnosticStepsAsync(
        ExecutionJobRecord job,
        IReadOnlyList<ExecutionLogEntry> entries,
        string? workspace,
        int ordinalOffset,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<ConsoleJobStep>();
        }

        var artifactSummary = await BuildStepArtifactSummaryAsync(job, cancellationToken).ConfigureAwait(false);
        var steps = new ConsoleJobStep[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var isLast = i == entries.Count - 1;
            var nextTimestamp = isLast ? job.CompletedAt : entries[i + 1].Timestamp;
            steps[i] = MapStep(job, entries[i], isLast ? null : entries[i + 1], ordinalOffset + i, workspace, artifactSummary, isLast, nextTimestamp);
        }

        return steps;
    }

    private static async Task<ConsoleJobDiagnosticEvents> BuildEventsAsync(
        IExecutionLogStore? logStore,
        ExecutionJobRecord job,
        ExecutionLogTail tail,
        int limit,
        string? eventCursor,
        List<string> truncated,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        if (logStore == null)
        {
            missing.Add("events: no execution log store is registered on this host");
            return new ConsoleJobDiagnosticEvents
            {
                State = "unavailable",
                Items = Array.Empty<ConsoleJobLogEntry>(),
                TotalCount = 0,
                Limit = limit,
                Truncated = false
            };
        }

        var page = eventCursor is null
            ? null
            : await logStore.QueryAsync(job.OperationId,
                new ExecutionLogQuery { Cursor = eventCursor == "start" ? null : eventCursor, Limit = limit },
                cancellationToken).ConfigureAwait(false);
        var selected = page?.Items ?? tail.Items.TakeLast(limit).ToArray();
        var items = selected.Select(MapDiagnosticLogEntry).ToArray();
        // A cursor page is a later read than the tail. Its count has to come from
        // that same read, or a row appended in between is reported without being counted.
        var totalCount = page is null
            ? tail.TotalCount
            : Math.Max(page.TotalCount, items.Length);
        var isTruncated = totalCount > items.Length;
        if (tail.DroppedCount > 0)
        {
            truncated.Add($"events: {tail.DroppedCount} earliest records were discarded by the durable storage limit; paging starts at the first retained event");
        }
        if (isTruncated)
        {
            truncated.Add($"events: {items.Length} of {totalCount} returned; use firstCursor to start paging and nextCursor to continue");
        }

        return new ConsoleJobDiagnosticEvents
        {
            State = "available",
            Items = items,
            TotalCount = totalCount,
            Limit = limit,
            FirstCursor = "start",
            NextCursor = page?.NextCursor,
            Mode = eventCursor is null ? "tail" : "page",
            Truncated = isTruncated
        };
    }

    private static ConsoleJobLogEntry MapDiagnosticLogEntry(ExecutionLogEntry entry)
        => new()
        {
            Timestamp = entry.Timestamp,
            Level = entry.Level.ToString(),
            Message = WorkspacePathSanitizer.SanitizeForClient(entry.Message, null),
            Phase = string.IsNullOrWhiteSpace(entry.Phase) ? null : WorkspacePathSanitizer.SanitizeForClient(entry.Phase, null),
            Metadata = SanitizeStepMetadata(entry.Metadata, null)
        };

    private async Task<ConsoleJobDiagnosticArtifacts> BuildDiagnosticArtifactsAsync(
        ExecutionJobRecord job,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        if (job.ArtifactReferences.Count == 0)
        {
            if (job.Status == ExecutionJobStatus.Succeeded)
            {
                missing.Add("artifacts: the job succeeded without recording any artifact references");
            }

            return new ConsoleJobDiagnosticArtifacts
            {
                State = "none",
                Items = Array.Empty<ConsoleJobArtifact>(),
                AvailableCount = 0,
                ExpiredCount = 0,
                UnavailableCount = 0
            };
        }

        var artifactStore = serviceProvider.GetService<IArtifactStore>();
        var items = new List<ConsoleJobArtifact>(job.ArtifactReferences.Count);
        foreach (var reference in job.ArtifactReferences)
        {
            items.Add(await MapArtifactAsync(reference, artifactStore, cancellationToken).ConfigureAwait(false));
        }

        var available = items.Count(static a => a.Availability is "Available" or "Redacted");
        var expired = items.Count(static a => a.Availability == "Expired");
        var unavailable = items.Count - available - expired;
        var state = available == items.Count ? "complete" : available > 0 ? "partial" : "unavailable";
        if (expired > 0)
        {
            missing.Add($"artifacts: {expired} artifact(s) expired and can no longer be fetched");
        }

        if (unavailable > 0)
        {
            missing.Add($"artifacts: {unavailable} artifact reference(s) could not be resolved");
        }

        return new ConsoleJobDiagnosticArtifacts
        {
            State = state,
            Items = items.ToArray(),
            AvailableCount = available,
            ExpiredCount = expired,
            UnavailableCount = unavailable
        };
    }

    private static string? FirstMetadata(IReadOnlyDictionary<string, string>? metadata, string[] keys)
    {
        if (metadata == null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? ParseExitCode(string? raw)
        => raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;

    // Caller-supplied identities can embed credentials. Withhold URI userinfo
    // before truncation or path redaction, then apply the general secret filters.
    private static string? SafeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return "[REDACTED]";
        }

        var sanitized = WorkspacePathSanitizer.SanitizeForClient(value, null, MaxMetadataValueLength);
        return ContainsSecretToken(sanitized) ? "[REDACTED]" : sanitized;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Age(DateTimeOffset now, DateTimeOffset then)
    {
        var age = now - then;
        if (age < TimeSpan.Zero)
        {
            return "0s";
        }

        return age.TotalSeconds < 120
            ? $"{age.TotalSeconds:0}s"
            : $"{age.TotalMinutes:0}m";
    }

    private static string ResolveServerVersion()
    {
        var asm = typeof(ConsoleJobService).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? asm.GetName().Version?.ToString() ?? "0.0.0" : info;
    }
}
