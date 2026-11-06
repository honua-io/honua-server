// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

// The fingerprint excludes the caller timestamp, and the builder strips URL secrets and never copies style bodies or feature payloads.
public static class MigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.evidence-pack-builder/1.0";

    public static MigrationEvidencePackArtifact Build(
        MigrationEvidencePackInputs inputs,
        MigrationEvidencePackBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Inventory);
        ArgumentNullException.ThrowIfNull(inputs.Manifest);
        ArgumentNullException.ThrowIfNull(inputs.ApplyExecution);

        var resolvedOptions = options ?? new MigrationEvidencePackBuilderOptions();

        var redactedSource = RedactSource(inputs.ApplyExecution.Source);
        var redactedInventory = inputs.Inventory with { Source = RedactSource(inputs.Inventory.Source) };
        var redactedManifest = inputs.Manifest with { Source = RedactSource(inputs.Manifest.Source) };

        var workspaceScope = BuildWorkspaceScope(inputs.RequestedWorkspaceNames);
        var stages = BuildStages(inputs.ApplyExecution);
        var summary = BuildSummary(inputs.ApplyExecution, stages);
        var styleDiagnostics = BuildStyleDiagnostics(stages);

        var bundle = new MigrationEvidencePackBundle
        {
            SourceKind = inputs.ApplyExecution.SourceKind,
            Source = redactedSource,
            WorkspaceScope = workspaceScope,
            Apply = new MigrationEvidencePackApplyIdentity
            {
                PlanFingerprint = inputs.ApplyExecution.PlanFingerprint,
                ReplayToken = inputs.ApplyExecution.ReplayToken,
                ExecutionMode = inputs.ApplyExecution.ExecutionMode
            },
            Summary = summary,
            Stages = stages,
            StyleDiagnostics = styleDiagnostics,
            Inventory = redactedInventory,
            Manifest = redactedManifest
        };

        var fingerprint = ComputeBundleFingerprint(bundle);

        return new MigrationEvidencePackArtifact
        {
            RunId = string.IsNullOrWhiteSpace(resolvedOptions.RunId) ? "migration-evidence-run" : resolvedOptions.RunId,
            Generator = string.IsNullOrWhiteSpace(resolvedOptions.Generator) ? BuilderGenerator : resolvedOptions.Generator,
            GeneratedAt = resolvedOptions.GeneratedAt ?? DateTimeOffset.UnixEpoch,
            BundleFingerprint = fingerprint,
            Bundle = bundle
        };
    }

    public static string ComputeBundleFingerprint(MigrationEvidencePackBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var payload = JsonSerializer.SerializeToUtf8Bytes(bundle, MigrationEvidencePackJsonContext.Default.MigrationEvidencePackBundle);
        var hash = SHA256.HashData(payload);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static MigrationSourceIdentity RedactSource(MigrationSourceIdentity source)
    {
        return new MigrationSourceIdentity
        {
            DisplayName = source.DisplayName,
            BaseUrl = RedactUrl(source.BaseUrl),
            Product = source.Product,
            Version = source.Version,
            Build = source.Build,
            ServiceType = source.ServiceType
        };
    }

    private static string RedactUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return baseUrl;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static MigrationEvidencePackWorkspaceScope BuildWorkspaceScope(IReadOnlyCollection<string>? requestedWorkspaceNames)
    {
        if (requestedWorkspaceNames is null || requestedWorkspaceNames.Count == 0)
        {
            return new MigrationEvidencePackWorkspaceScope
            {
                Restricted = false,
                WorkspaceNames = []
            };
        }

        var ordered = requestedWorkspaceNames
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new MigrationEvidencePackWorkspaceScope
        {
            Restricted = ordered.Length > 0,
            WorkspaceNames = ordered
        };
    }

    private static MigrationEvidencePackStage[] BuildStages(MigrationApplyExecutionArtifact applyExecution)
    {
        var grouped = applyExecution.StepResults
            .GroupBy(MapStepKindToStage, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var stageIds = new[]
        {
            MigrationEvidencePackStageIds.Catalog,
            MigrationEvidencePackStageIds.Data,
            MigrationEvidencePackStageIds.Style
        };

        var stages = new List<MigrationEvidencePackStage>(stageIds.Length);
        foreach (var stageId in stageIds)
        {
            if (!grouped.TryGetValue(stageId, out var stageSteps) || stageSteps.Length == 0)
            {
                stages.Add(new MigrationEvidencePackStage
                {
                    Id = stageId,
                    StepCount = 0,
                    StepResults = []
                });
                continue;
            }

            var ordered = stageSteps
                .OrderBy(static step => step.StepId, StringComparer.Ordinal)
                .ToArray();

            stages.Add(new MigrationEvidencePackStage
            {
                Id = stageId,
                StepCount = ordered.Length,
                AppliedCount = ordered.Count(static step => step.Outcome == "applied"),
                AlreadyAppliedCount = ordered.Count(static step => step.Outcome == "already-applied"),
                ManualReviewCount = ordered.Count(static step => step.Outcome == "manual-review"),
                UnsupportedCount = ordered.Count(static step => step.Outcome == "unsupported"),
                FailedCount = ordered.Count(static step => step.Outcome == "failed"),
                StepResults = ordered
            });
        }

        return stages.ToArray();
    }

    private static string MapStepKindToStage(MigrationApplyExecutionStepResult step)
    {
        // Slice 1 (#1095) emits workspace + layer-group catalog rows.
        // Slice 2 (#1107) emits data-source + feature-data rows.
        // Slice 3 (#1114) emits style rows.
        // Any other kind (e.g. per-layer publish) is folded into the catalog
        // stage so the pack always covers every executed step.
        return step.Kind switch
        {
            "datastore" or "data-source" or "feature-copy" => MigrationEvidencePackStageIds.Data,
            "style" => MigrationEvidencePackStageIds.Style,
            _ => MigrationEvidencePackStageIds.Catalog
        };
    }

    private static MigrationEvidencePackSummary BuildSummary(
        MigrationApplyExecutionArtifact applyExecution,
        IReadOnlyList<MigrationEvidencePackStage> stages)
    {
        var styleStage = stages.FirstOrDefault(s => s.Id == MigrationEvidencePackStageIds.Style);

        return new MigrationEvidencePackSummary
        {
            TotalStepCount = applyExecution.Summary.TotalStepCount,
            AppliedStepCount = applyExecution.Summary.AppliedStepCount,
            AlreadyAppliedStepCount = applyExecution.Summary.AlreadyAppliedStepCount,
            ManualReviewStepCount = applyExecution.Summary.ManualReviewStepCount,
            UnsupportedStepCount = applyExecution.Summary.UnsupportedStepCount,
            FailedStepCount = applyExecution.Summary.FailedStepCount,
            StyleManualReviewCount = styleStage?.ManualReviewCount ?? 0
        };
    }

    private static MigrationEvidencePackStyleDiagnostic[] BuildStyleDiagnostics(
        IReadOnlyList<MigrationEvidencePackStage> stages)
    {
        var styleStage = stages.FirstOrDefault(s => s.Id == MigrationEvidencePackStageIds.Style);
        if (styleStage is null || styleStage.StepResults.Length == 0)
        {
            return [];
        }

        return styleStage.StepResults
            .Where(static step => step.Outcome is "manual-review" or "unsupported" or "failed")
            .Select(static step => new MigrationEvidencePackStyleDiagnostic
            {
                SourceId = step.SourceId,
                StepOutcome = step.Outcome,
                Message = step.Message
            })
            .OrderBy(static diag => diag.SourceId, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record MigrationEvidencePackInputs
{
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public required MigrationManifestArtifact Manifest { get; init; }

    public required MigrationApplyExecutionArtifact ApplyExecution { get; init; }

    public IReadOnlyCollection<string>? RequestedWorkspaceNames { get; init; }
}

public sealed record MigrationEvidencePackBuilderOptions
{
    public string? RunId { get; init; }

    public string? Generator { get; init; }

    public DateTimeOffset? GeneratedAt { get; init; }
}
