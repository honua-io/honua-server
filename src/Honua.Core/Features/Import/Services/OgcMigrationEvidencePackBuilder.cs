// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Slice 5 of issue #1016. Aggregates the slice 1-4 outputs emitted by a
/// classic OGC (WFS / WMS / WMTS) migration run into a single deterministic
/// <see cref="OgcMigrationEvidencePackArtifact"/>.
/// </summary>
/// <remarks>
/// <para>
/// The builder is intentionally pure and side-effect free so the same inputs
/// always produce the same output (and the same
/// <see cref="OgcMigrationEvidencePackArtifact.BundleFingerprint"/>). It
/// performs no I/O, no logging, and no clock reads except for the caller-
/// supplied <see cref="OgcMigrationEvidencePackBuilderOptions.GeneratedAt"/>
/// stamp, which is excluded from the fingerprint.
/// </para>
/// <para>
/// Credential redaction: every embedded source URL is sanitized by stripping
/// userinfo, query, and fragment components. The builder never copies raw
/// capabilities documents, feature payloads, or tile bytes — only deterministic
/// counts, ordered plan entries, and plan diagnostics surfaced by slices 1-4.
/// </para>
/// </remarks>
public static class OgcMigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.ogc.evidence-pack-builder/1.0";

    /// <summary>
    /// Build an OGC migration evidence pack from the slice 1-4 inputs.
    /// </summary>
    /// <param name="inputs">Inventory, WFS import, WMS/WMTS plans, and tile-cache export inputs.</param>
    /// <param name="options">Optional run id / generator / clock overrides.</param>
    public static OgcMigrationEvidencePackArtifact Build(
        OgcMigrationEvidencePackInputs inputs,
        OgcMigrationEvidencePackBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Inventory);

        var resolvedOptions = options ?? new OgcMigrationEvidencePackBuilderOptions();

        var redactedInventory = inputs.Inventory with
        {
            Source = RedactSource(inputs.Inventory.Source)
        };

        var redactedWfs = inputs.WfsImport is null
            ? null
            : inputs.WfsImport with
            {
                SourceServiceUrl = RedactUrl(inputs.WfsImport.SourceServiceUrl),
                Manifest = inputs.WfsImport.Manifest with
                {
                    Source = RedactSource(inputs.WfsImport.Manifest.Source)
                }
            };

        var redactedTileCache = inputs.TileCacheExport is null
            ? null
            : inputs.TileCacheExport with
            {
                SourceServiceUrl = RedactUrl(inputs.TileCacheExport.SourceServiceUrl),
                Manifest = inputs.TileCacheExport.Manifest with
                {
                    Source = RedactSource(inputs.TileCacheExport.Manifest.Source)
                }
            };

        var wmsStage = BuildRenderStage("ogc-wms", inputs.WmsPlan);
        var wmtsStage = BuildRenderStage("ogc-wmts", inputs.WmtsPlan);

        var summary = BuildSummary(
            redactedInventory,
            redactedWfs,
            wmsStage,
            wmtsStage,
            redactedTileCache);

        var bundle = new OgcMigrationEvidencePackBundle
        {
            SourceKind = redactedInventory.SourceKind,
            Source = RedactSource(redactedInventory.Source),
            Summary = summary,
            Inventory = redactedInventory,
            WfsImport = redactedWfs,
            WmsPlan = wmsStage,
            WmtsPlan = wmtsStage,
            TileCacheExport = redactedTileCache
        };

        var fingerprint = ComputeBundleFingerprint(bundle);

        return new OgcMigrationEvidencePackArtifact
        {
            RunId = string.IsNullOrWhiteSpace(resolvedOptions.RunId)
                ? "ogc-migration-evidence-run"
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
    /// Compute the deterministic SHA-256 fingerprint that is also embedded in
    /// the pack via <see cref="OgcMigrationEvidencePackArtifact.BundleFingerprint"/>.
    /// Exposed for tests and downstream verifiers.
    /// </summary>
    public static string ComputeBundleFingerprint(OgcMigrationEvidencePackBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            bundle,
            OgcMigrationEvidencePackJsonContext.Default.OgcMigrationEvidencePackBundle);
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

    private static OgcMigrationEvidencePackRenderStage BuildRenderStage(
        string serviceKind,
        OgcRenderMigrationPlanResult? plan)
    {
        if (plan is null || (plan.Value.Entries.Length == 0 && plan.Value.Diagnostics.Length == 0))
        {
            return OgcMigrationEvidencePackRenderStage.Empty(serviceKind);
        }

        var entries = plan.Value.Entries
            .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = plan.Value.Diagnostics
            .OrderBy(static diag => diag.SourceId, StringComparer.Ordinal)
            .ThenBy(static diag => diag.Code, StringComparer.Ordinal)
            .ToArray();

        return new OgcMigrationEvidencePackRenderStage
        {
            ServiceKind = serviceKind,
            EntryCount = entries.Length,
            AutomatedCount = entries.Count(static e => e.AutomationStatus == MigrationFidelityAutomationStatuses.Automated),
            AssistedCount = entries.Count(static e => e.AutomationStatus == MigrationFidelityAutomationStatuses.Assisted),
            ManualReviewCount = entries.Count(static e => e.AutomationStatus == MigrationFidelityAutomationStatuses.ManualReview),
            UnsupportedCount = entries.Count(static e => e.AutomationStatus == MigrationFidelityAutomationStatuses.Unsupported),
            Entries = entries,
            Diagnostics = diagnostics
        };
    }

    private static OgcMigrationEvidencePackSummary BuildSummary(
        MigrationSourceInventoryArtifact inventory,
        OgcWfsImportResult? wfsImport,
        OgcMigrationEvidencePackRenderStage wmsStage,
        OgcMigrationEvidencePackRenderStage wmtsStage,
        OgcTileCacheExportResult? tileCacheExport)
    {
        var renderManualOrUnsupported =
            wmsStage.ManualReviewCount + wmsStage.UnsupportedCount +
            wmtsStage.ManualReviewCount + wmtsStage.UnsupportedCount;

        return new OgcMigrationEvidencePackSummary
        {
            InventoryContainerCount = inventory.Summary.ContainerCount,
            InventoryResourceCount = inventory.Summary.ResourceCount,
            InventoryStyleCount = inventory.Summary.StyleCount,
            WfsImportExecuted = wfsImport is not null,
            WfsFeatureTypesImported = wfsImport?.FeatureTypesImported ?? 0,
            WfsFeatureTypesSkipped = wfsImport?.FeatureTypesSkipped ?? 0,
            WfsFeaturesCopied = wfsImport?.FeaturesCopied ?? 0,
            WmsPlanEntryCount = wmsStage.EntryCount,
            WmtsPlanEntryCount = wmtsStage.EntryCount,
            RenderManualReviewOrUnsupportedCount = renderManualOrUnsupported,
            TileCacheExportExecuted = tileCacheExport is not null,
            TileCacheTileSetsExported = tileCacheExport?.TileSetsExported ?? 0,
            TileCacheTileSetsSkipped = tileCacheExport?.TileSetsSkipped ?? 0,
            TileCacheTilesPersisted = tileCacheExport?.TilesPersisted ?? 0,
            TileCacheTilesFailed = tileCacheExport?.TilesFailed ?? 0
        };
    }
}

/// <summary>
/// Aggregated inputs consumed by <see cref="OgcMigrationEvidencePackBuilder.Build"/>.
/// </summary>
public sealed record OgcMigrationEvidencePackInputs
{
    /// <summary>
    /// Slice-1 inventory artifact captured from the OGC service scan.
    /// Required: the inventory anchors the pack to a single source.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Slice-2 WFS data-import result, or <c>null</c> when no WFS data
    /// import was executed (e.g. WMS- or WMTS-only run).
    /// </summary>
    public OgcWfsImportResult? WfsImport { get; init; }

    /// <summary>
    /// Slice-3 WMS migration planner output, or <c>null</c> when no WMS
    /// planning was executed.
    /// </summary>
    public OgcRenderMigrationPlanResult? WmsPlan { get; init; }

    /// <summary>
    /// Slice-3 WMTS migration planner output, or <c>null</c> when no WMTS
    /// planning was executed.
    /// </summary>
    public OgcRenderMigrationPlanResult? WmtsPlan { get; init; }

    /// <summary>
    /// Slice-4 tile-cache export result, or <c>null</c> when no tile cache
    /// export was executed.
    /// </summary>
    public OgcTileCacheExportResult? TileCacheExport { get; init; }
}

/// <summary>
/// Override hooks for tests and the nightly workflow.
/// </summary>
public sealed record OgcMigrationEvidencePackBuilderOptions
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
    /// Generation timestamp. Excluded from the bundle fingerprint. Defaults to
    /// <see cref="DateTimeOffset.UnixEpoch"/> when omitted so deterministic
    /// tests do not have to set it.
    /// </summary>
    public DateTimeOffset? GeneratedAt { get; init; }
}
