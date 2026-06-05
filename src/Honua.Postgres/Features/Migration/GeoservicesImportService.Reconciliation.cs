// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    /// <summary>
    /// Outcome of the post-publish reconciliation gate (issues #1247, #1380). Carries the
    /// data-reconciliation artifact, the optional catalog-parity report, and the aggregated verdict
    /// the import pipeline uses to decide between <see cref="GeoservicesImportStatus.Completed"/> and
    /// <see cref="GeoservicesImportStatus.NeedsReview"/>.
    /// </summary>
    private readonly record struct ReconciliationGateOutcome
    {
        /// <summary>A gate outcome for runs where reconciliation did not run (no publish or no service registered).</summary>
        public static ReconciliationGateOutcome Skipped => default;

        /// <summary>Per-layer data-reconciliation artifact, when reconciliation ran.</summary>
        public MigrationReconciliationArtifact? Artifact { get; init; }

        /// <summary>Catalog parity report, when one was produced. Always <c>null</c> on the per-layer geoservices path today.</summary>
        public MigrationCatalogReconciliationReport? CatalogReport { get; init; }

        /// <summary>True when a hard finding routes the run to operator review.</summary>
        public bool NeedsReview { get; init; }

        /// <summary>Operator-visible reason the run was routed to review. <c>null</c> when the gate passed.</summary>
        public string? ReviewReason { get; init; }
    }

    /// <summary>
    /// Runs the post-publish reconciliation gate for a single imported + published layer. Builds the
    /// reconciliation request from the apply-time source snapshot (<paramref name="layerInfo"/>) so
    /// the gate is deterministic against the apply moment, runs the
    /// <see cref="ILayerReconciliationService"/>, and aggregates the verdict.
    /// </summary>
    private async Task<ReconciliationGateOutcome> RunReconciliationGateAsync(
        GeoservicesImportRequest request,
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer,
        CancellationToken cancellationToken)
    {
        if (_reconciliationService is null)
        {
            return ReconciliationGateOutcome.Skipped;
        }

        try
        {
            var reconciliationRequest = BuildReconciliationRequest(request, jobId, layerInfo, publishedLayer);
            var artifact = await _reconciliationService
                .ReconcileAsync(reconciliationRequest, cancellationToken)
                .ConfigureAwait(false);

            // Catalog reconciliation (issue #1379): reconcile the published Metadata v2 catalog entry
            // against the apply-time source layer definition. This is the deeper "counts can match
            // while the schema is wrong" pass (schema/domain/identifier/relationship/attachment/
            // subtype). It is recorded on the artifact and surfaced separately; it does NOT change
            // the #1380 gate verdict, which stays driven by the data-movement artifact below.
            var catalogReport = await RunCatalogReconciliationAsync(
                jobId,
                layerInfo,
                publishedLayer,
                cancellationToken).ConfigureAwait(false);

            // Bind the catalog report to the artifact so both travel together through persistence,
            // the evidence pack, and the admin surfaces (#1379 AC).
            if (catalogReport is not null)
            {
                artifact = artifact with { CatalogReconciliation = catalogReport };
            }

            // Parity gate (issue #1380): a hard (fail) finding blocks Completed and routes the run to
            // NeedsReview. Warn and skipped classifications are recorded on the artifact but do not
            // block — they surface to operators without halting the import.
            var needsReview = string.Equals(
                artifact.Classification,
                MigrationReconciliationClassifications.Fail,
                StringComparison.Ordinal);

            return new ReconciliationGateOutcome
            {
                Artifact = artifact,
                CatalogReport = catalogReport,
                NeedsReview = needsReview,
                ReviewReason = needsReview ? BuildReviewReason(artifact) : null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The data was already committed and published. If reconciliation itself errors out we do
            // not fail the import — we record the gate as unavailable and let the run complete, since
            // a reconciliation-infrastructure failure is not evidence of a faithless import. Operators
            // can re-run reconciliation out of band.
            Log.ReconciliationGateUnavailable(_logger, request.TableName, ex);
            return ReconciliationGateOutcome.Skipped;
        }
    }

    /// <summary>
    /// Builds the per-run reconciliation request from the apply-time source snapshot. The source
    /// facts (feature count, extent, field names, where-clause mirror) are read from
    /// <paramref name="layerInfo"/> and the import request so the gate does not re-issue source HTTP
    /// calls and stays deterministic against the apply moment.
    /// </summary>
    private static LayerReconciliationRequest BuildReconciliationRequest(
        GeoservicesImportRequest request,
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer)
    {
        var sourceFieldNames = layerInfo.Fields
            .Where(static field => !field.IsObjectId && !IsGeometryField(field))
            .Select(static field => field.Name.SanitizeFieldName())
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
            TargetHonuaLayerId = publishedLayer.LayerId,
            SourceFeatureCount = layerInfo.FeatureCount,
            SourceExtent = sourceExtent,
            SourceFieldNames = sourceFieldNames,
            // The where-clause supplied to the import is mirrored onto the reconciliation count
            // probe so a partial import (subset of source features) is reconciled apples-to-apples.
            FilterMirror = string.IsNullOrWhiteSpace(request.WhereClause) ? null : request.WhereClause
        };

        return new LayerReconciliationRequest
        {
            RunId = jobId,
            SourceKind = "arcgis-geoservices-rest",
            Layers = [layerInput]
        };
    }

    private static string BuildReviewReason(MigrationReconciliationArtifact artifact)
    {
        if (artifact.Reasons.Length > 0)
        {
            return $"Post-publish reconciliation reported {artifact.Summary.FailCount} blocking finding(s): {string.Join(" ", artifact.Reasons)}";
        }

        return $"Post-publish reconciliation reported {artifact.Summary.FailCount} blocking finding(s).";
    }

    /// <summary>
    /// Catalog reconciliation pass for issue #1379. Reads the published Metadata v2 catalog entry
    /// that the AutoPublish step materialized for <paramref name="publishedLayer"/> back out of the
    /// persisted graph, builds a source inventory resource from the apply-time
    /// <paramref name="layerInfo"/> snapshot, and runs the deterministic
    /// <see cref="MigrationCatalogReconciler"/> over the pair. Returns <c>null</c> (skipping the
    /// pass without failing the import) when no read-back seam is available or the published entry
    /// cannot be located — a reconciliation-infrastructure gap is not evidence of a faithless import.
    /// </summary>
    private async Task<MigrationCatalogReconciliationReport?> RunCatalogReconciliationAsync(
        string jobId,
        GeoservicesLayerInfo layerInfo,
        PublishedLayerSummary publishedLayer,
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
        var resourceId = $"res-layer-{publishedLayer.LayerId.ToString(CultureInfo.InvariantCulture)}";
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
            "arcgis-geoservices-rest",
            [
                new MigrationCatalogReconciliationInput
                {
                    Resource = inventoryResource,
                    PublishedResource = published,
                    SourceBbox = sourceBbox,
                    TargetResourceId = published.Metadata.Id,
                    // The per-layer geoservices import path does not currently translate relationship
                    // classes here (relationship apply is a separate flow); pass none so relationship
                    // findings are not asserted against this single-layer publish.
                    ExpectedRelationships = [],
                    ExpectedSubtypes = layerInfo.Subtypes
                }
            ]);
    }

    /// <summary>
    /// Projects the apply-time <see cref="GeoservicesLayerInfo"/> snapshot into the
    /// <see cref="MigrationInventoryResource"/> shape the catalog reconciler consumes. Only the facts
    /// the reconciler probes are mapped (fields + types + captured domains, geometry type, declared
    /// SRID, attachment advertisement). Coded-value domains that exceeded the capture cap surface
    /// with no captured values so the reconciler's cap guard does not false-fail them.
    /// </summary>
    private static MigrationInventoryResource BuildInventoryResourceFromLayerInfo(GeoservicesLayerInfo layerInfo)
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

        var fields = layerInfo.Fields
            .Select(BuildInventoryField)
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

    private static MigrationInventoryField BuildInventoryField(GeoservicesFieldInfo field)
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
            Name = field.Name,
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
