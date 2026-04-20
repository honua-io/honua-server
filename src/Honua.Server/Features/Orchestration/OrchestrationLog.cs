// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Source-generated logging entries for the orchestration feature slice.
/// Event IDs reserved in the 8100-8199 range.
/// </summary>
internal static partial class OrchestrationLog
{
    [LoggerMessage(8100, LogLevel.Information, "Created workflow run {RunId} for workflow {WorkflowId} ({TriggerKind})")]
    public static partial void WorkflowRunCreated(
        ILogger logger,
        string runId,
        string workflowId,
        string triggerKind);

    [LoggerMessage(8101, LogLevel.Information, "Workflow run {RunId} completed with status {Status} ({StepsCompleted}/{TotalSteps} steps)")]
    public static partial void WorkflowRunCompleted(
        ILogger logger,
        string runId,
        string status,
        int stepsCompleted,
        int totalSteps);

    [LoggerMessage(8102, LogLevel.Debug, "Submitted workflow step {RunId}:{StepId} as job {JobId} (attempt {Attempt})")]
    public static partial void WorkflowStepSubmitted(
        ILogger logger,
        string runId,
        string stepId,
        string jobId,
        int attempt);

    [LoggerMessage(8103, LogLevel.Debug, "Workflow step {RunId}:{StepId} reached terminal status {Status}")]
    public static partial void WorkflowStepCompleted(
        ILogger logger,
        string runId,
        string stepId,
        string status);

    [LoggerMessage(8104, LogLevel.Information, "Workflow step {RunId}:{StepId} scheduled for retry attempt {Attempt} at {NextAttemptAt:o}")]
    public static partial void WorkflowStepRetrying(
        ILogger logger,
        string runId,
        string stepId,
        int attempt,
        DateTimeOffset nextAttemptAt);

    [LoggerMessage(8105, LogLevel.Information, "Workflow step {RunId}:{StepId} was skipped by policy (reason: {Reason})")]
    public static partial void WorkflowStepSkipped(
        ILogger logger,
        string runId,
        string stepId,
        string reason);

    [LoggerMessage(8106, LogLevel.Debug, "Resolved binding for {RunId}:{StepId} target {TargetKey} to artifact {ArtifactRef}")]
    public static partial void InputBindingResolved(
        ILogger logger,
        string runId,
        string stepId,
        string targetKey,
        string artifactRef);

    [LoggerMessage(8107, LogLevel.Warning, "Failed to resolve binding for {RunId}:{StepId} target {TargetKey}: {Reason}")]
    public static partial void InputBindingFailed(
        ILogger logger,
        string runId,
        string stepId,
        string targetKey,
        string reason);

    [LoggerMessage(8108, LogLevel.Information, "Scheduler triggered workflow {WorkflowId} at {FireTime:o}")]
    public static partial void SchedulerTriggered(
        ILogger logger,
        string workflowId,
        DateTimeOffset fireTime);

    [LoggerMessage(8109, LogLevel.Debug, "Reconciliation lease was lost for workflow run {RunId}")]
    public static partial void ReconciliationLeaseLost(
        ILogger logger,
        string runId);

    [LoggerMessage(8110, LogLevel.Warning, "Workflow reconciliation failed for run {RunId}")]
    public static partial void ReconciliationFailed(
        ILogger logger,
        string runId,
        Exception exception);

    [LoggerMessage(8111, LogLevel.Warning, "Orchestration poll loop failed")]
    public static partial void PollLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(8112, LogLevel.Information, "Started orchestration reconciler background service")]
    public static partial void ReconcilerBackgroundServiceStarted(ILogger logger);

    [LoggerMessage(8113, LogLevel.Information, "Started orchestration scheduler background service")]
    public static partial void SchedulerBackgroundServiceStarted(ILogger logger);

    [LoggerMessage(8114, LogLevel.Warning, "Orchestration scheduler tick failed")]
    public static partial void SchedulerTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(8115, LogLevel.Warning, "Workflow step {RunId}:{StepId} failed: {Reason}")]
    public static partial void WorkflowStepFailed(
        ILogger logger,
        string runId,
        string stepId,
        string reason);

    [LoggerMessage(8116, LogLevel.Warning, "Scheduler skipping workflow {WorkflowId}: cron expression '{CronExpression}' or time zone '{TimeZoneId}' could not be compiled; runs will not fire until the definition is corrected.")]
    public static partial void SchedulerDefinitionInvalid(
        ILogger logger,
        string workflowId,
        string cronExpression,
        string timeZoneId);

    [LoggerMessage(8117, LogLevel.Warning, "Failed to cancel underlying job {JobId} for workflow step {RunId}:{StepId}; step remains non-terminal and reconciliation will retry cancellation on a later tick")]
    public static partial void WorkflowStepCancelJobFailed(
        ILogger logger,
        string runId,
        string stepId,
        string jobId,
        Exception exception);

    [LoggerMessage(8118, LogLevel.Warning, "Transient job-observation failure for step {RunId}:{StepId} job {JobId}; reconcile will retry")]
    public static partial void WorkflowStepObservationTransientFailure(
        ILogger logger,
        string runId,
        string stepId,
        string jobId,
        Exception exception);

    [LoggerMessage(8119, LogLevel.Information, "Cancel request for workflow run {RunId} could not acquire reconcile lease within the polling window; caller should retry")]
    public static partial void WorkflowCancelLeaseContention(ILogger logger, string runId);

    [LoggerMessage(8120, LogLevel.Warning, "Workflow step {RunId}:{StepId} succeeded but artifact retrieval failed and downstream bindings cannot be satisfied; failing step so the workflow does not silently produce a null-artifact success")]
    public static partial void WorkflowStepArtifactsUnavailableForBoundDependents(
        ILogger logger,
        string runId,
        string stepId,
        Exception exception);

    [LoggerMessage(8121, LogLevel.Error, "Workflow run {RunId} step-set does not match definition {WorkflowId}: {MismatchDetail}. Run will be failed deterministically.")]
    public static partial void DefinitionStepSetMismatch(
        ILogger logger,
        string runId,
        string workflowId,
        string mismatchDetail);

    [LoggerMessage(8122, LogLevel.Warning, "Progress projection failed for workflow run {RunId}; the authoritative run state is durable but the progress view may be stale")]
    public static partial void ProgressProjectionFailed(
        ILogger logger,
        string runId,
        Exception exception);
}
