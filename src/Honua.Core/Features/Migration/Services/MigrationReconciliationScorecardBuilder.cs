// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Pure, deterministic builder for <see cref="MigrationReconciliationScorecard"/> (issue #1381).
/// Aggregates the per-layer data-reconciliation artifact (and optional catalog parity report) into
/// the data-reconciliation dimension, folds the per-construct automation statuses into the distinct
/// capability-parity dimension, and signs the body with the evidence-pack fingerprint mechanism.
/// </summary>
public static class MigrationReconciliationScorecardBuilder
{
    /// <summary>
    /// Build and sign a scorecard from a completed reconciliation run.
    /// </summary>
    /// <param name="runId">Stable migration run identifier.</param>
    /// <param name="sourceKind">Source kind, e.g. <c>arcgis-geoservices-rest</c>.</param>
    /// <param name="generatedAt">UTC instant the scorecard is generated.</param>
    /// <param name="dataArtifact">
    /// Per-layer data-reconciliation artifact (count/geometry/content/extent). Required: this is the
    /// gating dimension.
    /// </param>
    /// <param name="catalogReport">
    /// Optional deeper catalog parity report (schema/domain/identifier/relationship/attachment/
    /// subtype). Its findings are folded into the data-reconciliation dimension when supplied.
    /// </param>
    /// <param name="capabilityAutomationStatuses">
    /// Per-construct automation statuses from the Esri construct capability registry / fidelity
    /// matrix (see <see cref="MigrationFidelityAutomationStatuses"/>). Drives the capability-parity
    /// dimension, which is kept strictly separate from data reconciliation and never gates the run.
    /// </param>
    /// <returns>A signed scorecard whose <see cref="MigrationReconciliationScorecard.Fingerprint"/> is set.</returns>
    public static MigrationReconciliationScorecard Build(
        string runId,
        string sourceKind,
        DateTimeOffset generatedAt,
        MigrationReconciliationArtifact dataArtifact,
        MigrationCatalogReconciliationReport? catalogReport,
        IEnumerable<string> capabilityAutomationStatuses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentNullException.ThrowIfNull(dataArtifact);
        ArgumentNullException.ThrowIfNull(capabilityAutomationStatuses);

        var data = BuildDataReconciliation(dataArtifact, catalogReport);
        var capability = BuildCapabilityParity(capabilityAutomationStatuses);

        // The verdict is derived from the data-reconciliation dimension ONLY. Capability parity is
        // advisory: a construct that Honua cannot express (and was therefore not imported) is not a
        // faithless import of the data that DID land.
        var verdict = data.Classification;

        var unsigned = new MigrationReconciliationScorecard
        {
            RunId = runId,
            SourceKind = sourceKind,
            GeneratedAt = generatedAt,
            Verdict = verdict,
            DataReconciliation = data,
            CapabilityParity = capability,
            Fingerprint = null
        };

        return unsigned with { Fingerprint = ComputeFingerprint(unsigned) };
    }

    /// <summary>
    /// Compute the deterministic SHA-256 fingerprint over the unsigned scorecard body (i.e. with
    /// <see cref="MigrationReconciliationScorecard.Fingerprint"/> set to <c>null</c>). Mirrors the
    /// evidence-pack fingerprint mechanism (<c>sha256:&lt;lowercase-hex&gt;</c>) so downstream
    /// verifiers can recompute it independently. Exposed for tests and verifiers.
    /// </summary>
    public static string ComputeFingerprint(MigrationReconciliationScorecard scorecard)
    {
        ArgumentNullException.ThrowIfNull(scorecard);
        var body = scorecard.Fingerprint is null ? scorecard : scorecard with { Fingerprint = null };
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            body,
            MigrationReconciliationScorecardJsonContext.Default.MigrationReconciliationScorecard);
        var hash = SHA256.HashData(payload);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static MigrationScorecardDataReconciliation BuildDataReconciliation(
        MigrationReconciliationArtifact dataArtifact,
        MigrationCatalogReconciliationReport? catalogReport)
    {
        var layers = dataArtifact.Layers
            .Select(static layer => new MigrationScorecardLayer
            {
                SourceLayerId = layer.SourceLayerId,
                SourceLayerName = layer.SourceLayerName,
                TargetHonuaLayerId = layer.TargetHonuaLayerId,
                Classification = layer.Classification
            })
            .OrderBy(static layer => layer.SourceLayerId, StringComparer.Ordinal)
            .ToArray();

        var catalogFindingCount = catalogReport?.Summary.FindingCount ?? 0;
        var catalogReasons = catalogReport is null
            ? []
            : catalogReport.Resources
                .SelectMany(static resource => resource.Findings)
                .Select(static finding => finding.Summary)
                .Where(static summary => !string.IsNullOrWhiteSpace(summary))
                .Select(static summary => summary.Trim());

        var reasons = dataArtifact.Reasons
            .Concat(catalogReasons)
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Select(static reason => reason.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal)
            .ToArray();

        // Fold catalog parity into the same dimension: a hard catalog finding escalates the data
        // classification to fail, a warn escalates pass to warn.
        var classification = EscalateClassification(dataArtifact.Classification, catalogReport);

        return new MigrationScorecardDataReconciliation
        {
            Classification = classification,
            LayerCount = dataArtifact.Summary.LayerCount,
            PassCount = dataArtifact.Summary.PassCount,
            WarnCount = dataArtifact.Summary.WarnCount,
            FailCount = dataArtifact.Summary.FailCount,
            SkippedCount = dataArtifact.Summary.SkippedCount,
            CatalogFindingCount = catalogFindingCount,
            Layers = layers,
            Reasons = reasons
        };
    }

    private static string EscalateClassification(
        string dataClassification,
        MigrationCatalogReconciliationReport? catalogReport)
    {
        if (catalogReport is null)
        {
            return dataClassification;
        }

        if (catalogReport.Summary.FailResourceCount > 0 ||
            string.Equals(dataClassification, MigrationReconciliationClassifications.Fail, StringComparison.Ordinal))
        {
            return MigrationReconciliationClassifications.Fail;
        }

        if (catalogReport.Summary.WarnResourceCount > 0 ||
            string.Equals(dataClassification, MigrationReconciliationClassifications.Warn, StringComparison.Ordinal))
        {
            return MigrationReconciliationClassifications.Warn;
        }

        return MigrationReconciliationClassifications.Pass;
    }

    private static MigrationScorecardCapabilityParity BuildCapabilityParity(
        IEnumerable<string> capabilityAutomationStatuses)
    {
        var automated = 0;
        var assisted = 0;
        var manualReview = 0;
        var unsupported = 0;
        var total = 0;

        foreach (var status in capabilityAutomationStatuses)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                continue;
            }

            total++;
            switch (status.Trim().ToLowerInvariant())
            {
                case MigrationFidelityAutomationStatuses.Automated:
                    automated++;
                    break;
                case MigrationFidelityAutomationStatuses.Assisted:
                    assisted++;
                    break;
                case MigrationFidelityAutomationStatuses.ManualReview:
                    manualReview++;
                    break;
                default:
                    // Treat any unrecognized status as unsupported so the parity ratio stays
                    // conservative rather than silently inflating.
                    unsupported++;
                    break;
            }
        }

        var parityRatio = total == 0 ? 1d : (double)(automated + assisted) / total;

        return new MigrationScorecardCapabilityParity
        {
            ConstructCount = total,
            AutomatedCount = automated,
            AssistedCount = assisted,
            ManualReviewCount = manualReview,
            UnsupportedCount = unsupported,
            ParityRatio = parityRatio
        };
    }
}
