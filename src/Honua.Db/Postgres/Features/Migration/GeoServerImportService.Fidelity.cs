// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoServerImportService
{
    private sealed record ImportFlowContext
    {
        public required string JobId { get; init; }
        public required GeoServerServiceInfo ServiceInfo { get; init; }
        public required FilteredResources FilteredResources { get; init; }
        public required GeoServerImportRequest Request { get; init; }
        public required GeoServerImportProgress CurrentProgress { get; init; }
        public IProgress<GeoServerImportProgress>? Progress { get; init; }
        public required MigrationRunMetricsRecorder MetricsRecorder { get; init; }
        public required Stopwatch Stopwatch { get; init; }
    }

    private sealed record RunCompletionRecord
    {
        public required string RunId { get; init; }
        public required MigrationRunStatus Status { get; init; }
        public required GeoServerReconciliationBundle Bundle { get; init; }
        public required string StatusNote { get; init; }
    }

    private sealed record ApplyFlowOutcome(
        string MigrationRunId,
        MigrationApplyPlanArtifact ApplyPlan,
        MigrationApplyExecutionArtifact ApplyExecution);

    private sealed record ApplyCompletionContext(
        ImportFlowContext Flow,
        ApplyFlowOutcome Outcome,
        ApplyFinalArtifacts Artifacts);

    private sealed record ApplyFinalArtifacts(
        List<string> Warnings,
        MigrationRunMetricsArtifact RunMetrics,
        GeoServerReconciliationBundle Bundle);

    private async Task<GeoServerImportResult> CompleteDryRunAsync(
        ImportFlowContext flow,
        CancellationToken cancellationToken)
    {
        MigrationApplyPlanArtifact dryRunPlan;
        using (flow.MetricsRecorder.BeginPhase(MigrationCostPerformancePhases.Manifest))
        {
            dryRunPlan = await CreateApplyPlanAsync(flow.ServiceInfo, flow.FilteredResources, flow.Request, cancellationToken)
                .ConfigureAwait(false);
            flow.MetricsRecorder.RecordManualReview(dryRunPlan.Summary.ManualReviewStepCount, dryRunPlan.Summary.TotalStepCount);
            flow.MetricsRecorder.SampleResources();
        }

        flow.MetricsRecorder.SampleResources();
        var dryRunMetrics = BuildRunMetricsArtifact(flow.MetricsRecorder, flow.ServiceInfo, flow.JobId, "geoserver dry run");
        EmitRunMetrics(dryRunMetrics);
        flow.Progress?.Report(flow.CurrentProgress with
        {
            Status = GeoServerImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Dry run completed",
            ApplyPlan = dryRunPlan,
            RunMetrics = dryRunMetrics
        });

        return CreateDryRunResult(flow.ServiceInfo, flow.FilteredResources, flow.Request, flow.Stopwatch.Elapsed) with
        {
            ApplyPlan = dryRunPlan,
            ResourcesPlanned = dryRunPlan.Summary.TotalStepCount,
            ResourcesManualReview = dryRunPlan.Summary.ManualReviewStepCount,
            ResourcesUnsupported = dryRunPlan.Summary.UnsupportedStepCount,
            RunMetrics = dryRunMetrics
        };
    }

    private async Task<GeoServerImportResult> CompleteApplyFlowAsync(
        ImportFlowContext flow,
        CancellationToken cancellationToken)
    {
        MigrationApplyPlanArtifact applyPlan;
        using (flow.MetricsRecorder.BeginPhase(MigrationCostPerformancePhases.Manifest))
        {
            applyPlan = await CreateApplyPlanAsync(flow.ServiceInfo, flow.FilteredResources, flow.Request, cancellationToken)
                .ConfigureAwait(false);
            flow.MetricsRecorder.RecordResourceCount(applyPlan.Summary.TotalStepCount);
            flow.MetricsRecorder.RecordManualReview(applyPlan.Summary.ManualReviewStepCount, applyPlan.Summary.TotalStepCount);
            flow.MetricsRecorder.SampleResources();
        }

        var migrationRunId = $"geoserver-run-{flow.JobId}";
        await TryRecordMigrationRunStartedAsync(migrationRunId, flow.ServiceInfo, cancellationToken).ConfigureAwait(false);

        MigrationApplyExecutionArtifact applyExecution;
        using (flow.MetricsRecorder.BeginPhase(MigrationCostPerformancePhases.Apply))
        {
            applyExecution = await ExecuteApplyPlanAsync(
                    flow.FilteredResources, flow.Request, applyPlan, flow.CurrentProgress, flow.Progress, cancellationToken)
                .ConfigureAwait(false);
            flow.MetricsRecorder.RecordResourceCount(applyExecution.Summary.AppliedStepCount);
            flow.MetricsRecorder.RecordResume(applyPlan.ReplayToken, applyExecution.Summary.AlreadyAppliedStepCount);
            flow.MetricsRecorder.SampleResources();
        }

        return await FinalizeApplyFlowAsync(flow, new ApplyFlowOutcome(migrationRunId, applyPlan, applyExecution), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GeoServerImportResult> FinalizeApplyFlowAsync(
        ImportFlowContext flow,
        ApplyFlowOutcome outcome,
        CancellationToken cancellationToken)
    {
        var warnings = BuildApplyPlanWarnings(outcome.ApplyPlan, outcome.ApplyExecution);
        var runMetrics = BuildRunMetricsArtifact(flow.MetricsRecorder, flow.ServiceInfo, flow.JobId, "geoserver migration");
        EmitRunMetrics(runMetrics);
        var bundle = GeoServerReconciliationWriter.BuildBundle(
            new GeoServerReconciliationBuildInput
            {
                RunId = outcome.MigrationRunId,
                JobId = flow.JobId,
                ServiceInfo = flow.ServiceInfo,
                Layers = flow.FilteredResources.Layers,
                WorkspaceCount = flow.FilteredResources.WorkspaceCount,
                DataStoreCount = flow.FilteredResources.DataStoreCount,
                StyleCount = flow.FilteredResources.StyleCount,
                ApplyPlan = outcome.ApplyPlan,
                ApplyExecution = outcome.ApplyExecution
            });
        var completion = new ApplyCompletionContext(flow, outcome, new ApplyFinalArtifacts(warnings, runMetrics, bundle));
        return outcome.ApplyExecution.Summary.FailedStepCount > 0
            ? await CompleteFailedApplyAsync(completion, cancellationToken).ConfigureAwait(false)
            : await CompleteSucceededApplyAsync(completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeoServerImportResult> CompleteFailedApplyAsync(
        ApplyCompletionContext ctx,
        CancellationToken cancellationToken)
    {
        var failureMessage = BuildApplyFailureMessage(ctx.Outcome.ApplyExecution);
        var record = new RunCompletionRecord
        {
            RunId = ctx.Outcome.MigrationRunId,
            Status = MigrationRunStatus.Failed,
            Bundle = ctx.Artifacts.Bundle,
            StatusNote = failureMessage
        };
        await TryRecordMigrationRunCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        ReportTerminalApplyProgress(ctx, new TerminalProgressUpdate(GeoServerImportStatus.Failed, "Apply plan failed", failureMessage));
        return CreateFailedApplyPlanResult(
                ctx.Flow.ServiceInfo, ctx.Outcome.ApplyPlan, ctx.Outcome.ApplyExecution,
                ctx.Flow.Request, ctx.Flow.Stopwatch.Elapsed, ctx.Artifacts.Warnings, failureMessage) with
        {
            RunMetrics = ctx.Artifacts.RunMetrics,
            MigrationRunId = ctx.Outcome.MigrationRunId,
            ReconciliationBundle = ctx.Artifacts.Bundle
        };
    }

    private async Task<GeoServerImportResult> CompleteSucceededApplyAsync(
        ApplyCompletionContext ctx,
        CancellationToken cancellationToken)
    {
        var record = new RunCompletionRecord
        {
            RunId = ctx.Outcome.MigrationRunId,
            Status = MigrationRunStatus.Succeeded,
            Bundle = ctx.Artifacts.Bundle,
            StatusNote = "GeoServer apply plan executed."
        };
        await TryRecordMigrationRunCompletedAsync(record, cancellationToken).ConfigureAwait(false);
        ReportTerminalApplyProgress(ctx, new TerminalProgressUpdate(GeoServerImportStatus.Completed, "Apply plan executed", null));
        Log.ApplyPlanExecuted(
            _logger,
            ctx.Outcome.ApplyPlan.Summary.TotalStepCount,
            ctx.Outcome.ApplyExecution.Summary.AppliedStepCount,
            ctx.Outcome.ApplyExecution.Summary.AlreadyAppliedStepCount,
            ctx.Outcome.ApplyExecution.Summary.ManualReviewStepCount,
            ctx.Outcome.ApplyExecution.Summary.UnsupportedStepCount);

        return CreateApplyPlanResult(
                ctx.Flow.ServiceInfo, ctx.Outcome.ApplyPlan, ctx.Outcome.ApplyExecution,
                ctx.Flow.Request, ctx.Flow.Stopwatch.Elapsed, ctx.Artifacts.Warnings) with
        {
            RunMetrics = ctx.Artifacts.RunMetrics,
            MigrationRunId = ctx.Outcome.MigrationRunId,
            ReconciliationBundle = ctx.Artifacts.Bundle
        };
    }

    private sealed record TerminalProgressUpdate(GeoServerImportStatus Status, string Phase, string? ErrorMessage);

    private static void ReportTerminalApplyProgress(ApplyCompletionContext ctx, TerminalProgressUpdate update)
    {
        ctx.Flow.Progress?.Report(ctx.Flow.CurrentProgress with
        {
            Status = update.Status,
            ResourcesProcessed = ctx.Outcome.ApplyPlan.Summary.TotalStepCount,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = update.Phase,
            ErrorMessage = update.ErrorMessage,
            Warnings = ctx.Artifacts.Warnings,
            ApplyPlan = ctx.Outcome.ApplyPlan,
            ApplyExecution = ctx.Outcome.ApplyExecution,
            RunMetrics = ctx.Artifacts.RunMetrics,
            MigrationRunId = ctx.Outcome.MigrationRunId,
            ReconciliationBundle = ctx.Artifacts.Bundle
        });
    }

    private async Task TryRecordMigrationRunStartedAsync(
        string runId,
        GeoServerServiceInfo serviceInfo,
        CancellationToken cancellationToken)
    {
        if (MigrationRunCatalog is null)
        {
            return;
        }

        await MigrationRunCatalog.RecordStartedAsync(
                new MigrationRunRecord
                {
                    RunId = runId,
                    SourceKind = "geoserver-rest",
                    SourceUrl = SanitizeUrlForRunCatalog(serviceInfo.GeoServerRestUrl),
                    SourceDisplayName = serviceInfo.GlobalSettings?.Title ?? "GeoServer",
                    Status = MigrationRunStatus.Running,
                    StartedAt = DateTimeOffset.UtcNow,
                    StatusNote = "GeoServer migration apply started."
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryRecordMigrationRunCompletedAsync(
        RunCompletionRecord completion,
        CancellationToken cancellationToken)
    {
        if (MigrationRunCatalog is null)
        {
            return;
        }

        var evidenceJson = GeoServerReconciliationWriter.SerializeBundle(completion.Bundle);
        var fingerprint = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceJson))).ToLowerInvariant()}";
        await MigrationRunCatalog.RecordCompletedAsync(
                completion.RunId,
                completion.Status,
                DateTimeOffset.UtcNow,
                evidencePackRef: $"geoserver-reconciliation:{completion.RunId}",
                evidencePackFingerprint: fingerprint,
                evidencePackBody: evidenceJson,
                statusNote: completion.StatusNote,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SanitizeUrlForRunCatalog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        return new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
    }
}
