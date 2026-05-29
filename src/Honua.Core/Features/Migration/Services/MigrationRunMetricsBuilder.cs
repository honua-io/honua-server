// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Produces release-safe <see cref="MigrationRunMetricsArtifact"/> instances from a
/// <see cref="MigrationRunMetricsRecorder"/> snapshot. URLs, credentials, query strings,
/// and fragments are stripped before emission.
/// </summary>
public static class MigrationRunMetricsBuilder
{
    private static readonly string[] OmittedFieldList =
    [
        "source.baseUrl",
        "source.userInfo",
        "source.queryString",
        "source.fragment",
        "credential values",
        "source data samples"
    ];

    private static readonly string[] PreferredPhaseOrder =
    [
        MigrationCostPerformancePhases.Scan,
        MigrationCostPerformancePhases.Manifest,
        MigrationCostPerformancePhases.Apply,
        MigrationCostPerformancePhases.Import
    ];

    /// <summary>
    /// Builds a deterministic artifact from a recorder snapshot.
    /// </summary>
    public static MigrationRunMetricsArtifact Build(
        MigrationRunMetricsRecorder recorder,
        string sourceKind,
        string sourceFamily,
        MigrationSourceIdentity source,
        string measurementScope,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFamily);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(measurementScope);

        var snapshot = recorder.SnapshotPhases();
        var phases = OrderPhases(snapshot.Keys)
            .Select(phase => snapshot[phase].ToValues() switch
            {
                var values => new MigrationRunMetricsPhase
                {
                    Phase = phase,
                    StartedAt = snapshot[phase].StartedAt,
                    CompletedAt = snapshot[phase].CompletedAt,
                    Metrics = values
                }
            })
            .ToArray();

        var resumeMarkers = recorder.SnapshotResumeMarkers()
            .OrderBy(static marker => marker, StringComparer.Ordinal)
            .ToArray();

        var samples = recorder.SnapshotSamples()
            .OrderBy(static sample => sample.SampledAt)
            .ToArray();

        return new MigrationRunMetricsArtifact
        {
            SourceKind = SafeIdentifier(sourceKind, "unknown-source"),
            SourceFamily = SafeIdentifier(sourceFamily, "unknown-source"),
            Source = BuildSourceSummary(source),
            RunId = string.IsNullOrWhiteSpace(runId) ? null : SafeIdentifier(runId!, "run"),
            MeasurementScope = SafeText(measurementScope, "migration run"),
            StartedAt = recorder.StartedAt,
            CompletedAt = recorder.CompletedAt,
            Totals = recorder.SnapshotTotals(),
            Phases = phases,
            ResourceSamples = samples,
            ResumeMarkers = resumeMarkers,
            Privacy = new MigrationRunMetricsPrivacySummary
            {
                SourceUrlsIncluded = false,
                CredentialValuesIncluded = false,
                SourceDataIncluded = false,
                OmittedFields = OmittedFieldList
            }
        };
    }

    private static MigrationRunMetricsSourceSummary BuildSourceSummary(MigrationSourceIdentity source)
        => new()
        {
            DisplayName = SafeDisplayName(source),
            Product = SafeNullableText(source.Product),
            Version = SafeNullableText(source.Version),
            ServiceType = SafeNullableText(source.ServiceType)
        };

    private static string[] OrderPhases(IEnumerable<string> phases)
    {
        var observed = phases.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<string>(observed.Count);
        foreach (var preferred in PreferredPhaseOrder)
        {
            if (observed.Remove(preferred))
            {
                ordered.Add(preferred);
            }
        }

        foreach (var leftover in observed.OrderBy(static phase => phase, StringComparer.Ordinal))
        {
            ordered.Add(leftover);
        }

        return ordered.ToArray();
    }

    private static string SafeDisplayName(MigrationSourceIdentity source)
    {
        var raw = source.DisplayName;
        if (string.IsNullOrWhiteSpace(raw)) return "migration source";

        if (LooksLikeUrl(raw)) return SafeHost(raw);

        // Strip embedded user info or query strings inside a display name.
        var sanitized = raw
            .Split('?', 2, StringSplitOptions.None)[0]
            .Split('#', 2, StringSplitOptions.None)[0];

        if (sanitized.Contains('@', StringComparison.Ordinal))
        {
            sanitized = sanitized[(sanitized.LastIndexOf('@') + 1)..];
        }

        sanitized = sanitized.Trim();
        if (string.IsNullOrWhiteSpace(sanitized)) return "migration source";
        return Truncate(sanitized, 256);
    }

    private static string SafeHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return string.IsNullOrWhiteSpace(parsed.Host) ? "migration source" : parsed.Host;
        }

        return "migration source";
    }

    private static bool LooksLikeUrl(string value)
        => value.Contains("://", StringComparison.Ordinal);

    private static string? SafeNullableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (LooksLikeUrl(value)) return null;
        return Truncate(value.Trim(), 256);
    }

    private static string SafeText(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim();
        if (LooksLikeUrl(trimmed)) return fallback;
        return Truncate(trimmed, 256);
    }

    private static string SafeIdentifier(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim().ToLowerInvariant();
        if (LooksLikeUrl(trimmed)) return fallback;
        var sanitized = new string(trimmed.Select(static character =>
            char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'
                ? character
                : '-').ToArray());
        sanitized = sanitized.Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : Truncate(sanitized, 128);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
