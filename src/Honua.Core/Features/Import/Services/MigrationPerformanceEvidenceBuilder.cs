// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Builds deterministic, website-linkable migration performance evidence artifacts
/// from a slice-1 <see cref="MigrationRunMetricsArtifact"/> + slice-2
/// <see cref="MigrationRunMetricsBaselineArtifact"/> pair (issue #1033 slice 4).
/// </summary>
/// <remarks>
/// AOT-safe: the builder uses no reflection-based serialization. The SHA-256
/// fingerprint is computed by a hand-rolled deterministic canonicalizer so the
/// hash is stable across machines and runtimes regardless of JSON formatting.
/// </remarks>
public static class MigrationPerformanceEvidenceBuilder
{
    /// <summary>
    /// Build the slice-4 performance evidence artifact.
    /// </summary>
    /// <param name="runMetrics">Slice-1 raw run metrics artifact.</param>
    /// <param name="baselineEvaluation">Slice-2 baseline classification output for the same run.</param>
    /// <param name="fixtureProfile">Fixture metadata used to size the baseline.</param>
    /// <param name="generatedAt">Wall-clock instant to stamp on the artifact (workflow-supplied).</param>
    /// <returns>A deterministic artifact ready for release publication.</returns>
    public static MigrationPerformanceEvidenceArtifact Build(
        MigrationRunMetricsArtifact runMetrics,
        MigrationRunMetricsBaselineArtifact baselineEvaluation,
        MigrationFixtureSizeProfile fixtureProfile,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(runMetrics);
        ArgumentNullException.ThrowIfNull(baselineEvaluation);
        ArgumentNullException.ThrowIfNull(fixtureProfile);

        if (!string.Equals(runMetrics.SourceFamily, baselineEvaluation.SourceFamily, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Run metrics source family '{runMetrics.SourceFamily}' does not match baseline source family '{baselineEvaluation.SourceFamily}'.",
                nameof(baselineEvaluation));
        }

        if (!string.Equals(baselineEvaluation.Size, fixtureProfile.Size, StringComparison.Ordinal) ||
            !string.Equals(baselineEvaluation.SourceFamily, fixtureProfile.SourceFamily, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Fixture profile must agree with the baseline source family and size.",
                nameof(fixtureProfile));
        }

        var redaction = BuildRedactionPosture(runMetrics);
        var fingerprint = ComputeFingerprint(runMetrics, baselineEvaluation, fixtureProfile);

        return new MigrationPerformanceEvidenceArtifact
        {
            SourceFamily = baselineEvaluation.SourceFamily,
            FixtureSize = baselineEvaluation.Size,
            BaselineProfile = baselineEvaluation.BaselineProfile,
            Status = baselineEvaluation.Status,
            Summary = BuildSummary(baselineEvaluation, fixtureProfile),
            RunId = runMetrics.RunId,
            MeasurementScope = runMetrics.MeasurementScope,
            GeneratedAt = generatedAt.ToUniversalTime(),
            FixtureProfile = fixtureProfile,
            RunMetrics = runMetrics,
            BaselineEvaluation = baselineEvaluation,
            Fingerprint = fingerprint,
            Redaction = redaction
        };
    }

    private static string BuildSummary(
        MigrationRunMetricsBaselineArtifact baseline,
        MigrationFixtureSizeProfile fixture)
    {
        var subject = $"{baseline.SourceFamily} {baseline.Size} fixture";
        return baseline.Status switch
        {
            MigrationMetricBaselineStatuses.Fail =>
                $"Migration performance evidence for the {subject} failed the {baseline.BaselineProfile} baseline.",
            MigrationMetricBaselineStatuses.Warn =>
                $"Migration performance evidence for the {subject} passed with warnings against the {baseline.BaselineProfile} baseline.",
            _ =>
                $"Migration performance evidence for the {subject} is within the {baseline.BaselineProfile} baseline."
        }
        + (fixture.Description is { Length: > 0 } description ? $" Fixture: {description}" : string.Empty);
    }

    private static MigrationPerformanceEvidenceRedactionPosture BuildRedactionPosture(
        MigrationRunMetricsArtifact runMetrics)
        => new()
        {
            SourceUrlsIncluded = runMetrics.Privacy.SourceUrlsIncluded,
            CredentialValuesIncluded = runMetrics.Privacy.CredentialValuesIncluded,
            SourceDataIncluded = runMetrics.Privacy.SourceDataIncluded,
            OperatorIdentitiesIncluded = false,
            OmittedFields =
            [
                "source.baseUrl",
                "source.userInfo",
                "source.queryString",
                "source.fragment",
                "credential values",
                "operator identifying values",
                "source data samples"
            ],
            Summary = "Deny-by-default posture; URLs, credentials, source data, and operator identifiers are excluded from the artifact."
        };

    /// <summary>
    /// Compute a deterministic SHA-256 fingerprint over a canonical view of the
    /// run metrics + baseline + fixture profile. Hex-encoded lowercase with a
    /// <c>sha256:</c> prefix so consumers can identify the algorithm.
    /// </summary>
    private static string ComputeFingerprint(
        MigrationRunMetricsArtifact runMetrics,
        MigrationRunMetricsBaselineArtifact baseline,
        MigrationFixtureSizeProfile fixture)
    {
        var canonical = new StringBuilder(capacity: 4096);
        AppendRunMetrics(canonical, runMetrics);
        AppendBaseline(canonical, baseline);
        AppendFixtureProfile(canonical, fixture);

        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        var hash = SHA256.HashData(bytes);
        var hex = new StringBuilder("sha256:", capacity: 7 + (hash.Length * 2));
        foreach (var b in hash)
        {
            hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    private static void AppendRunMetrics(StringBuilder sb, MigrationRunMetricsArtifact run)
    {
        sb.Append("run|");
        AppendField(sb, "kind", run.ArtifactKind);
        AppendField(sb, "version", run.ArtifactVersion);
        AppendField(sb, "sourceKind", run.SourceKind);
        AppendField(sb, "sourceFamily", run.SourceFamily);
        AppendField(sb, "displayName", run.Source.DisplayName);
        AppendField(sb, "product", run.Source.Product);
        AppendField(sb, "sourceVersion", run.Source.Version);
        AppendField(sb, "serviceType", run.Source.ServiceType);
        AppendField(sb, "runId", run.RunId);
        AppendField(sb, "scope", run.MeasurementScope);
        AppendField(sb, "startedAt", FormatInstant(run.StartedAt));
        AppendField(sb, "completedAt", FormatInstant(run.CompletedAt));
        AppendValues(sb, "totals", run.Totals);

        foreach (var phase in run.Phases.OrderBy(static p => p.Phase, StringComparer.Ordinal))
        {
            sb.Append("phase[").Append(phase.Phase).Append("]|");
            AppendField(sb, "startedAt", FormatInstant(phase.StartedAt));
            AppendField(sb, "completedAt", FormatInstant(phase.CompletedAt));
            AppendValues(sb, "metrics", phase.Metrics);
        }

        foreach (var sample in run.ResourceSamples.OrderBy(static s => s.SampledAt))
        {
            sb.Append("sample|");
            AppendField(sb, "sampledAt", FormatInstant(sample.SampledAt));
            AppendField(sb, "phase", sample.Phase);
            AppendField(sb, "cpuMs", FormatLong(sample.CpuMilliseconds));
            AppendField(sb, "workingSet", FormatLong(sample.WorkingSetBytes));
            AppendField(sb, "gcHeap", FormatLong(sample.GcHeapBytes));
        }

        foreach (var marker in run.ResumeMarkers.OrderBy(static m => m, StringComparer.Ordinal))
        {
            sb.Append("resume|").Append(marker).Append('|');
        }

        sb.Append("privacy|");
        AppendField(sb, "urls", run.Privacy.SourceUrlsIncluded.ToString());
        AppendField(sb, "creds", run.Privacy.CredentialValuesIncluded.ToString());
        AppendField(sb, "data", run.Privacy.SourceDataIncluded.ToString());
        foreach (var omitted in run.Privacy.OmittedFields.OrderBy(static o => o, StringComparer.Ordinal))
        {
            sb.Append("omit|").Append(omitted).Append('|');
        }
    }

    private static void AppendBaseline(StringBuilder sb, MigrationRunMetricsBaselineArtifact baseline)
    {
        sb.Append("baseline|");
        AppendField(sb, "kind", baseline.ArtifactKind);
        AppendField(sb, "version", baseline.ArtifactVersion);
        AppendField(sb, "sourceFamily", baseline.SourceFamily);
        AppendField(sb, "size", baseline.Size);
        AppendField(sb, "profile", baseline.BaselineProfile);
        AppendField(sb, "status", baseline.Status);
        AppendField(sb, "summary", baseline.Summary);
        AppendField(sb, "runId", baseline.RunId);
        AppendField(sb, "scope", baseline.MeasurementScope);

        foreach (var signal in baseline.Signals.OrderBy(static s => s.Metric, StringComparer.Ordinal))
        {
            sb.Append("signal[").Append(signal.Metric).Append("]|");
            AppendField(sb, "status", signal.Status);
            AppendField(sb, "unit", signal.Unit);
            AppendField(sb, "observed", FormatDouble(signal.Observed));
            AppendField(sb, "warn", FormatDouble(signal.WarnThreshold));
            AppendField(sb, "fail", FormatDouble(signal.FailThreshold));
            AppendField(sb, "summary", signal.Summary);
        }
    }

    private static void AppendFixtureProfile(StringBuilder sb, MigrationFixtureSizeProfile fixture)
    {
        sb.Append("fixture|");
        AppendField(sb, "sourceFamily", fixture.SourceFamily);
        AppendField(sb, "size", fixture.Size);
        AppendField(sb, "description", fixture.Description);
        AppendField(sb, "expResources", FormatLong(fixture.ExpectedResourceCount));
        AppendField(sb, "expFeatures", FormatLong(fixture.ExpectedFeatureCount));
        AppendField(sb, "expCoverages", FormatLong(fixture.ExpectedCoverageCount));
        AppendField(sb, "expDurationMs", FormatLong(fixture.ExpectedDurationMilliseconds));
        AppendField(sb, "expBytesRead", FormatLong(fixture.ExpectedBytesRead));
        AppendField(sb, "expBytesWritten", FormatLong(fixture.ExpectedBytesWritten));
        AppendField(sb, "expSourceRequests", FormatLong(fixture.ExpectedSourceRequestCount));
    }

    private static void AppendValues(StringBuilder sb, string name, MigrationRunMetricsValues values)
    {
        sb.Append(name).Append('|');
        AppendField(sb, "durationMs", FormatLong(values.DurationMilliseconds));
        AppendField(sb, "sourceRequests", FormatLong(values.SourceRequestCount));
        AppendField(sb, "bytesRead", FormatLong(values.BytesRead));
        AppendField(sb, "bytesWritten", FormatLong(values.BytesWritten));
        AppendField(sb, "retryCount", FormatInt(values.RetryCount));
        AppendField(sb, "resumeCount", FormatInt(values.ResumeCount));
        AppendField(sb, "resumeFromCheckpoint", values.ResumeFromCheckpoint?.ToString());
        AppendField(sb, "idempotentReplayCount", FormatInt(values.IdempotentReplayCount));
        AppendField(sb, "cancellationCount", FormatInt(values.CancellationCount));
        AppendField(sb, "cpuMs", FormatLong(values.CpuMilliseconds));
        AppendField(sb, "peakMemoryBytes", FormatLong(values.PeakMemoryBytes));
        AppendField(sb, "dbGrowthBytes", FormatLong(values.DatabaseGrowthBytes));
        AppendField(sb, "dbGrowthRows", FormatLong(values.DatabaseGrowthRows));
        AppendField(sb, "artifactBytes", FormatLong(values.ArtifactBytes));
        AppendField(sb, "resourceCount", FormatLong(values.ResourceCount));
        AppendField(sb, "featureCount", FormatLong(values.FeatureCount));
        AppendField(sb, "coverageCount", FormatLong(values.CoverageCount));
        AppendField(sb, "resourceTps", FormatDouble(values.ResourceThroughputPerSecond));
        AppendField(sb, "featureTps", FormatDouble(values.FeatureThroughputPerSecond));
        AppendField(sb, "coverageTps", FormatDouble(values.CoverageThroughputPerSecond));
        AppendField(sb, "manualReviewCount", FormatInt(values.ManualReviewCount));
        AppendField(sb, "candidateItemCount", FormatInt(values.CandidateItemCount));
        AppendField(sb, "manualReviewRatio", FormatDouble(values.ManualReviewRatio));
    }

    private static void AppendField(StringBuilder sb, string name, string? value)
    {
        sb.Append(name).Append('=').Append(value ?? "null").Append('|');
    }

    private static string FormatInstant(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatLong(long? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static string? FormatInt(int? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static string? FormatDouble(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture);
}
