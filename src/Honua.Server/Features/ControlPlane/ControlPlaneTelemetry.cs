// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.ControlPlane;

/// <summary>
/// OpenTelemetry instrumentation for control-plane workflow operations.
/// </summary>
internal static class ControlPlaneTelemetry
{
    internal static class Activities
    {
        public const string WorkflowCreate = "honua.controlplane.workflow.create";
        public const string WorkflowRollback = "honua.controlplane.workflow.rollback";
        public const string WorkflowReconcile = "honua.controlplane.workflow.reconcile";
        public const string BackendStart = "honua.controlplane.backend.start";
        public const string BackendObserve = "honua.controlplane.backend.observe";
        public const string BackendRollback = "honua.controlplane.backend.rollback";
        public const string ExecutionStart = "honua.controlplane.execution.start";
        public const string ExecutionObserve = "honua.controlplane.execution.observe";
        public const string ExecutionCancel = "honua.controlplane.execution.cancel";
        public const string ExecutionReconcile = "honua.controlplane.execution.reconcile";
    }

    internal static class Tags
    {
        public const string WorkflowKind = "honua.controlplane.workflow.kind";
        public const string WorkflowStatus = "honua.controlplane.workflow.status";
        public const string WorkflowPreviousStatus = "honua.controlplane.workflow.previous_status";
        public const string WorkflowResult = "honua.controlplane.workflow.result";
        public const string TargetKind = "honua.controlplane.target_kind";
        public const string Backend = "honua.controlplane.backend";
        public const string Environment = "honua.controlplane.environment";
        public const string ExecutionJobKind = "honua.controlplane.execution.kind";
        public const string ExecutionJobStatus = "honua.controlplane.execution.status";
        public const string ExecutionJobPreviousStatus = "honua.controlplane.execution.previous_status";
        public const string ExecutionResult = "honua.controlplane.execution.result";
    }

    // Instrument names as const strings so tests can subscribe to the same identifier
    // the Counter/Histogram fields register against, without hitting the static-cctor
    // cycle that referencing the field itself would trigger from a MeterListener
    // callback during this type's own initialization.
    internal static class Metrics
    {
        public const string WorkflowRequests = "honua.controlplane.workflow.requests_total";
        public const string WorkflowTransitions = "honua.controlplane.workflow.transitions_total";
        public const string WorkflowDurations = "honua.controlplane.workflow.duration_ms";
        public const string ReconcileDurations = "honua.controlplane.workflow.reconcile_duration_ms";
        public const string ExecutionRequests = "honua.controlplane.execution.requests_total";
        public const string ExecutionJobSubmitted = "honua.execution.job.submitted";
        public const string ExecutionJobTransitions = "honua.execution.job.transitions_total";
        public const string ExecutionJobDurations = "honua.execution.job.duration_ms";
        public const string ExecutionReconcileCycles = "honua.execution.reconcile.cycle";
    }

    public static readonly Counter<long> WorkflowRequests = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.WorkflowRequests,
        "requests",
        "Number of workflow requests by operation and result.");

    public static readonly Counter<long> WorkflowTransitions = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.WorkflowTransitions,
        "transitions",
        "Number of workflow state transitions observed by the control plane.");

    public static readonly Histogram<double> WorkflowDurations = HonuaTelemetry.Meter.CreateHistogram<double>(
        Metrics.WorkflowDurations,
        "ms",
        "Elapsed time for workflow operations that reached terminal states.");

    public static readonly Histogram<double> ReconcileDurations = HonuaTelemetry.Meter.CreateHistogram<double>(
        Metrics.ReconcileDurations,
        "ms",
        "Elapsed time spent reconciling workflow operations.");

    public static readonly Counter<long> ExecutionRequests = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.ExecutionRequests,
        "requests",
        "Number of execution-job backend requests by operation and result.");

    public static readonly Counter<long> ExecutionJobSubmitted = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.ExecutionJobSubmitted,
        "jobs",
        "Number of execution jobs submitted to a batch-compute backend.");

    public static readonly Counter<long> ExecutionJobTransitions = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.ExecutionJobTransitions,
        "transitions",
        "Number of execution job state transitions observed by the control plane.");

    public static readonly Histogram<double> ExecutionJobDurations = HonuaTelemetry.Meter.CreateHistogram<double>(
        Metrics.ExecutionJobDurations,
        "ms",
        "Elapsed time for execution jobs that reached terminal states.");

    public static readonly Counter<long> ExecutionReconcileCycles = HonuaTelemetry.Meter.CreateCounter<long>(
        Metrics.ExecutionReconcileCycles,
        "cycles",
        "Number of execution job reconciliation cycles completed by the background service.");

    public static Activity? StartWorkflowActivity(
        string activityName,
        string operation,
        WorkflowOperationKind kind,
        string? operationId = null,
        DeployOperationSpec? spec = null,
        string? correlationId = null)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Admin);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);
        activity?.SetTag(Tags.WorkflowKind, kind.ToString());

        if (spec != null)
        {
            activity?.SetTag(Tags.TargetKind, spec.TargetKind.ToString());
            activity?.SetTag(Tags.Backend, spec.Backend);
            activity?.SetTag(Tags.Environment, spec.Environment);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            activity?.SetTag(HonuaTelemetry.Tags.CorrelationId, correlationId);
        }

        return activity;
    }

    public static TagList CreateTags(WorkflowOperationRecord operation, string? result = null, string? previousStatus = null)
    {
        var tags = new TagList
        {
            { Tags.WorkflowKind, operation.Kind.ToString() },
            { Tags.WorkflowStatus, operation.Status.ToString() }
        };

        if (!string.IsNullOrWhiteSpace(previousStatus))
        {
            tags.Add(Tags.WorkflowPreviousStatus, previousStatus);
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            tags.Add(Tags.WorkflowResult, result);
        }

        AddDeployTags(ref tags, operation.Deploy);
        return tags;
    }

    public static TagList CreateRequestTags(
        string operationName,
        string result,
        DeployOperationSpec? spec = null)
    {
        var tags = new TagList
        {
            { HonuaTelemetry.Tags.Operation, operationName },
            { Tags.WorkflowResult, result }
        };

        AddDeployTags(ref tags, spec);
        return tags;
    }

    public static void RecordTransition(WorkflowOperationRecord previous, WorkflowOperationRecord current)
    {
        if (previous.Status == current.Status)
        {
            return;
        }

        WorkflowTransitions.Add(1, CreateTags(current, previousStatus: previous.Status.ToString()));

        if (current.CompletedAt.HasValue)
        {
            var durationMs = (current.CompletedAt.Value - current.CreatedAt).TotalMilliseconds;
            if (durationMs >= 0)
            {
                WorkflowDurations.Record(durationMs, CreateTags(current));
            }
        }
    }

    private static void AddDeployTags(ref TagList tags, DeployOperationSpec? spec)
    {
        if (spec == null)
        {
            return;
        }

        tags.Add(Tags.TargetKind, spec.TargetKind.ToString());
        tags.Add(Tags.Backend, spec.Backend);
        tags.Add(Tags.Environment, spec.Environment);
    }

    public static Activity? StartExecutionActivity(
        string activityName,
        string operation,
        ExecutionJobRecord job)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Admin);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, operation);
        activity?.SetTag(Tags.ExecutionJobKind, job.Spec.Kind.ToString());
        activity?.SetTag(Tags.TargetKind, job.Spec.TargetKind.ToString());
        activity?.SetTag(Tags.Backend, job.Spec.Backend);
        if (!string.IsNullOrWhiteSpace(job.Audit.CorrelationId))
        {
            activity?.SetTag(HonuaTelemetry.Tags.CorrelationId, job.Audit.CorrelationId);
        }

        return activity;
    }

    public static TagList CreateExecutionTags(ExecutionJobRecord job, string? result = null, string? previousStatus = null)
    {
        var tags = new TagList
        {
            { Tags.ExecutionJobKind, job.Spec.Kind.ToString() },
            { Tags.ExecutionJobStatus, job.Status.ToString() },
            { Tags.TargetKind, job.Spec.TargetKind.ToString() },
            { Tags.Backend, job.Spec.Backend }
        };

        if (!string.IsNullOrWhiteSpace(previousStatus))
        {
            tags.Add(Tags.ExecutionJobPreviousStatus, previousStatus);
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            tags.Add(Tags.ExecutionResult, result);
        }

        return tags;
    }

    public static void RecordExecutionRequest(ExecutionJobRecord job, string operation, string result)
    {
        var tags = CreateExecutionTags(job, result: result);
        tags.Add(HonuaTelemetry.Tags.Operation, operation);
        ExecutionRequests.Add(1, tags);
    }

    public static void RecordExecutionSubmission(ExecutionJobRecord job)
    {
        ExecutionJobSubmitted.Add(1, CreateExecutionTags(job));
    }

    public static void RecordExecutionTransition(ExecutionJobRecord previous, ExecutionJobRecord current)
    {
        if (previous.Status == current.Status)
        {
            return;
        }

        ExecutionJobTransitions.Add(1, CreateExecutionTags(current, previousStatus: previous.Status.ToString()));

        if (current.CompletedAt.HasValue)
        {
            var durationMs = (current.CompletedAt.Value - current.CreatedAt).TotalMilliseconds;
            if (durationMs >= 0)
            {
                ExecutionJobDurations.Record(durationMs, CreateExecutionTags(current));
            }
        }
    }
}
