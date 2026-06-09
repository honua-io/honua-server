// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;

namespace Honua.Server.Features.Admin.Jobs;

internal sealed partial class ConsoleJobService(
    IServiceProvider serviceProvider,
    OperatorApprovalGate approvalGate,
    ILogger<ConsoleJobService> logger,
    TimeProvider timeProvider) : IConsoleJobService
{
    private const int MaxMetadataValueLength = 512;
    private const int MaxArtifactPageSize = 200;

    private static readonly string[] SafeMetadataKeys =
    [
        ExecutionJobParameterKeys.DefinitionId,
        ExecutionJobParameterKeys.Queue,
        ExecutionJobParameterKeys.ResourceRefs,
        ExecutionJobParameterKeys.ParentId,
        ExecutionJobParameterKeys.ChildIds,
        ExecutionJobParameterKeys.Environment,
        ExecutionJobParameterKeys.Server,
        ExecutionJobParameterKeys.ReleaseId,
        ExecutionJobParameterKeys.ChangeSetId,
        ExecutionJobParameterKeys.AlertId,
        ExecutionJobParameterKeys.TraceId,
        ExecutionJobParameterKeys.GeoprocessingPlanId,
        ExecutionJobParameterKeys.GeoprocessingProcessDefinitions,
        ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds,
        ExecutionJobParameterKeys.ShareExportId,
        ExecutionJobParameterKeys.ShareRunId,
        ExecutionJobParameterKeys.ShareDestinationType,
        ExecutionJobParameterKeys.ShareFormat
    ];

    public async Task<ConsoleJobListResponse> ListAsync(
        HttpContext context,
        ConsoleJobQueryRequest request,
        CancellationToken cancellationToken)
    {
        var jobStore = RequireJobStore();
        var page = await jobStore.QueryAsync(ToExecutionQuery(request), cancellationToken).ConfigureAwait(false);
        var items = new List<ConsoleJobSummary>(page.Items.Count);

        foreach (var job in page.Items)
        {
            if (!await CanReadAsync(context, job.OperationId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            items.Add(MapSummary(context, job));
        }

        return new ConsoleJobListResponse
        {
            Items = items.ToArray(),
            NextCursor = page.NextCursor
        };
    }

    public async Task<ConsoleJobDetail?> GetDetailAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        var summary = MapSummary(context, job);
        return new ConsoleJobDetail
        {
            JobId = summary.JobId,
            Kind = summary.Kind,
            Queue = summary.Queue,
            Backend = summary.Backend,
            TargetKind = summary.TargetKind,
            WorkloadName = summary.WorkloadName,
            DefinitionId = summary.DefinitionId,
            Status = summary.Status,
            Priority = summary.Priority,
            RequestedBy = summary.RequestedBy,
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            CompletedAt = summary.CompletedAt,
            DurationMs = summary.DurationMs,
            PercentComplete = summary.PercentComplete,
            CurrentPhase = summary.CurrentPhase,
            AttemptCount = summary.AttemptCount,
            MaxAttempts = summary.MaxAttempts,
            NextRetryAt = summary.NextRetryAt,
            CorrelationId = summary.CorrelationId,
            TraceId = summary.TraceId,
            ResourceRefs = summary.ResourceRefs,
            ArtifactCount = summary.ArtifactCount,
            LatestEvent = summary.LatestEvent,
            Links = summary.Links,
            ProviderOperationId = job.ProviderOperationId,
            ClaimedBy = job.ClaimedBy,
            ClaimedAt = job.ClaimedAt,
            LastHeartbeatAt = job.LastHeartbeatAt,
            CancellationRequestedAt = job.CancellationRequestedAt,
            Warnings = job.Warnings.ToArray(),
            Failure = string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? null
                : new ConsoleJobFailure
                {
                    Message = job.ErrorMessage,
                    Classification = ClassifyFailure(job)
                },
            RetryPolicy = MapRetryPolicy(job.RetryPolicy ?? JobRetryPolicy.Default),
            ParentJobId = GetParameter(job, ExecutionJobParameterKeys.ParentId),
            ChildJobIds = SplitList(GetParameter(job, ExecutionJobParameterKeys.ChildIds)),
            SelectedMetadata = SelectMetadata(job),
            Stages = BuildStages(job),
            Actions = await BuildActionsAsync(context, job, cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<ConsoleJobLogPageResponse?> GetLogsAsync(
        HttpContext context,
        string jobId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        var logStore = serviceProvider.GetService<IExecutionLogStore>();
        if (logStore == null)
        {
            return new ConsoleJobLogPageResponse
            {
                JobId = job.OperationId,
                CorrelationId = job.Audit.CorrelationId,
                State = "unavailable",
                Items = Array.Empty<ConsoleJobLogEntry>()
            };
        }

        var page = await logStore.QueryAsync(
                job.OperationId,
                new ExecutionLogQuery { Cursor = cursor, Limit = limit },
                cancellationToken)
            .ConfigureAwait(false);

        return new ConsoleJobLogPageResponse
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            State = "available",
            Items = page.Items.Select(MapLogEntry).ToArray(),
            NextCursor = page.NextCursor
        };
    }

    public async Task<ConsoleJobArtifactPageResponse?> GetArtifactsAsync(
        HttpContext context,
        string jobId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        if (!TryDecodeOffsetCursor(cursor, out var offset, out var error))
        {
            throw new ArgumentException(error, nameof(cursor));
        }

        var effectiveLimit = Math.Clamp(limit, 1, MaxArtifactPageSize);
        var artifactRefs = job.ArtifactReferences
            .Skip((int)offset)
            .Take(effectiveLimit + 1)
            .ToArray();

        var itemCount = Math.Min(artifactRefs.Length, effectiveLimit);
        var items = new ConsoleJobArtifact[itemCount];
        var artifactStore = serviceProvider.GetService<IArtifactStore>();

        for (var i = 0; i < itemCount; i++)
        {
            items[i] = await MapArtifactAsync(artifactRefs[i], artifactStore, cancellationToken).ConfigureAwait(false);
        }

        return new ConsoleJobArtifactPageResponse
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            Items = items,
            NextCursor = artifactRefs.Length > effectiveLimit ? EncodeOffsetCursor(offset + effectiveLimit) : null
        };
    }

    public async Task<ConsoleJobActionsResponse?> GetActionsAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        return new ConsoleJobActionsResponse
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            Actions = await BuildActionsAsync(context, job, cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<ConsoleJobControlResponse?> CancelAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var jobStore = RequireJobStore();
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            await RemoveFromQueueIfPresentAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            return ControlResponse(job, "cancel", "Job is already cancelled.");
        }

        if (ExecutionJobCancellationHelper.IsTerminal(job.Status))
        {
            throw new ConsoleJobConflictException("Terminal jobs cannot be cancelled.");
        }

        var approval = approvalGate.EvaluateApproval(
            context,
            OperatorResourceType.Job,
            OperatorOperation.Execute,
            job.OperationId,
            isDestructive: true);
        if (approval != null)
        {
            throw new ConsoleJobApprovalRequiredException(approval);
        }

        if (job.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.HasSubmittedProviderMarker(job))
        {
            var preResult = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(
                    jobStore,
                    job,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preResult.Outcome == PreSubmissionCancelOutcome.Cancelled && preResult.Job != null)
            {
                await RemoveFromQueueIfPresentAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
                await NotifyTerminalCallbacksAsync(preResult.Job).ConfigureAwait(false);
                JobControlAction(logger, "cancel", job.OperationId, preResult.Job.Status.ToString());
                return ControlResponse(preResult.Job, "cancel", "Job cancelled before submission.");
            }

            ThrowForCancelOutcome(preResult.Outcome);
        }

        _ = serviceProvider.GetServices<IJobCancellationNotifier>().CancelAny(job.OperationId);

        var backends = serviceProvider.GetServices<IBatchComputeBackend>();
        var backend = backends.Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend != null && ExecutionJobCancellationHelper.HasSubmittedProviderMarker(job))
        {
            var capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!capabilities.SupportsCancellation)
            {
                throw new ConsoleJobConflictException("The selected job backend does not support cancellation.");
            }

            var stamp = await ExecutionJobCancellationHelper.TryStampRemoteCancelRequestedAtAsync(
                    jobStore,
                    job,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (stamp.Outcome is RemoteCancelStampOutcome.TerminalConflict)
            {
                throw new ConsoleJobConflictException("Terminal jobs cannot be cancelled.");
            }

            var stampedJob = stamp.Job ?? job;
            var observation = await backend.CancelAsync(stampedJob, cancellationToken).ConfigureAwait(false);
            var applied = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
                    jobStore,
                    stampedJob,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (applied.Outcome == BackendCancelApplyOutcome.Applied && applied.Job != null)
            {
                if (ExecutionJobCancellationHelper.IsTerminal(applied.Job.Status))
                {
                    await RemoveFromQueueIfPresentAsync(applied.Job.OperationId, cancellationToken).ConfigureAwait(false);
                    await NotifyTerminalCallbacksAsync(applied.Job).ConfigureAwait(false);
                }

                JobControlAction(logger, "cancel", job.OperationId, applied.Job.Status.ToString());
                return ControlResponse(applied.Job, "cancel", "Job cancellation requested.");
            }
        }

        var result = await ExecutionJobCancellationHelper.TryApplyAsync(
                jobStore,
                job.OperationId,
                job,
                "Cancelled by operator",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.State switch
        {
            ExecutionJobCancellationState.Cancelled when result.Job != null => await CompleteLocalCancelAsync(result.Job, cancellationToken).ConfigureAwait(false),
            ExecutionJobCancellationState.CancellationRequested when result.Job != null => ControlResponse(result.Job, "cancel", "Job cancellation requested."),
            ExecutionJobCancellationState.TerminalConflict => throw new ConsoleJobConflictException("Terminal jobs cannot be cancelled."),
            _ => throw new ConsoleJobConflictException("Job cancellation could not be confirmed.")
        };
    }

    public async Task<ConsoleJobControlResponse?> RetryAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var jobStore = RequireJobStore();
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        if (job.Status is not (ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled))
        {
            throw new ConsoleJobConflictException("Only failed or cancelled jobs can be retried.");
        }

        var policy = job.RetryPolicy ?? JobRetryPolicy.Default;
        var policyDisabledReason = ResolveRetryPolicyDisabledReason(policy, job.AttemptCount);
        if (policyDisabledReason != null)
        {
            ThrowForRetryDisabledReason(policyDisabledReason);
        }

        var approval = approvalGate.EvaluateApproval(
            context,
            OperatorResourceType.Job,
            OperatorOperation.Execute,
            job.OperationId);
        if (approval != null)
        {
            throw new ConsoleJobApprovalRequiredException(approval);
        }

        var now = timeProvider.GetUtcNow();
        var retryDispatch = await ResolveRetryDispatchAsync(
                job,
                throwOnCapabilityFailure: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!retryDispatch.IsAllowed)
        {
            ThrowForRetryDisabledReason(retryDispatch.DisabledReason);
        }

        var retried = job with
        {
            Status = ExecutionJobStatus.Queued,
            UpdatedAt = now,
            CompletedAt = null,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            ProviderOperationId = null,
            CancellationRequestedAt = null,
            ErrorMessage = null,
            PercentComplete = null,
            NextRetryAt = retryDispatch.IsRemote ? now : null,
            Warnings = Array.Empty<string>(),
            ArtifactReferences = Array.Empty<string>(),
            CurrentPhase = "Queued for manual retry"
        };

        if (!await jobStore.TrySetAsync(retried, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new ConsoleJobConflictException("Job retry could not be confirmed because the record changed.");
        }

        if (retryDispatch.Queue != null)
        {
            try
            {
                await retryDispatch.Queue.RequeueAsync(
                        job.OperationId,
                        job.Priority,
                        visibleAfter: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                JobQueueRetryFailed(logger, job.OperationId, ex);
                if (await TryRestoreTerminalStateAfterRetryQueueFailureAsync(
                        jobStore,
                        job,
                        retried,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    await RemoveFromQueueIfPresentAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
                }

                throw new ConsoleJobServiceUnavailableException("Job retry could not be queued.");
            }
        }

        JobControlAction(logger, "retry", job.OperationId, retried.Status.ToString());
        return ControlResponse(retried, "retry", "Job queued for manual retry.");
    }

    private async Task<ConsoleJobControlResponse> CompleteLocalCancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        await RemoveFromQueueIfPresentAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        await NotifyTerminalCallbacksAsync(job).ConfigureAwait(false);
        JobControlAction(logger, "cancel", job.OperationId, job.Status.ToString());
        return ControlResponse(job, "cancel", "Job cancelled.");
    }

    // Operator cancel can be the only terminal transition a job ever receives (a queued job cancelled
    // before worker pickup never reaches the worker/reconciler notify paths). Fire the registered
    // terminal callbacks here, mirroring JobExecutionService/JobReconciliationService, so feature
    // run/progress stores (e.g. Share export run history) reconcile to the job's terminal state.
    // Callbacks are best-effort and kind-guarded. Use CancellationToken.None: the cancel is already
    // durably committed, so a disconnected client must not strand a backing run at a pre-terminal
    // status, consistent with the trigger path's commit-once side effects.
    private async Task NotifyTerminalCallbacksAsync(ExecutionJobRecord job)
    {
        foreach (var callback in serviceProvider.GetServices<IJobTerminalCallback>())
        {
            try
            {
                await callback.OnTerminalAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TerminalCallbackFailed(logger, job.OperationId, ex);
            }
        }
    }

    private async Task<ExecutionJobRecord?> GetAuthorizedJobAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await RequireJobStore().GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null || !await CanReadAsync(context, job.OperationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return job;
    }

    private IExecutionJobStore RequireJobStore()
        => serviceProvider.GetService<IExecutionJobStore>()
            ?? throw new ConsoleJobServiceUnavailableException("Execution job store is unavailable.");

    private async Task<bool> CanReadAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var decision = await approvalGate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read,
                ResourceId = jobId
            },
            cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
    }

    private static ExecutionJobQuery ToExecutionQuery(ConsoleJobQueryRequest request)
        => new()
        {
            Statuses = request.Statuses,
            Kind = request.Kind,
            Backend = request.Backend,
            Queue = request.Queue,
            RequestedBy = request.RequestedBy,
            CorrelationId = request.CorrelationId,
            TraceId = request.TraceId,
            DefinitionId = request.DefinitionId,
            ResourceRef = request.ResourceRef,
            Environment = request.Environment,
            Server = request.Server,
            ReleaseId = request.ReleaseId,
            ChangeSetId = request.ChangeSetId,
            AlertId = request.AlertId,
            CreatedFrom = request.CreatedFrom,
            CreatedTo = request.CreatedTo,
            Cursor = request.Cursor,
            Limit = request.Limit
        };

    private static ConsoleJobSummary MapSummary(HttpContext context, ExecutionJobRecord job)
    {
        var policy = job.RetryPolicy ?? JobRetryPolicy.Default;
        var definitionId = GetDefinitionId(job);
        var resourceRefs = GetResourceRefs(job);

        return new ConsoleJobSummary
        {
            JobId = job.OperationId,
            Kind = job.Spec.Kind.ToString(),
            Queue = ExecutionJobMetadata.ResolveQueue(job),
            Backend = job.Spec.Backend,
            TargetKind = job.Spec.TargetKind.ToString(),
            WorkloadName = job.Spec.WorkloadName,
            DefinitionId = definitionId,
            Status = job.Status.ToString(),
            Priority = job.Priority.ToString(),
            RequestedBy = job.Audit.RequestedBy,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            CompletedAt = job.CompletedAt,
            DurationMs = ComputeDurationMs(job),
            PercentComplete = job.PercentComplete,
            CurrentPhase = job.CurrentPhase,
            AttemptCount = job.AttemptCount,
            MaxAttempts = policy.MaxAttempts,
            NextRetryAt = job.NextRetryAt,
            CorrelationId = job.Audit.CorrelationId,
            TraceId = GetParameter(job, ExecutionJobParameterKeys.TraceId),
            ResourceRefs = resourceRefs,
            ArtifactCount = job.ArtifactReferences.Count,
            LatestEvent = MapLatestEvent(job),
            Links = BuildLinks(context, job)
        };
    }

    private static ConsoleJobLatestEvent MapLatestEvent(ExecutionJobRecord job)
        => new()
        {
            Type = job.Status switch
            {
                ExecutionJobStatus.Queued => "job.queued",
                ExecutionJobStatus.Provisioning => "job.provisioning",
                ExecutionJobStatus.Running => "job.running",
                ExecutionJobStatus.Succeeded => "job.completed",
                ExecutionJobStatus.Failed => "job.failed",
                ExecutionJobStatus.Cancelled => "job.cancelled",
                _ => "job.updated"
            },
            OccurredAt = job.CompletedAt ?? job.UpdatedAt,
            Status = job.Status.ToString(),
            Phase = job.CurrentPhase,
            Message = job.Status == ExecutionJobStatus.Failed ? job.ErrorMessage : null
        };

    private static ConsoleJobLogEntry MapLogEntry(ExecutionLogEntry entry)
        => new()
        {
            Timestamp = entry.Timestamp,
            Level = entry.Level.ToString(),
            Message = entry.Message,
            Phase = entry.Phase,
            Metadata = entry.Metadata is null ? null : new Dictionary<string, string>(entry.Metadata)
        };

    private async Task<ConsoleJobArtifact> MapArtifactAsync(
        string reference,
        IArtifactStore? artifactStore,
        CancellationToken cancellationToken)
    {
        if (artifactStore != null)
        {
            try
            {
                var artifact = await artifactStore.GetAsync(reference, cancellationToken).ConfigureAwait(false);
                if (artifact == null)
                {
                    return new ConsoleJobArtifact
                    {
                        ArtifactId = reference,
                        Availability = "Unavailable",
                        Message = "Artifact metadata is not available."
                    };
                }

                return MapStoredArtifact(artifact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ArtifactLookupFailed(logger, reference, ex);
                return new ConsoleJobArtifact
                {
                    ArtifactId = reference,
                    Availability = "ProviderError",
                    Message = "Artifact metadata could not be loaded."
                };
            }
        }

        return new ConsoleJobArtifact
        {
            ArtifactId = reference,
            Availability = IsSafeProviderLink(reference) ? "Available" : "Redacted",
            ProviderLink = IsSafeProviderLink(reference) ? reference : null,
            Message = IsSafeProviderLink(reference) ? null : "Artifact reference is not safe to expose."
        };
    }

    private static ConsoleJobArtifact MapStoredArtifact(Artifact artifact)
    {
        if (artifact.State is ArtifactLifecycleState.Expired or ArtifactLifecycleState.Deleted)
        {
            return new ConsoleJobArtifact
            {
                ArtifactId = artifact.ArtifactId,
                Availability = "Expired",
                Kind = artifact.Kind.ToString(),
                Label = artifact.Label,
                ContentType = artifact.ContentType,
                SizeBytes = artifact.SizeBytes
            };
        }

        var safe = IsSafeProviderLink(artifact.Uri);
        return new ConsoleJobArtifact
        {
            ArtifactId = artifact.ArtifactId,
            Availability = safe ? "Available" : "Redacted",
            Kind = artifact.Kind.ToString(),
            Label = artifact.Label,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes,
            ProviderLink = safe ? artifact.Uri : null,
            Message = safe ? null : "Artifact URI is not safe to expose."
        };
    }

    private async Task<ConsoleJobActionDescriptor[]> BuildActionsAsync(
        HttpContext context,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var executeDecision = await approvalGate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Execute,
                ResourceId = job.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        var cancelApproval = approvalGate.CheckApproval(context.User, new()
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute,
            ResourceId = job.OperationId,
            IsDestructive = true
        });
        var retryApproval = approvalGate.CheckApproval(context.User, new()
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute,
            ResourceId = job.OperationId
        });
        var canExecute = executeDecision.IsAllowed;
        var actions = new List<ConsoleJobActionDescriptor>(capacity: 2);
        var links = BuildLinks(context, job);

        if (job.Status is ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running)
        {
            actions.Add(new ConsoleJobActionDescriptor
            {
                Name = "cancel",
                Method = "POST",
                Href = links.Cancel!,
                Allowed = canExecute,
                DisabledReason = canExecute ? null : "execute permission required",
                RequiresApproval = cancelApproval.IsRequired
            });
        }

        if (job.Status is ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled)
        {
            var retryDispatch = await ResolveRetryDispatchAsync(
                    job,
                    throwOnCapabilityFailure: false,
                    cancellationToken)
                .ConfigureAwait(false);
            var allowed = canExecute && retryDispatch.IsAllowed;
            actions.Add(new ConsoleJobActionDescriptor
            {
                Name = "retry",
                Method = "POST",
                Href = links.Retry!,
                Allowed = allowed,
                DisabledReason = allowed
                    ? null
                    : ResolveRetryDisabledReason(canExecute, retryDispatch.DisabledReason),
                RequiresApproval = retryApproval.IsRequired
            });
        }

        return actions.ToArray();
    }

    private async Task<RetryDispatch> ResolveRetryDispatchAsync(
        ExecutionJobRecord job,
        bool throwOnCapabilityFailure,
        CancellationToken cancellationToken)
    {
        var retryPolicy = job.RetryPolicy ?? JobRetryPolicy.Default;
        var policyDisabledReason = ResolveRetryPolicyDisabledReason(retryPolicy, job.AttemptCount);
        if (policyDisabledReason != null)
        {
            return RetryDispatch.Disabled(policyDisabledReason);
        }

        if (IsLocalBackend(job))
        {
            var queue = serviceProvider.GetService<IJobQueue>();
            return queue == null
                ? RetryDispatch.Disabled(RetryDisabledReasons.JobQueueUnavailable)
                : RetryDispatch.Local(queue);
        }

        var backend = serviceProvider
            .GetServices<IBatchComputeBackend>()
            .Resolve(job.Spec.Backend, job.Spec.TargetKind);
        if (backend == null)
        {
            return RetryDispatch.Disabled(RetryDisabledReasons.BackendUnavailable);
        }

        BatchComputeBackendCapabilities capabilities;
        try
        {
            capabilities = await backend.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobRetryCapabilityLookupFailed(logger, job.OperationId, ex);
            if (throwOnCapabilityFailure)
            {
                throw new ConsoleJobServiceUnavailableException("Job backend retry capabilities could not be loaded.");
            }

            return RetryDispatch.Disabled(RetryDisabledReasons.BackendCapabilityUnavailable);
        }

        return capabilities.SupportsRetry
            ? RetryDispatch.Remote()
            : RetryDispatch.Disabled(RetryDisabledReasons.BackendRetryUnsupported);
    }

    // Determines whether the retry policy alone forbids a manual retry, independent of dispatch
    // capability. A no-retry policy (MaxAttempts <= 1, e.g. JobRetryPolicy.None) forbids manual retry
    // regardless of AttemptCount, including a job that reached a terminal state before its first
    // attempt (AttemptCount == 0) such as a cancel before worker pickup or a dispatch rollback. There
    // ShouldRetry(0) would otherwise be true for MaxAttempts == 1, and re-queuing the job would re-run
    // work whose downstream first-terminal state (e.g. a Share export run reconciled first-terminal-
    // wins) cannot reopen, desyncing the job and the run. Returns null when the policy permits a retry.
    private static string? ResolveRetryPolicyDisabledReason(JobRetryPolicy policy, int attemptCount)
    {
        if (policy.MaxAttempts <= 1)
        {
            return RetryDisabledReasons.NotRetryable;
        }

        return policy.ShouldRetry(attemptCount)
            ? null
            : RetryDisabledReasons.RetryBudgetExhausted;
    }

    private static string ResolveRetryDisabledReason(bool canExecute, string? retryDisabledReason)
    {
        if (!canExecute)
        {
            return "execute permission required";
        }

        return retryDisabledReason switch
        {
            RetryDisabledReasons.JobQueueUnavailable => "job queue unavailable",
            RetryDisabledReasons.BackendUnavailable => "job backend unavailable",
            RetryDisabledReasons.BackendCapabilityUnavailable => "job backend retry capability unavailable",
            RetryDisabledReasons.BackendRetryUnsupported => "job backend does not support retry",
            RetryDisabledReasons.RetryBudgetExhausted => "retry budget exhausted",
            RetryDisabledReasons.NotRetryable => "not retryable",
            _ => "not retryable"
        };
    }

    private static void ThrowForRetryDisabledReason(string? retryDisabledReason)
    {
        throw retryDisabledReason switch
        {
            RetryDisabledReasons.JobQueueUnavailable => new ConsoleJobServiceUnavailableException("Job queue is unavailable."),
            RetryDisabledReasons.BackendUnavailable => new ConsoleJobServiceUnavailableException("Job backend is unavailable."),
            RetryDisabledReasons.BackendCapabilityUnavailable => new ConsoleJobServiceUnavailableException("Job backend retry capabilities could not be loaded."),
            RetryDisabledReasons.BackendRetryUnsupported => new ConsoleJobConflictException("The selected job backend does not support retry."),
            RetryDisabledReasons.RetryBudgetExhausted => new ConsoleJobConflictException("The retry policy budget is exhausted."),
            RetryDisabledReasons.NotRetryable => new ConsoleJobConflictException("Job is not retryable."),
            _ => new ConsoleJobConflictException("Job is not retryable.")
        };
    }

    private static bool IsLocalBackend(ExecutionJobRecord job)
        => string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal);

    private static ConsoleJobLinks BuildLinks(HttpContext context, ExecutionJobRecord job)
    {
        var pathBase = context.Request.PathBase.Value ?? string.Empty;
        var basePath = $"{pathBase}/api/v1/admin/jobs/{Uri.EscapeDataString(job.OperationId)}";
        var correlation = job.Audit.CorrelationId;

        return new ConsoleJobLinks
        {
            Self = basePath,
            Logs = $"{basePath}/logs",
            Artifacts = $"{basePath}/artifacts",
            Actions = $"{basePath}/actions",
            Cancel = $"{basePath}/cancel",
            Retry = $"{basePath}/retry",
            EventsByJob = $"{pathBase}/api/v1/admin/observability/events?kind=job&operationId={Uri.EscapeDataString(job.OperationId)}",
            EventsByCorrelation = string.IsNullOrWhiteSpace(correlation)
                ? null
                : $"{pathBase}/api/v1/admin/observability/events?kind=job&correlationId={Uri.EscapeDataString(correlation)}"
        };
    }

    private static ConsoleJobStage[] BuildStages(ExecutionJobRecord job)
    {
        var status = job.Status.ToString();
        return
        [
            new ConsoleJobStage
            {
                Name = string.IsNullOrWhiteSpace(job.CurrentPhase) ? status : job.CurrentPhase,
                Status = status,
                StartedAt = job.ClaimedAt ?? job.CreatedAt,
                CompletedAt = job.CompletedAt,
                PercentComplete = job.PercentComplete
            }
        ];
    }

    private static ConsoleJobRetryPolicy MapRetryPolicy(JobRetryPolicy policy)
        => new()
        {
            MaxAttempts = policy.MaxAttempts,
            Strategy = policy.Strategy.ToString(),
            BaseDelayMs = (long)policy.BaseDelay.TotalMilliseconds,
            MaxDelayMs = (long)policy.MaxDelay.TotalMilliseconds
        };

    private static ConsoleJobControlResponse ControlResponse(ExecutionJobRecord job, string action, string message)
        => new()
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            Action = action,
            Status = job.Status.ToString(),
            Message = message
        };

    private async Task RemoveFromQueueIfPresentAsync(string jobId, CancellationToken cancellationToken)
    {
        var queue = serviceProvider.GetService<IJobQueue>();
        if (queue == null)
        {
            return;
        }

        try
        {
            await queue.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobQueueRemovalFailed(logger, jobId, ex);
        }
    }

    private async Task<bool> TryRestoreTerminalStateAfterRetryQueueFailureAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord terminalJob,
        ExecutionJobRecord retryCandidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await jobStore.GetAsync(terminalJob.OperationId, cancellationToken).ConfigureAwait(false);
            if (!IsUnclaimedManualRetryCandidate(current, retryCandidate))
            {
                return false;
            }

            var restored = terminalJob with { Version = current!.Version };
            if (await jobStore.TrySetAsync(restored, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            var latest = await jobStore.GetAsync(terminalJob.OperationId, cancellationToken).ConfigureAwait(false);
            JobRetryCompensationConflict(
                logger,
                terminalJob.OperationId,
                latest?.Status.ToString() ?? "missing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobRetryCompensationFailed(logger, terminalJob.OperationId, ex);
        }

        return false;
    }

    private static bool IsUnclaimedManualRetryCandidate(ExecutionJobRecord? current, ExecutionJobRecord retryCandidate)
        => current != null
            && current.Status == ExecutionJobStatus.Queued
            && current.AttemptCount == retryCandidate.AttemptCount
            && current.CompletedAt == null
            && current.ClaimedBy == null
            && current.ClaimedAt == null
            && current.LastHeartbeatAt == null
            && current.ProviderOperationId == null
            && current.CancellationRequestedAt == null
            && current.NextRetryAt == retryCandidate.NextRetryAt
            && string.Equals(current.CurrentPhase, retryCandidate.CurrentPhase, StringComparison.Ordinal);

    private static Dictionary<string, string> SelectMetadata(ExecutionJobRecord job)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in SafeMetadataKeys)
        {
            if (!job.Spec.Parameters.TryGetValue(key, out var value) || !IsSafeMetadataValue(key, value))
            {
                continue;
            }

            selected[key] = value.Length <= MaxMetadataValueLength
                ? value
                : value[..MaxMetadataValueLength];
        }

        return selected;
    }

    private static bool IsSafeMetadataValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !ContainsSecretToken(key) && !ContainsSecretToken(value);
    }

    private static bool ContainsSecretToken(string value)
        => value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || value.Contains("token", StringComparison.OrdinalIgnoreCase)
            || value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static string? GetDefinitionId(ExecutionJobRecord job)
        => GetParameter(job, ExecutionJobParameterKeys.DefinitionId)
            ?? GetParameter(job, ExecutionJobParameterKeys.GeoprocessingPlanId);

    private static string[] GetResourceRefs(ExecutionJobRecord job)
        => SplitList(GetParameter(job, ExecutionJobParameterKeys.ResourceRefs));

    private static string[] SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(
                    ExecutionJobParameterKeys.MetadataListSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? GetParameter(ExecutionJobRecord job, string key)
        => ExecutionJobMetadata.GetParameter(job, key);

    private static long? ComputeDurationMs(ExecutionJobRecord job)
    {
        var end = job.CompletedAt ?? job.UpdatedAt;
        var duration = end - job.CreatedAt;
        return duration < TimeSpan.Zero ? null : (long)duration.TotalMilliseconds;
    }

    private static string ClassifyFailure(ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Cancelled
            ? "cancelled"
            : "execution_failed";

    private static void ThrowForCancelOutcome(PreSubmissionCancelOutcome outcome)
    {
        throw outcome switch
        {
            PreSubmissionCancelOutcome.TerminalConflict => new ConsoleJobConflictException("Terminal jobs cannot be cancelled."),
            PreSubmissionCancelOutcome.Missing => new ConsoleJobNotFoundException(),
            _ => new ConsoleJobConflictException("Job cancellation could not be confirmed.")
        };
    }

    private static bool IsSafeProviderLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsSecretToken(value))
        {
            return false;
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith('/') ||
            value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https";
        }

        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    private static bool TryDecodeOffsetCursor(string? cursor, out long offset, out string error)
    {
        offset = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0)
            {
                error = "Invalid artifact cursor.";
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            error = "Invalid artifact cursor.";
            return false;
        }
    }

    private static string EncodeOffsetCursor(long offset)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    [LoggerMessage(117000, LogLevel.Information, "Job control action {Action} applied to {JobId}; status={Status}")]
    private static partial void JobControlAction(ILogger logger, string action, string jobId, string status);

    [LoggerMessage(117001, LogLevel.Warning, "Job artifact lookup failed for reference {ArtifactReference}")]
    private static partial void ArtifactLookupFailed(ILogger logger, string artifactReference, Exception exception);

    [LoggerMessage(117002, LogLevel.Error, "Manual retry queue write failed for job {JobId}")]
    private static partial void JobQueueRetryFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(117003, LogLevel.Warning, "Job queue removal failed for job {JobId}")]
    private static partial void JobQueueRemovalFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(117004, LogLevel.Warning, "Manual retry compensation conflicted for job {JobId}; latest status={Status}")]
    private static partial void JobRetryCompensationConflict(ILogger logger, string jobId, string status);

    [LoggerMessage(117005, LogLevel.Warning, "Manual retry compensation failed for job {JobId}")]
    private static partial void JobRetryCompensationFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(117006, LogLevel.Warning, "Manual retry capability lookup failed for job {JobId}")]
    private static partial void JobRetryCapabilityLookupFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(117007, LogLevel.Warning, "Terminal callback failed for job {JobId} after operator cancel; feature run/progress state may be stale until TTL or reconcile")]
    private static partial void TerminalCallbackFailed(ILogger logger, string jobId, Exception exception);

    private static class RetryDisabledReasons
    {
        public const string JobQueueUnavailable = "job_queue_unavailable";
        public const string BackendUnavailable = "backend_unavailable";
        public const string BackendCapabilityUnavailable = "backend_capability_unavailable";
        public const string BackendRetryUnsupported = "backend_retry_unsupported";
        public const string RetryBudgetExhausted = "retry_budget_exhausted";
        public const string NotRetryable = "not_retryable";
    }

    private readonly record struct RetryDispatch(
        bool IsAllowed,
        string? DisabledReason,
        IJobQueue? Queue,
        bool IsRemote)
    {
        public static RetryDispatch Local(IJobQueue queue) => new(true, null, queue, false);

        public static RetryDispatch Remote() => new(true, null, null, true);

        public static RetryDispatch Disabled(string reason) => new(false, reason, null, false);
    }
}

internal sealed class ConsoleJobServiceUnavailableException(string message) : Exception(message);

internal sealed class ConsoleJobConflictException(string message) : Exception(message);

internal sealed class ConsoleJobNotFoundException : Exception
{
}

internal sealed class ConsoleJobApprovalRequiredException(IResult result) : Exception("Approval required")
{
    public IResult Result { get; } = result;
}
