// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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

    private sealed record LayerPublishBuildContext
    {
        public required MigrationApplyPlanStep Step { get; init; }
        public required GeoServerLayerInfo Layer { get; init; }
        public required string Schema { get; init; }
        public required string Table { get; init; }
        public required GeoServerSecurityRule[] SecurityRules { get; init; }
        public required GeoServerImportRequest Request { get; init; }
    }

    private sealed record RunCompletionRecord
    {
        public required string RunId { get; init; }
        public required MigrationRunStatus Status { get; init; }
        public required GeoServerReconciliationBundle Bundle { get; init; }
        public required string StatusNote { get; init; }
    }

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

    private sealed record ApplyFlowOutcome(
        string MigrationRunId,
        MigrationApplyPlanArtifact ApplyPlan,
        MigrationApplyExecutionArtifact ApplyExecution);

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

        if (outcome.ApplyExecution.Summary.FailedStepCount > 0)
        {
            return await CompleteFailedApplyAsync(flow, outcome, new ApplyFinalArtifacts(warnings, runMetrics, bundle), cancellationToken)
                .ConfigureAwait(false);
        }

        return await CompleteSucceededApplyAsync(flow, outcome, new ApplyFinalArtifacts(warnings, runMetrics, bundle), cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record ApplyFinalArtifacts(
        List<string> Warnings,
        MigrationRunMetricsArtifact RunMetrics,
        GeoServerReconciliationBundle Bundle);

    private async Task<GeoServerImportResult> CompleteFailedApplyAsync(
        ImportFlowContext flow,
        ApplyFlowOutcome outcome,
        ApplyFinalArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var failureMessage = BuildApplyFailureMessage(outcome.ApplyExecution);
        await TryRecordMigrationRunCompletedAsync(
                new RunCompletionRecord
                {
                    RunId = outcome.MigrationRunId,
                    Status = MigrationRunStatus.Failed,
                    Bundle = artifacts.Bundle,
                    StatusNote = failureMessage
                },
                cancellationToken)
            .ConfigureAwait(false);

        flow.Progress?.Report(flow.CurrentProgress with
        {
            Status = GeoServerImportStatus.Failed,
            ResourcesProcessed = outcome.ApplyPlan.Summary.TotalStepCount,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Apply plan failed",
            ErrorMessage = failureMessage,
            Warnings = artifacts.Warnings,
            ApplyPlan = outcome.ApplyPlan,
            ApplyExecution = outcome.ApplyExecution,
            RunMetrics = artifacts.RunMetrics,
            MigrationRunId = outcome.MigrationRunId,
            ReconciliationBundle = artifacts.Bundle
        });

        return CreateFailedApplyPlanResult(
                flow.ServiceInfo, outcome.ApplyPlan, outcome.ApplyExecution, flow.Request, flow.Stopwatch.Elapsed, artifacts.Warnings, failureMessage) with
        {
            RunMetrics = artifacts.RunMetrics,
            MigrationRunId = outcome.MigrationRunId,
            ReconciliationBundle = artifacts.Bundle
        };
    }

    private async Task<GeoServerImportResult> CompleteSucceededApplyAsync(
        ImportFlowContext flow,
        ApplyFlowOutcome outcome,
        ApplyFinalArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        await TryRecordMigrationRunCompletedAsync(
                new RunCompletionRecord
                {
                    RunId = outcome.MigrationRunId,
                    Status = MigrationRunStatus.Succeeded,
                    Bundle = artifacts.Bundle,
                    StatusNote = "GeoServer apply plan executed."
                },
                cancellationToken)
            .ConfigureAwait(false);

        flow.Progress?.Report(flow.CurrentProgress with
        {
            Status = GeoServerImportStatus.Completed,
            ResourcesProcessed = outcome.ApplyPlan.Summary.TotalStepCount,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Apply plan executed",
            Warnings = artifacts.Warnings,
            ApplyPlan = outcome.ApplyPlan,
            ApplyExecution = outcome.ApplyExecution,
            RunMetrics = artifacts.RunMetrics,
            MigrationRunId = outcome.MigrationRunId,
            ReconciliationBundle = artifacts.Bundle
        });

        Log.ApplyPlanExecuted(
            _logger,
            outcome.ApplyPlan.Summary.TotalStepCount,
            outcome.ApplyExecution.Summary.AppliedStepCount,
            outcome.ApplyExecution.Summary.AlreadyAppliedStepCount,
            outcome.ApplyExecution.Summary.ManualReviewStepCount,
            outcome.ApplyExecution.Summary.UnsupportedStepCount);

        return CreateApplyPlanResult(
                flow.ServiceInfo, outcome.ApplyPlan, outcome.ApplyExecution, flow.Request, flow.Stopwatch.Elapsed, artifacts.Warnings) with
        {
            RunMetrics = artifacts.RunMetrics,
            MigrationRunId = outcome.MigrationRunId,
            ReconciliationBundle = artifacts.Bundle
        };
    }

    private MigrationApplyPlanArtifact EnrichApplyPlan(
        MigrationApplyPlanArtifact plan,
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources)
    {
        var layersById = filteredResources.Layers.ToDictionary(GetLayerId, StringComparer.Ordinal);
        var stylesById = filteredResources.Styles.ToDictionary(GetStyleId, StringComparer.Ordinal);
        var styleIdsByReference = BuildStyleReferenceLookup(filteredResources.Styles);
        var extraReview = new List<MigrationManifestReviewItem>();
        var updatedSteps = new MigrationApplyPlanStep[plan.Steps.Length];
        var anyChanged = false;

        for (var i = 0; i < plan.Steps.Length; i++)
        {
            var (updated, changed) = EnrichLayerStep(
                plan.Steps[i],
                new StepEnrichmentContext
                {
                    LayersById = layersById,
                    StylesById = stylesById,
                    StyleIdsByReference = styleIdsByReference,
                    SecurityRules = serviceInfo.SecurityRules,
                    ExtraReview = extraReview
                });
            updatedSteps[i] = updated;
            anyChanged |= changed;
        }

        extraReview.AddRange(GeoServerSecurityRuleTranslator.CollectUnmatchedUnexpressedRules(serviceInfo.SecurityRules, filteredResources.Layers));
        if (!anyChanged && extraReview.Count == 0)
        {
            return plan;
        }

        return plan with
        {
            Steps = updatedSteps,
            ManualReviewItems = plan.ManualReviewItems
                .Concat(extraReview)
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private sealed record StepEnrichmentContext
    {
        public required Dictionary<string, GeoServerLayerInfo> LayersById { get; init; }
        public required Dictionary<string, GeoServerStyleInfo> StylesById { get; init; }
        public required Dictionary<StyleReferenceKey, string[]> StyleIdsByReference { get; init; }
        public required GeoServerSecurityRule[] SecurityRules { get; init; }
        public required List<MigrationManifestReviewItem> ExtraReview { get; init; }
    }

    private (MigrationApplyPlanStep Step, bool Changed) EnrichLayerStep(
        MigrationApplyPlanStep step,
        StepEnrichmentContext context)
    {
        if (!string.Equals(step.Kind, "layer", StringComparison.Ordinal) ||
            !context.LayersById.TryGetValue(step.SourceId, out var layer))
        {
            return (step, false);
        }

        var layerStyles = ResolveLayerStyles(layer, context.StylesById, context.StyleIdsByReference);
        var styleDiff = GeoServerStyleDiffBuilder.BuildForLayer(step.SourceId, layerStyles, _sldConverter);
        var security = GeoServerSecurityRuleTranslator.TranslateForLayer(layer, context.SecurityRules, step.SourceId);
        if (styleDiff.Diffs.Length == 0 && security.Dispositions.Length == 0)
        {
            return (step, false);
        }

        context.ExtraReview.AddRange(styleDiff.ReviewItems);
        context.ExtraReview.AddRange(security.ReviewItems);
        var reviewCodes = step.ReviewCodes
            .Concat(styleDiff.ReviewItems.Select(static item => item.Code))
            .Concat(security.ReviewItems.Select(static item => item.Code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();

        return (step with
        {
            ReviewCodes = reviewCodes,
            StyleDiffs = styleDiff.Diffs.Length > 0 ? styleDiff.Diffs : null,
            SecurityRules = security.Dispositions.Length > 0 ? security.Dispositions : null
        }, true);
    }

    private static List<(string StyleId, GeoServerStyleInfo Style)> ResolveLayerStyles(
        GeoServerLayerInfo layer,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        Dictionary<StyleReferenceKey, string[]> styleIdsByReference)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<(string StyleId, GeoServerStyleInfo Style)>();
        var references = layer.AlternativeStyles.Prepend(layer.DefaultStyle);

        foreach (var styleReference in references)
        {
            foreach (var styleId in ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, styleReference))
            {
                if (seen.Add(styleId) && stylesById.TryGetValue(styleId, out var style))
                {
                    resolved.Add((styleId, style));
                }
            }
        }

        return resolved;
    }

    private static LayerPublishRequest BuildLayerPublishRequest(LayerPublishBuildContext ctx)
    {
        var temporalInfo = BuildLayerTemporalInfo(ctx.Layer);
        var security = GeoServerSecurityRuleTranslator.TranslateForLayer(ctx.Layer, ctx.SecurityRules, ctx.Step.SourceId);
        return new LayerPublishRequest
        {
            Schema = ctx.Schema,
            Table = ctx.Table,
            LayerName = ctx.Step.TargetResourceName ?? ctx.Layer.Name,
            Description = ctx.Layer.Abstract,
            Srid = ctx.Request.TargetSrid ?? ResolveSrid(ctx.Layer.SRS),
            TemporalColumn = temporalInfo?.StartTimeField,
            Temporal = temporalInfo,
            AccessPolicy = security.AccessPolicy,
            Extensions = BuildLayerDimensionExtensions(ctx.Layer),
            Fields = [],
            ServiceName = ctx.Step.TargetServiceName,
            Enabled = ctx.Request.AutoPublishLayers && ctx.Layer.Enabled
        };
    }

    private static MetadataV2ObjectTemporal? BuildLayerTemporalInfo(GeoServerLayerInfo layer)
    {
        if (layer.TimeDimension is not { Enabled: true } timeDim || string.IsNullOrWhiteSpace(timeDim.Attribute))
        {
            return null;
        }

        return new MetadataV2ObjectTemporal
        {
            StartTimeField = timeDim.Attribute.Trim(),
            EndTimeField = string.IsNullOrWhiteSpace(timeDim.EndAttribute) ? null : timeDim.EndAttribute.Trim()
        };
    }

    private static Dictionary<string, JsonElement>? BuildLayerDimensionExtensions(GeoServerLayerInfo layer)
    {
        if (layer.ElevationDimension is not { Enabled: true } elevDim)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElevationDimensionExtension(writer, elevDim);
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["wms"] = doc.RootElement.Clone() };
    }

    private static void WriteElevationDimensionExtension(Utf8JsonWriter writer, GeoServerDimensionInfo elevDim)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("dimensions");
        writer.WriteStartObject();
        writer.WriteString("name", "elevation");
        writer.WriteString("units", string.IsNullOrWhiteSpace(elevDim.Units) ? "EPSG:5030" : elevDim.Units);
        if (!string.IsNullOrWhiteSpace(elevDim.UnitSymbol))
        {
            writer.WriteString("unitSymbol", elevDim.UnitSymbol);
        }

        writer.WriteBoolean("nearestValue", elevDim.NearestMatchEnabled);
        if (!string.IsNullOrWhiteSpace(elevDim.DefaultValueReference))
        {
            writer.WriteString("default", elevDim.DefaultValueReference);
        }

        writer.WriteString("extent", !string.IsNullOrWhiteSpace(elevDim.Attribute) ? elevDim.Attribute! : (elevDim.DefaultValueReference ?? string.Empty));
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
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
