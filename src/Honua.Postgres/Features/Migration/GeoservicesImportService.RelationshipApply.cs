// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private const string ArcGisSourceKind = "arcgis-geoservices-rest";

    /// <inheritdoc />
    public async Task<MigrationRelationshipApplyOutcome[]> ApplyRelationshipsAsync(
        MigrationManifestArtifact manifest,
        IReadOnlyDictionary<string, int> publishedLayerMap,
        IMetadataV2GraphStore? graphStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publishedLayerMap);

        if (!string.Equals(manifest.SourceKind, ArcGisSourceKind, StringComparison.Ordinal))
        {
            // Other source kinds (geoserver-rest, ogc-wfs, etc.) do not currently
            // emit per-relationship records into the manifest. Returning an empty
            // outcome set is intentional: callers can treat it as "nothing to apply".
            return [];
        }

        if (_catalogWriter == null)
        {
            // Catalog writer is not wired up (e.g. in a discovery-only deployment).
            // Skip rather than failing so the import path remains usable for scans.
            Log.RelationshipApplySkipped(_logger, "catalog-writer-unavailable");
            return [];
        }

        var sourceTargets = manifest.TargetResources
            .ToDictionary(static r => r.SourceResourceId, StringComparer.Ordinal);

        var requests = new List<MigrationRelationshipApplyRequest>();
        var skipped = new List<MigrationRelationshipApplyOutcome>();

        foreach (var target in manifest.TargetResources.OrderBy(static r => r.SourceResourceId, StringComparer.Ordinal))
        {
            foreach (var relationship in target.Relationships)
            {
                if (!IsApplyEligible(relationship.Classification))
                {
                    continue;
                }

                // Origin: the current target resource.
                if (!publishedLayerMap.TryGetValue(target.SourceResourceId, out var originLayerId))
                {
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        $"Origin layer for resource '{target.SourceResourceId}' is not in the published layer map; relationship persistence deferred until the layer is imported.",
                        originLayerId: null,
                        relationship));
                    continue;
                }

                // Related: must be resolved through the manifest's source layer ids.
                if (!TryResolveRelatedSource(relationship, sourceTargets, out var relatedSourceResourceId))
                {
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        "Related layer is not present in the manifest target resources; relationship persistence deferred until the related layer is imported.",
                        originLayerId,
                        relationship));
                    continue;
                }

                if (!publishedLayerMap.TryGetValue(relatedSourceResourceId, out var relatedLayerId))
                {
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        $"Related layer '{relatedSourceResourceId}' is not in the published layer map; relationship persistence deferred until the related layer is imported.",
                        originLayerId,
                        relationship));
                    continue;
                }

                var originKeyField = relationship.OriginKeyField;
                var destinationKeyField = relationship.DestinationKeyField;
                if (string.IsNullOrWhiteSpace(originKeyField) || string.IsNullOrWhiteSpace(destinationKeyField))
                {
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        "Source did not advertise origin/destination key fields for the relationship; operator must populate the foreign-key fields before automated migration.",
                        originLayerId,
                        relationship));
                    continue;
                }

                requests.Add(new MigrationRelationshipApplyRequest
                {
                    SourceRelationshipId = relationship.SourceRelationshipId,
                    EsriRelationshipId = relationship.EsriRelationshipId,
                    Name = relationship.Name,
                    OriginLayerId = originLayerId,
                    RelatedLayerId = relatedLayerId,
                    OriginKeyField = originKeyField!,
                    DestinationKeyField = destinationKeyField!,
                    Role = string.IsNullOrWhiteSpace(relationship.Role) ? "origin" : relationship.Role!,
                    Cardinality = relationship.Cardinality ?? "1:N"
                });
            }
        }

        if (requests.Count == 0)
        {
            return skipped.ToArray();
        }

        var connectionString = _connectionProvider.GetConnectionString();
        var applied = await _catalogWriter.EnsureRelationshipsAsync(
                connectionString,
                graphStore,
                requests.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        return applied
            .Concat(skipped)
            .OrderBy(static o => o.SourceRelationshipId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsApplyEligible(string classification)
        => string.Equals(classification, MigrationManifestRelationshipClassifications.Automated, StringComparison.Ordinal) ||
           string.Equals(classification, MigrationManifestRelationshipClassifications.Assisted, StringComparison.Ordinal);

    private static bool TryResolveRelatedSource(
        MigrationManifestRelationshipRecord relationship,
        Dictionary<string, MigrationManifestTargetResource> sourceTargets,
        out string relatedSourceResourceId)
    {
        // The fidelity record carries the related layer's local identifier as
        // "layer:{id}"; the manifest target resource ids follow the inventory
        // convention "resource:{service}:{kind}:{id}". Resolve by suffix match
        // against the manifest's target resources so unrelated services in the
        // same graph snapshot are ignored.
        foreach (var related in relationship.RelatedLayerIds)
        {
            if (string.IsNullOrWhiteSpace(related))
            {
                continue;
            }

            var local = related.Trim();
            foreach (var (sourceResourceId, _) in sourceTargets)
            {
                if (sourceResourceId.EndsWith(":" + local, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sourceResourceId, local, StringComparison.OrdinalIgnoreCase))
                {
                    relatedSourceResourceId = sourceResourceId;
                    return true;
                }
            }
        }

        relatedSourceResourceId = string.Empty;
        return false;
    }

    private static MigrationRelationshipApplyOutcome BuildSkippedOutcome(
        string sourceRelationshipId,
        string message,
        int? originLayerId,
        MigrationManifestRelationshipRecord relationship)
    {
        // A skipped relationship still needs a TargetRelationshipRef so the
        // parity probe can report missing origin-side persistence as a manual
        // review record rather than a zero-coverage hole.
        var refValue = originLayerId.HasValue
            ? $"rel-{originLayerId.Value}-{(relationship.EsriRelationshipId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "deferred")}"
            : relationship.TargetRelationshipRef ?? sourceRelationshipId;
        return new MigrationRelationshipApplyOutcome
        {
            SourceRelationshipId = sourceRelationshipId,
            Outcome = MigrationCatalogWriteOutcome.AlreadyExists,
            Message = message,
            TargetRelationshipRef = refValue
        };
    }
}
