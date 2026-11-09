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

// The fingerprint excludes the caller timestamp, and the builder strips URL secrets and never copies capabilities documents, feature payloads, or tile bytes.
public static class OgcMigrationEvidencePackBuilder
{
    private const string BuilderGenerator = "honua.migration.ogc.evidence-pack-builder/1.0";

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

public sealed record OgcMigrationEvidencePackInputs
{
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    public OgcWfsImportResult? WfsImport { get; init; }

    public OgcRenderMigrationPlanResult? WmsPlan { get; init; }

    public OgcRenderMigrationPlanResult? WmtsPlan { get; init; }

    public OgcTileCacheExportResult? TileCacheExport { get; init; }
}

public sealed record OgcMigrationEvidencePackBuilderOptions
{
    public string? RunId { get; init; }

    public string? Generator { get; init; }

    public DateTimeOffset? GeneratedAt { get; init; }
}
