// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

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
    }

    public static readonly Counter<long> WorkflowRequests = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.controlplane.workflow.requests_total",
        "requests",
        "Number of workflow requests by operation and result.");

    public static readonly Counter<long> WorkflowTransitions = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.controlplane.workflow.transitions_total",
        "transitions",
        "Number of workflow state transitions observed by the control plane.");

    public static readonly Histogram<double> WorkflowDurations = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.controlplane.workflow.duration_ms",
        "ms",
        "Elapsed time for workflow operations that reached terminal states.");

    public static readonly Histogram<double> ReconcileDurations = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.controlplane.workflow.reconcile_duration_ms",
        "ms",
        "Elapsed time spent reconciling workflow operations.");

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
}
