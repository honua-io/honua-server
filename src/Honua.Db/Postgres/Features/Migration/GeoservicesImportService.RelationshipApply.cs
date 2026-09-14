// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private const string ArcGisSourceKind = "arcgis-geoservices-rest";

    /// <summary>
    /// Single-layer import has not run the separate relationship-apply stage. Retain every
    /// source declaration as an explicit omission, including malformed/null declarations,
    /// instead of silently reporting that a row transfer migrated its dependencies.
    /// </summary>
    internal static MigrationRelationshipApplyOutcome[] DescribeUnappliedSourceRelationships(
        GeoservicesLayerInfo layerInfo, int? publishedLayerId)
        => layerInfo.Relationships.Select((relationship, index) =>
        {
            var sourceId = relationship?.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? $"unknown-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var sourceReference = $"layer:{layerInfo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/relationship:{sourceId}";
            return new MigrationRelationshipApplyOutcome
            {
                SourceRelationshipId = sourceReference,
                TargetRelationshipRef = publishedLayerId.HasValue
                    ? $"rel-{publishedLayerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{sourceId}"
                    : sourceReference,
                Outcome = MigrationCatalogWriteOutcome.AlreadyExists,
                Deferred = true,
                Message = "Single-layer import does not recreate source relationships. Import the related resource "
                    + "and apply a reviewed relationship manifest with the published layer IDs and key fields. "
                    + (relationship?.Composite == true
                        ? "The source declares composite ownership; its target behavior also requires review."
                        : "Verify related-record readback before cutover.")
            };
        }).ToArray();

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
                    // #4600: a composite / many-to-many / unsupported relationship used to drop out
                    // of the apply loop here without leaving any trace, so the run reported success
                    // while the target was missing a construct the source had. Emit an explicit
                    // deferred outcome instead so the fidelity gate blocks on it.
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        $"Relationship is classified '{relationship.Classification}' and is not recreated by automated "
                        + "apply; the junction/composite behavior must be modelled on the target before cutover.",
                        publishedLayerMap.TryGetValue(target.SourceResourceId, out var ineligibleOriginLayerId)
                            ? ineligibleOriginLayerId
                            : null,
                        relationship));
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
                if (!TryResolveRelatedSource(target.SourceResourceId, relationship, sourceTargets, out var relatedSourceResourceId))
                {
                    skipped.Add(BuildSkippedOutcome(
                        relationship.SourceRelationshipId,
                        "Related layer cannot be uniquely resolved within the origin service's manifest target resources; relationship persistence deferred until the related layer is imported and identified.",
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
        string originSourceResourceId,
        MigrationManifestRelationshipRecord relationship,
        Dictionary<string, MigrationManifestTargetResource> sourceTargets,
        out string relatedSourceResourceId)
    {
        relatedSourceResourceId = string.Empty;
        if (!TryGetSourceServiceScope(originSourceResourceId, out var serviceScope))
        {
            return false;
        }

        // ArcGIS layer/table numbers are service-local. A relatedTableId is
        // recorded as layer:{id} even when the inventory resource is a table.
        // Resolve both kinds within the exact originating service, and reject
        // ambiguous manifests rather than choosing by resource enumeration order.
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var related in relationship.RelatedLayerIds)
        {
            if (string.IsNullOrWhiteSpace(related))
            {
                continue;
            }

            var local = related.Trim();
            if (TryGetSourceServiceScope(local, out var explicitScope))
            {
                if (string.Equals(explicitScope, serviceScope, StringComparison.Ordinal) && sourceTargets.ContainsKey(local))
                {
                    matches.Add(local);
                }

                continue;
            }

            var separator = local.IndexOf(':');
            if (separator < 0 ||
                (local[..separator] != "layer" && local[..separator] != "table"))
            {
                continue;
            }

            var id = local[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(id) || id.Contains(':'))
            {
                continue;
            }

            foreach (var kind in new[] { "layer", "table" })
            {
                var candidate = $"{serviceScope}{kind}:{id}";
                if (sourceTargets.ContainsKey(candidate))
                {
                    matches.Add(candidate);
                }
            }
        }

        if (matches.Count != 1)
        {
            return false;
        }

        relatedSourceResourceId = matches.Single();
        return true;
    }

    private static bool TryGetSourceServiceScope(string resourceId, out string serviceScope)
    {
        serviceScope = string.Empty;
        const string prefix = "resource:";
        if (!resourceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var idSeparator = resourceId.LastIndexOf(':');
        if (idSeparator <= prefix.Length || idSeparator == resourceId.Length - 1)
        {
            return false;
        }

        var kindSeparator = resourceId.LastIndexOf(':', idSeparator - 1);
        if (kindSeparator <= prefix.Length)
        {
            return false;
        }

        var kind = resourceId[(kindSeparator + 1)..idSeparator];
        if (kind != "layer" && kind != "table")
        {
            return false;
        }

        serviceScope = resourceId[..(kindSeparator + 1)];
        return true;
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
            TargetRelationshipRef = refValue,
            // #4600: mark the omission explicitly. AlreadyExists is also what a successful
            // idempotent re-apply reports, so without this flag the fidelity gate cannot tell a
            // persisted relationship from a dropped one.
            Deferred = true
        };
    }
}
