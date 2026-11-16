// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.Migration;

internal readonly record struct GeoservicesReconciliationGateOutcome
{
    public static GeoservicesReconciliationGateOutcome Skipped => default;

    public MigrationReconciliationArtifact? Artifact { get; init; }

    public MigrationCatalogReconciliationReport? CatalogReport { get; init; }

    public bool DataCheckExecuted { get; init; }

    public bool CatalogCheckExecuted { get; init; }
}

internal sealed partial class GeoservicesLayerPublicationService
{
    public async Task<GeoservicesReconciliationGateOutcome> RunReconciliationGateAsync(
        GeoservicesImportRequest request,
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer,
        CancellationToken cancellationToken,
        SourceGeometryCensus? sourceGeometry = null,
        BoundingBox? queriedSourceExtent = null)
    {
        if (_reconciliationService is null)
        {
            return GeoservicesReconciliationGateOutcome.Skipped;
        }

        try
        {
            var reconciliationRequest = BuildReconciliationRequest(
                request,
                jobId,
                layerInfo,
                publishedLayer,
                sourceGeometry,
                queriedSourceExtent);
            var artifact = await _reconciliationService
                .ReconcileAsync(reconciliationRequest, cancellationToken)
                .ConfigureAwait(false);

            // Catalog reconciliation (issue #1379): reconcile the published Metadata v2 catalog entry
            // against the apply-time source layer definition. This is the deeper "counts can match
            // while the schema is wrong" pass (schema/domain/identifier/relationship/attachment/
            // subtype). Since #4600 its findings feed the same unified migration verdict as the
            // data-movement artifact rather than being recorded and then ignored.
            var catalogReport = await RunCatalogReconciliationAsync(
                jobId,
                layerInfo,
                publishedLayer,
                request.TargetSrid,
                artifact.Layers.SingleOrDefault(layer =>
                    layer.TargetHonuaLayerId == publishedLayer.StorageLayerId &&
                    layer.SourceLayerId == reconciliationRequest.Layers[0].SourceLayerId)?.Extent.Target,
                cancellationToken).ConfigureAwait(false);

            // Bind the catalog report to the artifact so both travel together through persistence,
            // the evidence pack, and the admin surfaces (#1379 AC).
            if (catalogReport is not null)
            {
                artifact = artifact with { CatalogReconciliation = catalogReport };
            }

            return new GeoservicesReconciliationGateOutcome
            {
                Artifact = artifact,
                CatalogReport = catalogReport,
                DataCheckExecuted = true,
                CatalogCheckExecuted = catalogReport is not null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: the data was already committed and published. If reconciliation
            // itself errors out we do not fail the import — we record the gate as unavailable and let
            // the run complete, since a reconciliation-infrastructure failure is not evidence of a
            // faithless import. Operators can re-run reconciliation out of band.
            Log.ReconciliationGateUnavailable(_logger, request.TableName, ex);
            return GeoservicesReconciliationGateOutcome.Skipped;
        }
    }

    private static LayerReconciliationRequest BuildReconciliationRequest(
        GeoservicesImportRequest request,
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer,
        SourceGeometryCensus? sourceGeometry,
        BoundingBox? queriedSourceExtent)
    {
        var sourceFieldNames = GeoservicesImportColumns.Map(layerInfo).Columns
            .Select(static column => column.ColumnName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        BoundingBox? sourceExtent = layerInfo.Extent is { } extent
            ? BoundingBox.Create(
                extent.Xmin,
                extent.Ymin,
                extent.Xmax,
                extent.Ymax,
                extent.SpatialReferenceWkid ?? layerInfo.SpatialReferenceWkid ?? request.TargetSrid)
            : null;

        var layerInput = new LayerReconciliationLayerInput
        {
            SourceLayerId = $"{request.ServiceUrl}#{request.LayerId}",
            SourceLayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
            TargetHonuaLayerId = publishedLayer.StorageLayerId,
            SourceFeatureCount = layerInfo.FeatureCount,
            SourceHasGeometry = !string.IsNullOrEmpty(layerInfo.GeometryType),
            SourceExtent = sourceExtent,
            PlannedTargetSrid = request.TargetSrid,
            SourceFieldNames = sourceFieldNames,
            // The import writes only the selected source population into its own target table, and
            // the caller supplies the source count under the import filter. The where-clause is kept
            // as provenance; it is not re-run against the target (#4826).
            TargetContainsOnlyImportedFeatures = true,
            FilterMirror = string.IsNullOrWhiteSpace(request.WhereClause) ? null : request.WhereClause,
            FilterFieldMappings = BuildFilterFieldMappings(layerInfo),
            SourceGeometry = sourceGeometry,
            QueriedSourceExtent = queriedSourceExtent
        };

        return new LayerReconciliationRequest
        {
            RunId = jobId,
            SourceKind = MigrationSourceKinds.GeoServicesRest,
            Layers = [layerInput]
        };
    }

    private static Dictionary<string, string>? BuildFilterFieldMappings(GeoservicesLayerInfo layerInfo)
    {
        var map = GeoservicesImportColumns.Map(layerInfo);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in map.Columns)
        {
            if (!string.Equals(column.Field.Name, column.ColumnName, StringComparison.Ordinal))
            {
                mappings[column.Field.Name] = column.ColumnName;
            }
        }

        if (map.Spatial)
        {
            foreach (var field in layerInfo.Fields)
            {
                if (GeoservicesImportService.IsGeometryField(field) &&
                    !string.Equals(field.Name, "geom", StringComparison.Ordinal))
                {
                    mappings[field.Name] = "geom";
                }
            }
        }

        return mappings.Count == 0 ? null : mappings;
    }

    private async Task<MigrationCatalogReconciliationReport?> RunCatalogReconciliationAsync(
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer,
        int plannedTargetSrid,
        ExtentBox? observedTargetExtent,
        CancellationToken cancellationToken)
    {
        if (_metadataWriteBaseReader is null)
        {
            return null;
        }

        var snapshot = await _metadataWriteBaseReader
            .TryGetPersistedCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            // No activated snapshot (fresh DB / serving straight from the V1 catalog). There is no
            // canonical published entry to reconcile against, so skip rather than false-fail.
            return null;
        }

        // AutoPublish materializes the data resource with the stable id res-layer-{layerId}.
        var resourceId = $"res-layer-{publishedLayer.StorageLayerId.ToString(CultureInfo.InvariantCulture)}";
        var published = snapshot.Graph.Resources
            .FirstOrDefault(resource => string.Equals(resource.Metadata.Id, resourceId, StringComparison.Ordinal));
        if (published is null)
        {
            return null;
        }

        var inventoryResource = BuildInventoryResourceFromLayerInfo(layerInfo);
        var sourceBbox = layerInfo.Extent is { } extent
            ? new[] { extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax }
            : null;

        return MigrationCatalogReconciler.BuildReport(
            jobId,
            MigrationSourceKinds.GeoServicesRest,
            [
                new MigrationCatalogReconciliationInput
                {
                    Resource = inventoryResource,
                    PublishedResource = published,
                    SourceBbox = sourceBbox,
                    PlannedTargetSrid = plannedTargetSrid,
                    ObservedTargetExtent = observedTargetExtent,
                    TargetResourceId = published.Metadata.Id,
                    // The per-layer geoservices import path does not currently translate relationship
                    // classes here (relationship apply is a separate flow); pass none so relationship
                    // findings are not asserted against this single-layer publish.
                    ExpectedRelationships = [],
                    ExpectedSubtypes = layerInfo.Subtypes
                }
            ]);
    }

    internal static MigrationInventoryResource BuildInventoryResourceFromLayerInfo(GeoservicesLayerInfo layerInfo)
    {
        var srid = layerInfo.SpatialReferenceWkid
            ?? layerInfo.Extent?.SpatialReferenceWkid;

        var spatialReferences = srid.HasValue
            ? new[]
            {
                new MigrationSpatialReferenceInfo
                {
                    Role = "declared",
                    Srid = srid,
                    CrsUri = $"EPSG:{srid.Value.ToString(CultureInfo.InvariantCulture)}"
                }
            }
            : [];

        // Import creates the geometry column as geom, independently of its source name (Shape,
        // for example). Compare that expected physical field without dropping its type checks.
        // Attribute fields keep their own names, including an ordinary attribute named Shape.
        var hasGeometryColumn = !string.IsNullOrEmpty(layerInfo.GeometryType);
        var fields = layerInfo.Fields
            .Select(field => BuildInventoryField(field,
                hasGeometryColumn && GeoservicesImportService.IsGeometryField(field) ? "geom" : field.Name))
            .ToArray();

        return new MigrationInventoryResource
        {
            Id = $"{layerInfo.Id.ToString(CultureInfo.InvariantCulture)}",
            ContainerId = "geoservices-import",
            Kind = "layer",
            Name = layerInfo.Name,
            GeometryType = layerInfo.GeometryType,
            FeatureCount = layerInfo.FeatureCount,
            HasAttachments = layerInfo.HasAttachments,
            SpatialReferences = spatialReferences,
            Fields = fields,
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Reason = "Imported via the GeoServices apply path."
            }
        };
    }

    private static MigrationInventoryField BuildInventoryField(GeoservicesFieldInfo field, string targetFieldName)
    {
        var domain = field.Domain;
        string? domainType = domain?.Type;
        string? domainName = domain?.Name;
        MigrationInventoryCodedValue[]? domainValues = null;

        if (domain is { } captured &&
            string.Equals(captured.Type, "codedValue", StringComparison.OrdinalIgnoreCase) &&
            captured.CodedValues.Count > 0)
        {
            domainValues = captured.CodedValues
                .Select(static coded => new MigrationInventoryCodedValue
                {
                    Code = CodedValueToString(coded.Code) ?? string.Empty,
                    Name = string.IsNullOrWhiteSpace(coded.Name) ? CodedValueToString(coded.Code) ?? string.Empty : coded.Name
                })
                .Where(static value => value.Code.Length > 0)
                .ToArray();
        }

        return new MigrationInventoryField
        {
            Name = targetFieldName,
            Alias = string.Equals(field.Alias, field.Name, StringComparison.Ordinal) ? null : field.Alias,
            FieldType = field.Type,
            Nullable = field.Nullable,
            DomainType = domainType,
            DomainName = domainName,
            DomainValues = domainValues
        };
    }

    private static string? CodedValueToString(System.Text.Json.JsonElement element)
        => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.GetRawText(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
}
