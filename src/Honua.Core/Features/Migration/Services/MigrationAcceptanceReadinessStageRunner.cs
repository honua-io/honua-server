// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Drives the readiness stage of the migration acceptance suite by consuming the per-source outputs
/// of the slice-2 scan stage, the slice-3 apply stage, and the slice-4 parity stage and producing
/// one <see cref="MigrationReadinessAttestationArtifact"/> per source (rolled up into a
/// <see cref="MigrationReadinessStageReport"/>).
/// </summary>
/// <remarks>
/// <para>
/// The readiness stage is the final pipeline stage emitted by the migration acceptance suite
/// described in issue #1024 (scan -> manifest -> apply/dry-run -> publish -> parity -> readiness).
/// It does not re-run probes against the source or the target: the upstream stage reports are the
/// authoritative inputs and the readiness attestation is a deterministic projection of their
/// outcomes plus cited artifact hashes so cutover gates can verify the attestation against the
/// same artifacts.
/// </para>
/// <para>
/// Classification rules:
/// <list type="bullet">
///   <item>every parity probe is <c>pass</c> and no manual-review items remain -&gt;
///   <see cref="MigrationReadinessStatuses.Ready"/>;</item>
///   <item>at least one parity probe is <c>fail</c> -&gt;
///   <see cref="MigrationReadinessStatuses.NotReady"/>;</item>
///   <item>otherwise (manual-review items only, or parity warnings without failures) -&gt;
///   <see cref="MigrationReadinessStatuses.Conditional"/>.</item>
/// </list>
/// </para>
/// </remarks>
public static class MigrationAcceptanceReadinessStageRunner
{
    private const string ScanStage = "scan";
    private const string ApplyStage = "apply";
    private const string ParityStage = "parity";

    private const string ParityFailClassification = "fail";
    private const string ParityWarnClassification = "warn";
    private const string ParityManualReviewClassification = "manual-review";

    /// <summary>
    /// Build a deterministic readiness stage report from the supplied scan, apply, and parity
    /// stage reports.
    /// </summary>
    /// <param name="runId">Stable identifier for this acceptance readiness run.</param>
    /// <param name="scanReport">Upstream scan stage report.</param>
    /// <param name="applyReport">Upstream apply stage report.</param>
    /// <param name="parityReport">Upstream parity stage report.</param>
    /// <returns>Aggregate report pinning the per-source readiness attestations.</returns>
    public static MigrationReadinessStageReport BuildReport(
        string runId,
        MigrationScanStageReport scanReport,
        MigrationApplyStageReport applyReport,
        MigrationParityStageReport parityReport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(scanReport);
        ArgumentNullException.ThrowIfNull(applyReport);
        ArgumentNullException.ThrowIfNull(parityReport);

        var scanByFixture = scanReport.Sources
            .GroupBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var applyByFixture = applyReport.Sources
            .GroupBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var entries = parityReport.Sources
            .Select(parityEntry =>
            {
                if (!scanByFixture.TryGetValue(parityEntry.FixtureId, out var scanEntry))
                {
                    throw new ArgumentException(
                        $"Readiness stage is missing the scan entry for fixture '{parityEntry.FixtureId}'.",
                        nameof(scanReport));
                }
                if (!applyByFixture.TryGetValue(parityEntry.FixtureId, out var applyEntry))
                {
                    throw new ArgumentException(
                        $"Readiness stage is missing the apply entry for fixture '{parityEntry.FixtureId}'.",
                        nameof(applyReport));
                }

                var attestation = BuildAttestation(scanEntry, applyEntry, parityEntry);
                return new MigrationReadinessStageEntry
                {
                    FixtureId = parityEntry.FixtureId,
                    SourceKind = parityEntry.SourceKind,
                    Status = attestation.Status,
                    Attestation = attestation
                };
            })
            .OrderBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToArray();

        return new MigrationReadinessStageReport
        {
            RunId = runId,
            ScanRunId = scanReport.RunId,
            ApplyRunId = applyReport.RunId,
            ParityRunId = parityReport.RunId,
            Summary = BuildSummary(entries),
            Sources = entries
        };
    }

    /// <summary>
    /// Build a deterministic per-source readiness attestation. Exposed for callers (and tests) that
    /// drive the readiness stage one fixture at a time.
    /// </summary>
    /// <param name="scanEntry">Per-source scan stage entry for the fixture.</param>
    /// <param name="applyEntry">Per-source apply stage entry for the fixture.</param>
    /// <param name="parityEntry">Per-source parity stage entry for the fixture.</param>
    /// <returns>Deterministic per-source readiness attestation.</returns>
    public static MigrationReadinessAttestationArtifact BuildAttestation(
        MigrationScanStageEntry scanEntry,
        MigrationApplyStageEntry applyEntry,
        MigrationParityStageEntry parityEntry)
    {
        ArgumentNullException.ThrowIfNull(scanEntry);
        ArgumentNullException.ThrowIfNull(applyEntry);
        ArgumentNullException.ThrowIfNull(parityEntry);

        if (!string.Equals(scanEntry.FixtureId, parityEntry.FixtureId, StringComparison.Ordinal)
            || !string.Equals(applyEntry.FixtureId, parityEntry.FixtureId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Readiness stage inputs must agree on fixture identifier across scan, apply, and parity entries.");
        }

        var reasons = BuildReasons(applyEntry, parityEntry);
        var status = ClassifyStatus(parityEntry, applyEntry, reasons);
        var summary = BuildAttestationSummary(status, parityEntry, applyEntry, reasons);
        var citations = BuildCitations(scanEntry, applyEntry, parityEntry);
        var replayToken = ComputeReplayToken(parityEntry.FixtureId, status, reasons, citations);

        return new MigrationReadinessAttestationArtifact
        {
            FixtureId = parityEntry.FixtureId,
            SourceKind = parityEntry.SourceKind,
            Status = status,
            Summary = summary,
            ReplayToken = replayToken,
            Reasons = reasons,
            EvidenceCitations = citations
        };
    }

    private static MigrationReadinessReason[] BuildReasons(
        MigrationApplyStageEntry applyEntry,
        MigrationParityStageEntry parityEntry)
    {
        var reasons = new List<MigrationReadinessReason>();

        foreach (var diagnostic in parityEntry.Outcome.Diagnostics)
        {
            var severity = MapParityDiagnosticSeverity(diagnostic.Severity);
            reasons.Add(new MigrationReadinessReason
            {
                Code = $"readiness.parity.{diagnostic.Severity}",
                Severity = severity,
                Stage = ParityStage,
                SourceId = diagnostic.SourceId,
                Message = diagnostic.Message
            });
        }

        foreach (var reviewItem in applyEntry.Outcome.ManualReviewItems)
        {
            reasons.Add(new MigrationReadinessReason
            {
                Code = "readiness.apply.manual-review",
                Severity = MigrationReadinessReasonSeverities.ManualReview,
                Stage = ApplyStage,
                SourceId = reviewItem.SourceId,
                Message = reviewItem.Reason
            });
        }

        foreach (var diagnostic in applyEntry.Outcome.Diagnostics)
        {
            if (string.Equals(diagnostic.Severity, "unsupported", StringComparison.Ordinal))
            {
                reasons.Add(new MigrationReadinessReason
                {
                    Code = "readiness.apply.unsupported",
                    Severity = MigrationReadinessReasonSeverities.Fail,
                    Stage = ApplyStage,
                    SourceId = diagnostic.SourceId,
                    Message = diagnostic.Message
                });
            }
        }

        return reasons
            .OrderBy(static reason => SeverityOrder(reason.Severity))
            .ThenBy(static reason => reason.Stage, StringComparer.Ordinal)
            .ThenBy(static reason => reason.Code, StringComparer.Ordinal)
            .ThenBy(static reason => reason.SourceId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static reason => reason.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static string MapParityDiagnosticSeverity(string parityDiagnosticSeverity)
        => parityDiagnosticSeverity switch
        {
            ParityFailClassification => MigrationReadinessReasonSeverities.Fail,
            ParityWarnClassification => MigrationReadinessReasonSeverities.Warn,
            ParityManualReviewClassification => MigrationReadinessReasonSeverities.ManualReview,
            _ => MigrationReadinessReasonSeverities.Info
        };

    private static int SeverityOrder(string severity)
        => severity switch
        {
            MigrationReadinessReasonSeverities.Fail => 0,
            MigrationReadinessReasonSeverities.ManualReview => 1,
            MigrationReadinessReasonSeverities.Warn => 2,
            MigrationReadinessReasonSeverities.Info => 3,
            _ => 4
        };

    private static string ClassifyStatus(
        MigrationParityStageEntry parityEntry,
        MigrationApplyStageEntry applyEntry,
        MigrationReadinessReason[] reasons)
    {
        if (reasons.Any(static reason =>
            string.Equals(reason.Severity, MigrationReadinessReasonSeverities.Fail, StringComparison.Ordinal)))
        {
            return MigrationReadinessStatuses.NotReady;
        }

        if (string.Equals(parityEntry.Classification, ParityFailClassification, StringComparison.Ordinal))
        {
            return MigrationReadinessStatuses.NotReady;
        }

        if (reasons.Any(static reason =>
            string.Equals(reason.Severity, MigrationReadinessReasonSeverities.ManualReview, StringComparison.Ordinal)
            || string.Equals(reason.Severity, MigrationReadinessReasonSeverities.Warn, StringComparison.Ordinal)))
        {
            return MigrationReadinessStatuses.Conditional;
        }

        if (applyEntry.Outcome.ManualReviewItemCount > 0
            || applyEntry.Outcome.UnsupportedItemCount > 0
            || parityEntry.Outcome.ManualReviewResourceCount > 0)
        {
            return MigrationReadinessStatuses.Conditional;
        }

        if (string.Equals(parityEntry.Classification, ParityManualReviewClassification, StringComparison.Ordinal)
            || string.Equals(parityEntry.Classification, ParityWarnClassification, StringComparison.Ordinal))
        {
            return MigrationReadinessStatuses.Conditional;
        }

        return MigrationReadinessStatuses.Ready;
    }

    private static string BuildAttestationSummary(
        string status,
        MigrationParityStageEntry parityEntry,
        MigrationApplyStageEntry applyEntry,
        MigrationReadinessReason[] reasons)
    {
        var failCount = reasons.Count(static reason =>
            string.Equals(reason.Severity, MigrationReadinessReasonSeverities.Fail, StringComparison.Ordinal));
        var manualReviewCount = reasons.Count(static reason =>
            string.Equals(reason.Severity, MigrationReadinessReasonSeverities.ManualReview, StringComparison.Ordinal));
        var warnCount = reasons.Count(static reason =>
            string.Equals(reason.Severity, MigrationReadinessReasonSeverities.Warn, StringComparison.Ordinal));

        return status switch
        {
            MigrationReadinessStatuses.Ready =>
                $"Source '{parityEntry.FixtureId}' is ready for cutover: parity probes passed across "
                + $"{parityEntry.Outcome.ResourceCount} resource(s) and the apply stage staged "
                + $"{applyEntry.Outcome.AppliedItemCount} item(s) with no manual review.",
            MigrationReadinessStatuses.Conditional =>
                $"Source '{parityEntry.FixtureId}' is conditionally ready for cutover: "
                + $"{manualReviewCount} manual-review and {warnCount} warning reason(s) must be closed before cutover.",
            _ =>
                $"Source '{parityEntry.FixtureId}' is not ready for cutover: "
                + $"{failCount} failing reason(s) detected across the upstream acceptance stages.",
        };
    }

    private static MigrationReadinessEvidenceCitation[] BuildCitations(
        MigrationScanStageEntry scanEntry,
        MigrationApplyStageEntry applyEntry,
        MigrationParityStageEntry parityEntry)
    {
        var citations = new List<MigrationReadinessEvidenceCitation>
        {
            new()
            {
                Stage = ScanStage,
                ArtifactKind = scanEntry.Inventory.ArtifactKind,
                ArtifactHash = ComputeArtifactHash(scanEntry.Inventory)
            },
            new()
            {
                Stage = ApplyStage,
                ArtifactKind = applyEntry.Outcome.Manifest.ArtifactKind,
                ArtifactHash = ComputeArtifactHash(applyEntry.Outcome.Manifest)
            },
            new()
            {
                Stage = ParityStage,
                ArtifactKind = parityEntry.Outcome.Evidence.ArtifactKind,
                ArtifactHash = ComputeArtifactHash(parityEntry.Outcome.Evidence),
                ReplayToken = parityEntry.Outcome.ReplayToken
            }
        };

        return citations
            .OrderBy(static citation => citation.Stage, StringComparer.Ordinal)
            .ThenBy(static citation => citation.ArtifactKind, StringComparer.Ordinal)
            .ToArray();
    }

    private static MigrationReadinessStageSummary BuildSummary(MigrationReadinessStageEntry[] entries)
    {
        var ready = 0;
        var conditional = 0;
        var notReady = 0;
        var reasonCount = 0;
        var citationCount = 0;

        foreach (var entry in entries)
        {
            switch (entry.Status)
            {
                case MigrationReadinessStatuses.Ready: ready++; break;
                case MigrationReadinessStatuses.Conditional: conditional++; break;
                case MigrationReadinessStatuses.NotReady: notReady++; break;
                default: break;
            }

            reasonCount += entry.Attestation.Reasons.Length;
            citationCount += entry.Attestation.EvidenceCitations.Length;
        }

        return new MigrationReadinessStageSummary
        {
            SourceCount = entries.Length,
            ReadySourceCount = ready,
            ConditionalSourceCount = conditional,
            NotReadySourceCount = notReady,
            ReasonCount = reasonCount,
            EvidenceCitationCount = citationCount
        };
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "Hash artifact for deterministic replay token; serializer reads public properties only and is not part of the wire protocol.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "AOT",
        "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling",
        Justification = "Hash artifact for deterministic replay token; the AOT host registers the three artifact types via ImportJsonContext source-gen used elsewhere.")]
    private static string ComputeArtifactHash<T>(T artifact)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(artifact, ArtifactHashJsonOptions);
        var hash = SHA256.HashData(json);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static readonly JsonSerializerOptions ArtifactHashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static string ComputeReplayToken(
        string fixtureId,
        string status,
        MigrationReadinessReason[] reasons,
        MigrationReadinessEvidenceCitation[] citations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("fixtureId", fixtureId);
            writer.WriteString("status", status);
            writer.WriteStartArray("reasons");
            foreach (var reason in reasons)
            {
                writer.WriteStartObject();
                writer.WriteString("code", reason.Code);
                writer.WriteString("severity", reason.Severity);
                writer.WriteString("stage", reason.Stage);
                if (reason.SourceId is not null)
                {
                    writer.WriteString("sourceId", reason.SourceId);
                }
                writer.WriteString("message", reason.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("citations");
            foreach (var citation in citations)
            {
                writer.WriteStartObject();
                writer.WriteString("stage", citation.Stage);
                writer.WriteString("artifactKind", citation.ArtifactKind);
                writer.WriteString("artifactHash", citation.ArtifactHash);
                if (citation.ReplayToken is not null)
                {
                    writer.WriteString("replayToken", citation.ReplayToken);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.ToArray());
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
