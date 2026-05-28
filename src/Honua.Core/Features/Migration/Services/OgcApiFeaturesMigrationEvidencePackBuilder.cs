// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Slice 5 (capstone) of issue #1029. Aggregates the slice 1-4 artifacts emitted by a
/// successful OGC API Features migration run into a single deterministic
/// <see cref="OgcApiFeaturesMigrationEvidencePackArtifact"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder is intentionally pure and side-effect free so the same inputs always
/// produce the same output (and the same
/// <see cref="OgcApiFeaturesMigrationEvidencePackArtifact.BundleFingerprint"/>). It performs
/// no I/O, no logging, and no clock reads except for the caller-supplied
/// <see cref="OgcApiFeaturesMigrationEvidencePackBuilderOptions.GeneratedAt"/> stamp, which
/// is excluded from the fingerprint.
/// </para>
/// <para>
/// Credential redaction: the source identity is sanitized by stripping userinfo, query,
/// and fragment components from <c>BaseUrl</c> before the inventory snapshot is included
/// in the bundle. The builder never copies raw response bodies, HTTP headers, or feature
/// payloads — only counts, identifiers, slice-3 filter-scope records, and slice-4
/// diagnostic messages.
/// </para>
/// </remarks>
public static class OgcApiFeaturesMigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.ogc-api-features.evidence-pack-builder/1.0";
    private const string OgcApiFeaturesSourceKind = "ogc-api-features";

    /// <summary>
    /// Builds an evidence pack from the slice 1-4 inputs.
    /// </summary>
    /// <param name="inputs">Inventory, per-collection import, and per-collection scope inputs.</param>
    /// <param name="options">Optional run id / generator / clock overrides.</param>
    /// <returns>Deterministic per-source evidence pack with credentials stripped.</returns>
    public static OgcApiFeaturesMigrationEvidencePackArtifact Build(
        OgcApiFeaturesMigrationEvidencePackInputs inputs,
        OgcApiFeaturesMigrationEvidencePackBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Inventory);

        var resolvedOptions = options ?? new OgcApiFeaturesMigrationEvidencePackBuilderOptions();

        var redactedSource = RedactSource(inputs.Inventory.Source);
        var redactedInventory = inputs.Inventory with { Source = redactedSource };

        var collections = BuildCollections(inputs.CollectionResults);
        var summary = BuildSummary(redactedInventory, collections);

        var bundle = new OgcApiFeaturesMigrationEvidencePackBundle
        {
            SourceKind = string.IsNullOrWhiteSpace(redactedInventory.SourceKind)
                ? OgcApiFeaturesSourceKind
                : redactedInventory.SourceKind,
            Source = redactedSource,
            Summary = summary,
            Inventory = redactedInventory,
            Collections = collections
        };

        var fingerprint = ComputeBundleFingerprint(bundle);

        return new OgcApiFeaturesMigrationEvidencePackArtifact
        {
            RunId = string.IsNullOrWhiteSpace(resolvedOptions.RunId)
                ? "ogc-api-features-evidence-run"
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
    /// Computes the deterministic SHA-256 fingerprint embedded in the pack via
    /// <see cref="OgcApiFeaturesMigrationEvidencePackArtifact.BundleFingerprint"/>. Exposed
    /// for tests and downstream verifiers.
    /// </summary>
    public static string ComputeBundleFingerprint(OgcApiFeaturesMigrationEvidencePackBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            bundle,
            OgcApiFeaturesMigrationEvidencePackJsonContext.Default.OgcApiFeaturesMigrationEvidencePackBundle);
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

    private static OgcApiFeaturesMigrationEvidencePackCollectionResult[] BuildCollections(
        IReadOnlyCollection<OgcApiFeaturesMigrationEvidencePackCollectionInput>? collectionInputs)
    {
        if (collectionInputs is null || collectionInputs.Count == 0)
        {
            return [];
        }

        return collectionInputs
            .Where(static input => input is not null && input.Result is not null)
            .Select(static input => BuildCollection(input))
            .OrderBy(static collection => collection.CollectionId, StringComparer.Ordinal)
            .ThenBy(static collection => collection.Target, StringComparer.Ordinal)
            .ToArray();
    }

    private static OgcApiFeaturesMigrationEvidencePackCollectionResult BuildCollection(
        OgcApiFeaturesMigrationEvidencePackCollectionInput input)
    {
        var result = input.Result;

        var mappingDiagnostics = (result.MappingDiagnostics ?? [])
            .Where(static diagnostic => diagnostic is not null)
            .OrderBy(static diagnostic => diagnostic.PropertyName, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Classification)
            .ToArray();

        var filterScope = new OgcApiFeaturesMigrationEvidencePackFilterScope
        {
            Filter = NormalizeNullable(input.Filter),
            Bbox = NormalizeNullable(input.Bbox),
            Datetime = NormalizeNullable(input.Datetime),
            ScopeDriftDetected = result.ScopeDriftDetected,
            ManualReviewReason = NormalizeNullable(result.ManualReviewReason)
        };

        return new OgcApiFeaturesMigrationEvidencePackCollectionResult
        {
            CollectionId = result.CollectionId,
            Success = result.Success,
            Target = result.Target,
            FeaturesImported = result.FeaturesImported,
            FeaturesSkipped = result.FeaturesSkipped,
            PagesFetched = result.PagesFetched,
            Truncated = result.Truncated,
            ErrorCode = NormalizeNullable(result.ErrorCode),
            FilterScope = filterScope,
            MappingDiagnostics = mappingDiagnostics
        };
    }

    private static OgcApiFeaturesMigrationEvidencePackSummary BuildSummary(
        MigrationSourceInventoryArtifact inventory,
        IReadOnlyList<OgcApiFeaturesMigrationEvidencePackCollectionResult> collections)
    {
        var totalFeaturesImported = 0L;
        var totalFeaturesSkipped = 0L;
        var totalPagesFetched = 0L;
        var succeeded = 0;
        var failed = 0;
        var truncated = 0;
        var scopeDrift = 0;
        var diagnosticCount = 0;
        var manualReview = 0;
        var unsupported = 0;

        foreach (var collection in collections)
        {
            totalFeaturesImported += collection.FeaturesImported;
            totalFeaturesSkipped += collection.FeaturesSkipped;
            totalPagesFetched += collection.PagesFetched;

            if (collection.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }

            if (collection.Truncated)
            {
                truncated++;
            }

            if (collection.FilterScope.ScopeDriftDetected)
            {
                scopeDrift++;
            }

            foreach (var diagnostic in collection.MappingDiagnostics)
            {
                diagnosticCount++;
                switch (diagnostic.Classification)
                {
                    case OgcApiFeaturesSchemaMappingClassification.ManualReview:
                        manualReview++;
                        break;
                    case OgcApiFeaturesSchemaMappingClassification.Unsupported:
                        unsupported++;
                        break;
                }
            }
        }

        return new OgcApiFeaturesMigrationEvidencePackSummary
        {
            InventoryCollectionCount = inventory.Resources.Length,
            ConformanceClassCount = CountConformanceClasses(inventory),
            CollectionResultCount = collections.Count,
            SucceededCollectionCount = succeeded,
            FailedCollectionCount = failed,
            TotalFeaturesImported = totalFeaturesImported,
            TotalFeaturesSkipped = totalFeaturesSkipped,
            TotalPagesFetched = totalPagesFetched,
            TruncatedCollectionCount = truncated,
            ScopeDriftCollectionCount = scopeDrift,
            TotalSchemaMappingDiagnosticCount = diagnosticCount,
            SchemaMappingManualReviewCount = manualReview,
            SchemaMappingUnsupportedCount = unsupported
        };
    }

    private static int CountConformanceClasses(MigrationSourceInventoryArtifact inventory)
    {
        // The slice-1 scanner records each advertised conformance class as a
        // MigrationExternalDependency with Kind "ogc-api-features-conformance" so it shows
        // up in the inventory dependency graph. Counting them gives reviewers a single
        // signal for how much of the OGC API Features spec the source claims to support.
        return inventory.ExternalDependencies
            .Count(static dependency => string.Equals(
                dependency.Kind,
                "ogc-api-features-conformance",
                StringComparison.Ordinal));
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Aggregated inputs consumed by <see cref="OgcApiFeaturesMigrationEvidencePackBuilder.Build"/>.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackInputs
{
    /// <summary>
    /// Slice-1 inventory artifact captured by the OGC API Features migration scanner.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Slice-2/3 per-collection import results paired with the slice-3 filter/bbox/datetime
    /// pushdown tokens that were sent to the source items endpoint. May be empty when the
    /// scanner ran but no collection imports were attempted.
    /// </summary>
    public IReadOnlyCollection<OgcApiFeaturesMigrationEvidencePackCollectionInput>? CollectionResults { get; init; }
}

/// <summary>
/// One per-collection input pair (import result + pushed-down filter scope tokens) consumed
/// by <see cref="OgcApiFeaturesMigrationEvidencePackBuilder.Build"/>.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackCollectionInput
{
    /// <summary>
    /// Slice-2/3 import result emitted by the OGC API Features import service.
    /// </summary>
    public required OgcApiFeaturesImportResult Result { get; init; }

    /// <summary>
    /// Normalized CQL2-text filter that was supplied to the import run, or <c>null</c> when
    /// no filter was supplied.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Normalized bbox token that was supplied to the import run, or <c>null</c> when no
    /// bbox was supplied.
    /// </summary>
    public string? Bbox { get; init; }

    /// <summary>
    /// Normalized RFC3339 instant or interval that was supplied to the import run, or
    /// <c>null</c> when no datetime was supplied.
    /// </summary>
    public string? Datetime { get; init; }
}

/// <summary>
/// Override hooks for tests and the nightly workflow.
/// </summary>
public sealed record OgcApiFeaturesMigrationEvidencePackBuilderOptions
{
    /// <summary>
    /// Run identifier embedded in the artifact. Excluded from the bundle fingerprint so the
    /// same inputs produce the same fingerprint across nightly runs.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Generator label embedded in the artifact. Excluded from the bundle fingerprint.
    /// </summary>
    public string? Generator { get; init; }

    /// <summary>
    /// Generation timestamp. Excluded from the bundle fingerprint. Defaults to
    /// <see cref="DateTimeOffset.UnixEpoch"/> when omitted so deterministic tests do not have
    /// to set it.
    /// </summary>
    public DateTimeOffset? GeneratedAt { get; init; }
}
