// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// OpenTelemetry instrumentation for workflow orchestration.
/// </summary>
internal static class OrchestrationTelemetry
{
    internal static class Activities
    {
        public const string ReconcileRun = "honua.orchestration.reconcile_run";
        public const string ExecuteStep = "honua.orchestration.execute_step";
        public const string ResolveBindings = "honua.orchestration.resolve_bindings";
        public const string SchedulerTick = "honua.orchestration.scheduler_tick";
    }

    internal static class Tags
    {
        public const string WorkflowId = "honua.orchestration.workflow_id";
        public const string RunId = "honua.orchestration.run_id";
        public const string StepId = "honua.orchestration.step_id";
        public const string PlanId = "honua.orchestration.plan_id";
        public const string Attempt = "honua.orchestration.attempt";
        public const string TriggerKind = "honua.orchestration.trigger_kind";
        public const string RunStatus = "honua.orchestration.run_status";
        public const string StepStatus = "honua.orchestration.step_status";
        public const string BindingCount = "honua.orchestration.binding_count";
    }

    public static readonly Counter<long> RunsCreated = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.orchestration.runs_created_total",
        "runs",
        "Workflow runs created, tagged by trigger kind.");

    public static readonly Counter<long> RunsCompleted = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.orchestration.runs_completed_total",
        "runs",
        "Workflow runs that reached a terminal state, tagged by status.");

    public static readonly Counter<long> StepsCompleted = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.orchestration.steps_completed_total",
        "steps",
        "Workflow steps that reached a terminal state, tagged by status.");

    public static readonly Counter<long> StepsRetried = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.orchestration.steps_retried_total",
        "steps",
        "Workflow steps scheduled for retry after a failure.");

    public static readonly Histogram<double> RunDuration = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.orchestration.run_duration_ms",
        "ms",
        "Elapsed time for workflow runs that reached terminal states.");

    public static readonly Histogram<double> StepDuration = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.orchestration.step_duration_ms",
        "ms",
        "Elapsed time for workflow steps that reached terminal states.");

    public static Activity? StartReconcileRunActivity(string runId, string workflowId, int stepCount)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(Activities.ReconcileRun, ActivityKind.Internal);
        activity?.SetTag(Tags.RunId, runId);
        activity?.SetTag(Tags.WorkflowId, workflowId);
        activity?.SetTag("honua.orchestration.step_count", stepCount);
        return activity;
    }

    public static Activity? StartExecuteStepActivity(string runId, string stepId, string planId, int attempt)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(Activities.ExecuteStep, ActivityKind.Internal);
        activity?.SetTag(Tags.RunId, runId);
        activity?.SetTag(Tags.StepId, stepId);
        activity?.SetTag(Tags.PlanId, planId);
        activity?.SetTag(Tags.Attempt, attempt);
        return activity;
    }

    public static Activity? StartResolveBindingsActivity(string runId, string stepId, int bindingCount)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity(Activities.ResolveBindings, ActivityKind.Internal);
        activity?.SetTag(Tags.RunId, runId);
        activity?.SetTag(Tags.StepId, stepId);
        activity?.SetTag(Tags.BindingCount, bindingCount);
        return activity;
    }
}
