// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
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
                CatalogReport = null,
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
}
