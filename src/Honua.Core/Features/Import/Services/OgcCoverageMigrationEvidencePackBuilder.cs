// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CA1859 // Evidence-pack builders take IReadOnlyList<T> so callers can pass List/array/immutable collections uniformly; the perf difference is not on a hot path.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Slice 5 (capstone) of issue #1030. Aggregates the slice 1-4 artifacts
/// emitted by a successful OGC coverage migration run — coverage scanner
/// inventory, OGC API Coverages import (slice 2), legacy WCS import
/// (slice 3), and coverage-style migration diagnostics (slice 4) — into a
/// single deterministic
/// <see cref="OgcCoverageMigrationEvidencePackArtifact"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder is intentionally pure and side-effect free so the same
/// inputs always produce the same output (and the same
/// <see cref="OgcCoverageMigrationEvidencePackArtifact.BundleFingerprint"/>).
/// It performs no I/O, no logging, and no clock reads except for the
/// caller-supplied
/// <see cref="OgcCoverageMigrationEvidencePackBuilderOptions.GeneratedAt"/>
/// stamp, which is excluded from the fingerprint.
/// </para>
/// <para>
/// Credential redaction: the source identity is sanitized by stripping
/// userinfo, query, and fragment components from <c>BaseUrl</c> before the
/// inventory snapshot and per-channel manifests are included in the bundle.
/// The builder never copies raw raster payloads or style documents — only
/// counts, classifications, and slice-4 diagnostic messages.
/// </para>
/// <para>
/// Channels are emitted in canonical order
/// (<see cref="OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages"/>
/// then <see cref="OgcCoverageMigrationEvidencePackChannelIds.Wcs"/>) and
/// only when the corresponding slice-2 or slice-3 result was supplied, so
/// the same set of inputs always produces the same channel order regardless
/// of caller order.
/// </para>
/// </remarks>
public static class OgcCoverageMigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.ogc-coverage-evidence-pack-builder/1.0";

    /// <summary>
    /// Build an evidence pack from the slice 1-4 inputs.
    /// </summary>
    /// <param name="inputs">Inventory and per-channel import inputs.</param>
    /// <param name="options">Optional run id / generator / clock overrides.</param>
    public static OgcCoverageMigrationEvidencePackArtifact Build(
        OgcCoverageMigrationEvidencePackInputs inputs,
        OgcCoverageMigrationEvidencePackBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Inventory);

        if (inputs.OgcApiCoveragesImport is null && inputs.WcsImport is null)
        {
            throw new ArgumentException(
                "At least one of OgcApiCoveragesImport or WcsImport must be supplied.",
                nameof(inputs));
        }

        var resolvedOptions = options ?? new OgcCoverageMigrationEvidencePackBuilderOptions();

        var redactedInventory = inputs.Inventory with { Source = RedactSource(inputs.Inventory.Source) };

        var channels = BuildChannels(inputs);
        var styleDiagnostics = BuildAggregateStyleDiagnostics(inputs);
        var summary = BuildSummary(channels, styleDiagnostics);
        var coverageScope = BuildCoverageScope(inputs.RequestedCoverageIds);

        var bundle = new OgcCoverageMigrationEvidencePackBundle
        {
            SourceKind = inputs.Inventory.SourceKind,
            Source = RedactSource(inputs.Inventory.Source),
            CoverageScope = coverageScope,
            Summary = summary,
            Channels = channels,
            StyleDiagnostics = styleDiagnostics,
            Inventory = redactedInventory
        };

        var fingerprint = ComputeBundleFingerprint(bundle);

        return new OgcCoverageMigrationEvidencePackArtifact
        {
            RunId = string.IsNullOrWhiteSpace(resolvedOptions.RunId)
                ? "ogc-coverage-migration-evidence-run"
                : resolvedOptions.RunId,
            Generator = string.IsNullOrWhiteSpace(resolvedOptions.Generator)
                ? BuilderGenerator
                : resolvedOptions.Generator,
            GeneratedAt = resolvedOptions.GeneratedAt ?? DateTimeOffset.UnixEpoch,
            BundleFingerprint = fingerprint,
            Bundle = bundle
        };
    }

    /// <summary>
    /// Compute the deterministic SHA-256 fingerprint that is also embedded
    /// in the pack via
    /// <see cref="OgcCoverageMigrationEvidencePackArtifact.BundleFingerprint"/>.
    /// Exposed for tests and downstream verifiers.
    /// </summary>
    public static string ComputeBundleFingerprint(OgcCoverageMigrationEvidencePackBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            bundle,
            OgcCoverageMigrationEvidencePackJsonContext.Default.OgcCoverageMigrationEvidencePackBundle);
        var hash = SHA256.HashData(payload);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static OgcCoverageMigrationEvidencePackChannel[] BuildChannels(
        OgcCoverageMigrationEvidencePackInputs inputs)
    {
        var channels = new List<OgcCoverageMigrationEvidencePackChannel>(capacity: 2);

        if (inputs.OgcApiCoveragesImport is { } coverages)
        {
            channels.Add(BuildOgcApiCoveragesChannel(coverages));
        }

        if (inputs.WcsImport is { } wcs)
        {
            channels.Add(BuildWcsChannel(wcs));
        }

        return channels.ToArray();
    }

    private static OgcCoverageMigrationEvidencePackChannel BuildOgcApiCoveragesChannel(
        OgcCoverageImportResult result)
    {
        var orderedRecords = OrderRecords(result.Records);
        return new OgcCoverageMigrationEvidencePackChannel
        {
            Id = OgcCoverageMigrationEvidencePackChannelIds.OgcApiCoverages,
            ApplyMode = result.ApplyMode,
            DryRun = result.DryRun,
            ResolvedVersion = null,
            RequestedOutputFormat = null,
            CoverageCount = orderedRecords.Length,
            ImportedCount = CountByAction(orderedRecords, "imported"),
            PlannedCount = CountByAction(orderedRecords, "planned"),
            SkippedCount = CountByAction(orderedRecords, "skipped"),
            ManualReviewCount = CountByAction(orderedRecords, "manual-review"),
            FailedCount = CountByAction(orderedRecords, "failed"),
            Records = orderedRecords,
            Manifest = RedactManifest(result.Manifest)
        };
    }

    private static OgcCoverageMigrationEvidencePackChannel BuildWcsChannel(OgcWcsImportResult result)
    {
        var orderedRecords = OrderRecords(result.Records);
        return new OgcCoverageMigrationEvidencePackChannel
        {
            Id = OgcCoverageMigrationEvidencePackChannelIds.Wcs,
            ApplyMode = result.ApplyMode,
            DryRun = result.DryRun,
            ResolvedVersion = result.ResolvedVersion,
            RequestedOutputFormat = result.RequestedOutputFormat,
            CoverageCount = orderedRecords.Length,
            ImportedCount = CountByAction(orderedRecords, "imported"),
            PlannedCount = CountByAction(orderedRecords, "planned"),
            SkippedCount = CountByAction(orderedRecords, "skipped"),
            ManualReviewCount = CountByAction(orderedRecords, "manual-review"),
            FailedCount = CountByAction(orderedRecords, "failed"),
            Records = orderedRecords,
            Manifest = RedactManifest(result.Manifest)
        };
    }

    private static OgcCoverageImportRecord[] OrderRecords(OgcCoverageImportRecord[]? records)
    {
        if (records is null || records.Length == 0)
        {
            return [];
        }

        return records
            .OrderBy(static r => r.SourceCoverageId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int CountByAction(IReadOnlyList<OgcCoverageImportRecord> records, string action)
    {
        var count = 0;
        for (var i = 0; i < records.Count; i++)
        {
            if (string.Equals(records[i].Action, action, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static MigrationCoverageStyleDiagnostic[] BuildAggregateStyleDiagnostics(
        OgcCoverageMigrationEvidencePackInputs inputs)
    {
        var diagnostics = new List<MigrationCoverageStyleDiagnostic>();
        if (inputs.OgcApiCoveragesImport?.StyleDiagnostics is { Length: > 0 } a)
        {
            diagnostics.AddRange(a);
        }
        if (inputs.WcsImport?.StyleDiagnostics is { Length: > 0 } w)
        {
            diagnostics.AddRange(w);
        }

        if (diagnostics.Count == 0)
        {
            return [];
        }

        // Deduplicate identical diagnostics that appear in both channels
        // (slice 4 emits per-source-coverage rows, so identical hint+kind+
        // source-coverage pairs across channels collapse to one row in the
        // pack), then order deterministically by (sourceCoverageId, kind,
        // sourceStyleId, reason).
        return diagnostics
            .Distinct(MigrationCoverageStyleDiagnosticEqualityComparer.Instance)
            .OrderBy(static d => d.SourceCoverageId, StringComparer.Ordinal)
            .ThenBy(static d => d.Kind, StringComparer.Ordinal)
            .ThenBy(static d => d.SourceStyleId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static d => d.Reason, StringComparer.Ordinal)
            .ToArray();
    }

    private static OgcCoverageMigrationEvidencePackSummary BuildSummary(
        IReadOnlyList<OgcCoverageMigrationEvidencePackChannel> channels,
        IReadOnlyList<MigrationCoverageStyleDiagnostic> styleDiagnostics)
    {
        var total = 0;
        var imported = 0;
        var planned = 0;
        var skipped = 0;
        var manualReview = 0;
        var failed = 0;

        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            total += channel.CoverageCount;
            imported += channel.ImportedCount;
            planned += channel.PlannedCount;
            skipped += channel.SkippedCount;
            manualReview += channel.ManualReviewCount;
            failed += channel.FailedCount;
        }

        var styleManualReview = 0;
        for (var i = 0; i < styleDiagnostics.Count; i++)
        {
            if (string.Equals(styleDiagnostics[i].Classification, "manual-review", StringComparison.Ordinal))
            {
                styleManualReview++;
            }
        }

        return new OgcCoverageMigrationEvidencePackSummary
        {
            TotalCoverageCount = total,
            ImportedCount = imported,
            PlannedCount = planned,
            SkippedCount = skipped,
            ManualReviewCount = manualReview,
            FailedCount = failed,
            StyleDiagnosticCount = styleDiagnostics.Count,
            StyleManualReviewCount = styleManualReview
        };
    }

    private static OgcCoverageMigrationEvidencePackScope BuildCoverageScope(
        IReadOnlyCollection<string>? requestedCoverageIds)
    {
        if (requestedCoverageIds is null || requestedCoverageIds.Count == 0)
        {
            return new OgcCoverageMigrationEvidencePackScope
            {
                Restricted = false,
                CoverageIds = []
            };
        }

        var ordered = requestedCoverageIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new OgcCoverageMigrationEvidencePackScope
        {
            Restricted = ordered.Length > 0,
            CoverageIds = ordered
        };
    }

    private static MigrationManifestArtifact RedactManifest(MigrationManifestArtifact manifest)
    {
        return manifest with { Source = RedactSource(manifest.Source) };
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

    private sealed class MigrationCoverageStyleDiagnosticEqualityComparer
        : IEqualityComparer<MigrationCoverageStyleDiagnostic>
    {
        public static readonly MigrationCoverageStyleDiagnosticEqualityComparer Instance = new();

        public bool Equals(MigrationCoverageStyleDiagnostic? x, MigrationCoverageStyleDiagnostic? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }
            return string.Equals(x.Kind, y.Kind, StringComparison.Ordinal)
                && string.Equals(x.Classification, y.Classification, StringComparison.Ordinal)
                && string.Equals(x.SourceCoverageId, y.SourceCoverageId, StringComparison.Ordinal)
                && string.Equals(x.SourceStyleId, y.SourceStyleId, StringComparison.Ordinal)
                && string.Equals(x.Reason, y.Reason, StringComparison.Ordinal)
                && string.Equals(x.SuggestedTargetStyleId, y.SuggestedTargetStyleId, StringComparison.Ordinal)
                && string.Equals(x.VendorName, y.VendorName, StringComparison.Ordinal)
                && x.ManualSteps.SequenceEqual(y.ManualSteps, StringComparer.Ordinal);
        }

        public int GetHashCode(MigrationCoverageStyleDiagnostic obj)
        {
            return HashCode.Combine(
                obj.Kind,
                obj.Classification,
                obj.SourceCoverageId,
                obj.SourceStyleId,
                obj.Reason,
                obj.SuggestedTargetStyleId,
                obj.VendorName);
        }
    }
}

/// <summary>
/// Aggregated inputs consumed by
/// <see cref="OgcCoverageMigrationEvidencePackBuilder.Build"/>.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackInputs
{
    /// <summary>
    /// Slice-1 coverage inventory artifact captured from the source scan.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Slice-2 OGC API Coverages import result. Null when the run did not
    /// drive the modern OGC API Coverages channel.
    /// </summary>
    public OgcCoverageImportResult? OgcApiCoveragesImport { get; init; }

    /// <summary>
    /// Slice-3 legacy WCS import result. Null when the run did not drive
    /// the WCS channel.
    /// </summary>
    public OgcWcsImportResult? WcsImport { get; init; }

    /// <summary>
    /// Operator-requested coverage scope (source coverage identifiers), or
    /// <c>null</c>/empty when every inventoried coverage was eligible.
    /// </summary>
    public IReadOnlyCollection<string>? RequestedCoverageIds { get; init; }
}

/// <summary>
/// Override hooks for tests and the nightly workflow.
/// </summary>
public sealed record OgcCoverageMigrationEvidencePackBuilderOptions
{
    /// <summary>
    /// Run identifier embedded in the artifact. Excluded from the bundle
    /// fingerprint so the same inputs produce the same fingerprint across
    /// nightly runs.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Generator label embedded in the artifact. Excluded from the bundle
    /// fingerprint.
    /// </summary>
    public string? Generator { get; init; }

    /// <summary>
    /// Generation timestamp. Excluded from the bundle fingerprint. Defaults
    /// to <see cref="DateTimeOffset.UnixEpoch"/> when omitted so
    /// deterministic tests do not have to set it.
    /// </summary>
    public DateTimeOffset? GeneratedAt { get; init; }
}
