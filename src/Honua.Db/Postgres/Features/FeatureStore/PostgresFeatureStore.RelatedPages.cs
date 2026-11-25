// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal sealed partial class PostgresFeatureStoreRefactored
{
    public bool SupportsRelatedPages => _dataAccess is FeatureDataAccess;

    private sealed class RelatedReadSecurityRequest
    {
        public required int OriginLayerId { get; init; }
        public int? RelatedLayerId { get; init; }
        public string? OriginForeignKeyField { get; init; }
        public string? DestinationForeignKeyField { get; init; }
    }

    public async Task<RelatedFeaturePage> QueryRelatedPageAsync(
        int layerId, RelatedPageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate();
        if (_dataAccess is not FeatureDataAccess postgres)
        {
            throw new NotSupportedException("The provider does not support bounded relationship pages.");
        }
        var relatedLayerId = query.Relationship.RelatedLayerId!.Value;
        var security = await ResolveRelatedReadSecurityAsync(
            new RelatedReadSecurityRequest
            {
                OriginLayerId = layerId,
                RelatedLayerId = relatedLayerId,
                OriginForeignKeyField = query.Relationship.OriginForeignKeyField,
                DestinationForeignKeyField = query.Relationship.DestinationForeignKeyField
            },
            cancellationToken).ConfigureAwait(false);
        return await postgres.QueryRelatedPageAsync(new RelatedPageReadRequest(layerId, query, security), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RelatedPageReadSecurity> ResolveRelatedReadSecurityAsync(
        RelatedReadSecurityRequest request,
        CancellationToken cancellationToken)
    {
        var originMasks = await ResolveMaskedFieldsAsync(request.OriginLayerId, cancellationToken).ConfigureAwait(false);
        var relatedMasks = request.RelatedLayerId is int targetLayerId
            ? await ResolveMaskedFieldsAsync(targetLayerId, cancellationToken).ConfigureAwait(false)
            : ImmutableArray<string>.Empty;
        if (!string.IsNullOrWhiteSpace(request.OriginForeignKeyField))
        {
            RejectMaskedRelationshipKey(originMasks, request.OriginForeignKeyField);
        }

        if (!string.IsNullOrWhiteSpace(request.DestinationForeignKeyField))
        {
            RejectMaskedRelationshipKey(relatedMasks, request.DestinationForeignKeyField);
        }

        var originFilter = await ResolveEnforcedSqlFilterAsync(request.OriginLayerId, cancellationToken).ConfigureAwait(false);
        var relatedFilter = request.RelatedLayerId is int filterLayerId
            ? await ResolveEnforcedSqlFilterAsync(filterLayerId, cancellationToken).ConfigureAwait(false)
            : null;
        return new RelatedPageReadSecurity(originFilter, relatedFilter, relatedMasks);
    }

    private static string NormalizeRelationshipKey(string field) =>
        field.Equals("object_id", StringComparison.OrdinalIgnoreCase) ? "objectid" : field;

    private static void RejectMaskedRelationshipKey(ImmutableArray<string> masks, string field)
    {
        if (!masks.IsDefaultOrEmpty && masks.Any(mask => NormalizeRelationshipKey(mask).Equals(NormalizeRelationshipKey(field), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A masked field cannot be used as a relationship key.");
        }
    }
}
