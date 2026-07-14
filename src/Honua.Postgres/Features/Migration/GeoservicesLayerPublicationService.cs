// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Postgres.Features.Metadata;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Owns the post-commit, published-layer lifecycle for ArcGIS GeoServices imports: AutoPublish of the
/// imported table, MapLibre style attachment, and the post-publish data/catalog reconciliation gate.
/// <see cref="GeoservicesImportService"/> commits the imported data and then delegates this ordered
/// publish -> style -> reconcile work here so its own constructor stays within the collaborator ceiling.
/// All behavior (warning text, ordered progress phases, reconciliation verdicts) is identical to the
/// pre-extraction inline implementation; this type is pure delegation that bundles the publishing,
/// style-conversion, style-catalog, reconciliation, and metadata-graph collaborators.
/// </summary>
internal sealed partial class GeoservicesLayerPublicationService
{
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly IGeoServicesStyleConverter? _styleConverter;
    private readonly ILayerStyleCatalog? _styleCatalog;
    private readonly ILayerReconciliationService? _reconciliationService;
    private readonly IMetadataV2GraphWriteBaseReader? _metadataWriteBaseReader;
    private readonly ILogger<GeoservicesLayerPublicationService> _logger;

    /// <summary>
    /// Initializes the publication service. Every collaborator is optional so the host can run the
    /// importer without an admin/publishing surface; the AutoPublish, style, and reconciliation steps
    /// each no-op (with the same operator warnings as before) when their collaborator is absent.
    /// </summary>
    public GeoservicesLayerPublicationService(
        ILogger<GeoservicesLayerPublicationService> logger,
        ILayerPublishingService? layerPublishingService = null,
        IGeoServicesStyleConverter? styleConverter = null,
        ILayerStyleCatalog? styleCatalog = null,
        ILayerReconciliationService? reconciliationService = null,
        IMetadataV2GraphStore? metadataGraphStore = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _layerPublishingService = layerPublishingService;
        _styleConverter = styleConverter;
        _styleCatalog = styleCatalog;
        _reconciliationService = reconciliationService;
        // The catalog-reconciliation read-back (issue #1379) needs the genuinely-persisted current
        // graph (where AutoPublish materialized res-layer-{layerId}), never the V1-catalog compat
        // synthesis. The Postgres store implements this seam; a non-implementing test double leaves
        // the reader null and catalog reconciliation is skipped.
        _metadataWriteBaseReader = metadataGraphStore as IMetadataV2GraphWriteBaseReader;
    }

    /// <summary>
    /// True when a reconciliation service is registered, so the importer emits the "Validating" progress
    /// phase and runs the post-publish gate. Mirrors the previous inline <c>_reconciliationService is not
    /// null</c> guard so the ordered progress phases are unchanged.
    /// </summary>
    public bool ReconciliationEnabled => _reconciliationService is not null;

    /// <summary>
    /// Publishes the freshly imported table as a Honua layer (AutoPublish) and attaches its converted
    /// MapLibre style. Returns the published layer summary, or <c>null</c> with an operator warning when
    /// publishing is unavailable, no target service name was supplied, or publishing did not complete.
    /// </summary>
    public async Task<PublishedLayerSummary?> TryPublishImportedLayerAsync(
        GeoservicesImportRequest request,
        string targetSchema,
        GeoservicesLayerInfo layerInfo,
        List<string> warnings,
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        int featuresProcessed,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (_layerPublishingService == null)
        {
            warnings.Add("AutoPublish was requested, but no layer publishing service is registered for this server.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ServiceName))
        {
            warnings.Add("AutoPublish was requested, but no target serviceName was supplied; the imported table was not published.");
            return null;
        }

        try
        {
            GeoservicesImportService.ReportProgress(
                progress,
                jobId,
                startedAt,
                GeoservicesImportStatus.Publishing,
                request,
                "Publishing imported layer",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name);

            var publishRequest = new LayerPublishRequest
            {
                Schema = targetSchema,
                Table = request.TableName,
                LayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
                Description = layerInfo.Description,
                GeometryColumn = "geom",
                GeometryType = string.IsNullOrWhiteSpace(layerInfo.GeometryType)
                    ? null
                    : GeoservicesImportService.MapEsriGeometryType(layerInfo.GeometryType),
                Srid = request.TargetSrid,
                PrimaryKey = FieldNames.ObjectId,
                Fields = [],
                ServiceName = request.ServiceName,
                Enabled = true,
                FieldDomains = BuildFieldDomainMap(layerInfo),
                // Carry the captured Esri subtype set through publish. The subtype field
                // is the canonical 'type' column on the imported layer; the publish path
                // only attaches the subtypes when that column is actually published, so a
                // subtype set referencing a dropped column never false-fails reconciliation.
                Subtypes = layerInfo.Subtypes,
                // Carry the captured Esri attribute rules through publish so calculation /
                // constraint / validation rules fire on applyEdits. Calculation rules whose
                // target column is not published are pruned in the projection so a rule
                // referencing a dropped field never fails graph validation.
                AttributeRules = layerInfo.AttributeRules
            };

            var published = await _layerPublishingService.PublishLayerAsync(
                    connectionString,
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            await TryAttachLayerStyleAsync(
                    published,
                    layerInfo,
                    request,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);

            return published;
        }
        catch (LayerPublishingException)
        {
            warnings.Add("AutoPublish was requested, but publishing did not complete.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoPublishFailed(_logger, request.TableName, request.ServiceName!, ex);
            warnings.Add("AutoPublish was requested, but publishing did not complete.");
            return null;
        }
    }

    // Projects the captured per-field Esri domains onto the publish request, keyed
    // by source field name. Coded-value domains over the capture cap are not present
    // here (the source parser omitted them and the inventory raised the truncation
    // warning), so they intentionally publish without a domain rather than as a
    // misleading partial lookup.
    private static Dictionary<string, MetadataV2FieldDomain> BuildFieldDomainMap(
        GeoservicesLayerInfo layerInfo)
    {
        var map = new Dictionary<string, MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layerInfo.Fields.Where(field => field.Domain is not null && !string.IsNullOrWhiteSpace(field.Name)))
        {
            map[field.Name] = field.Domain!;
        }

        return map;
    }

    private async Task TryAttachLayerStyleAsync(
        PublishedLayerSummary published,
        GeoservicesLayerInfo layerInfo,
        GeoservicesImportRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_styleConverter == null || _styleCatalog == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(layerInfo.DrawingInfoJson))
        {
            return;
        }

        JsonElement drawingInfoElement;
        try
        {
            using var drawingInfoDocument = JsonDocument.Parse(layerInfo.DrawingInfoJson);
            drawingInfoElement = drawingInfoDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            warnings.Add(
                "Source service returned a malformed 'drawingInfo' payload; the published layer was left unstyled.");
            return;
        }

        try
        {
            var geometryType = MapEsriGeometryTypeToMetadataV2(layerInfo.GeometryType);
            var conversion = _styleConverter.Convert(
                drawingInfoElement,
                published.LayerId,
                published.LayerName,
                geometryType);

            await _styleCatalog
                .SetStyleAsync(
                    published.LayerId,
                    conversion.MapLibreStyleJson,
                    layerInfo.DrawingInfoJson!,
                    revisedBy: GeoservicesStyleRevisedBy,
                    changeSummary: $"Style attached during Geoservices auto-publish from {request.ServiceUrl}",
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var unsupported in conversion.Unsupported)
            {
                Log.AutoPublishStyleUnsupportedSymbolizer(
                    _logger,
                    published.LayerId,
                    unsupported.Code,
                    unsupported.SymbolizerType);
                warnings.Add(BuildUnsupportedSymbolizerWarning(unsupported));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoPublishStyleAttachFailed(_logger, published.LayerId, ex);
            warnings.Add(
                "AutoPublish succeeded, but attaching the converted MapLibre style to the published layer failed; drawingInfo was not persisted.");
        }
    }

    private const string GeoservicesStyleRevisedBy = "geoservices-import";

    private static string BuildUnsupportedSymbolizerWarning(UnsupportedSymbolizerInfo unsupported)
    {
        if (string.IsNullOrWhiteSpace(unsupported.SymbolizerType))
        {
            return $"Imported renderer carried unsupported input ({unsupported.Code}): {unsupported.Guidance}";
        }

        return $"Imported renderer carried unsupported input ({unsupported.Code}, '{unsupported.SymbolizerType}'): {unsupported.Guidance}";
    }

    private static MetadataV2GeometryType MapEsriGeometryTypeToMetadataV2(string? esriGeometryType)
    {
        if (string.IsNullOrWhiteSpace(esriGeometryType))
        {
            return MetadataV2GeometryType.None;
        }

        return esriGeometryType.Trim().ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => MetadataV2GeometryType.Point,
            "ESRIGEOMETRYMULTIPOINT" => MetadataV2GeometryType.MultiPoint,
            "ESRIGEOMETRYPOLYLINE" => MetadataV2GeometryType.MultiLineString,
            "ESRIGEOMETRYLINE" => MetadataV2GeometryType.LineString,
            "ESRIGEOMETRYPOLYGON" => MetadataV2GeometryType.MultiPolygon,
            "ESRIGEOMETRYENVELOPE" => MetadataV2GeometryType.Polygon,
            _ => MetadataV2GeometryType.None
        };
    }

    private static partial class Log
    {
        [LoggerMessage(7832, LogLevel.Warning, "Auto-publish failed for imported table {TableName} into service {ServiceName}")]
        public static partial void AutoPublishFailed(ILogger logger, string tableName, string serviceName, Exception exception);

        [LoggerMessage(7833, LogLevel.Warning,
            "Auto-publish style attach failed for layer {LayerId}; drawingInfo and MapLibre style were not persisted")]
        public static partial void AutoPublishStyleAttachFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(7834, LogLevel.Information,
            "Geoservices auto-publish flagged unsupported style input {Code} ('{SymbolizerType}') on layer {LayerId}")]
        public static partial void AutoPublishStyleUnsupportedSymbolizer(
            ILogger logger,
            int layerId,
            string code,
            string symbolizerType);

        [LoggerMessage(7841, LogLevel.Warning,
            "Reconciliation gate could not run for table {TableName}; import completed without a reconciliation verdict")]
        public static partial void ReconciliationGateUnavailable(ILogger logger, string tableName, Exception exception);
    }
}
