// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#pragma warning disable CA1859 // Evidence-pack builders take IReadOnlyList<T> so callers can pass List/array/immutable collections uniformly; the perf difference is not on a hot path.

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

// The fingerprint excludes the caller timestamp, and the builder strips URL secrets and never copies raster payloads or style documents.
public static class OgcCoverageMigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.ogc-coverage-evidence-pack-builder/1.0";

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

public sealed record OgcCoverageMigrationEvidencePackInputs
{
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public OgcCoverageImportResult? OgcApiCoveragesImport { get; init; }

    public OgcWcsImportResult? WcsImport { get; init; }

    public IReadOnlyCollection<string>? RequestedCoverageIds { get; init; }
}

public sealed record OgcCoverageMigrationEvidencePackBuilderOptions
{
    public string? RunId { get; init; }

    public string? Generator { get; init; }

    public DateTimeOffset? GeneratedAt { get; init; }
}
