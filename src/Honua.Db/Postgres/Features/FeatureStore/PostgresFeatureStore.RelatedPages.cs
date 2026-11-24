// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal sealed partial class PostgresFeatureStoreRefactored
{
    public bool SupportsRelatedPages => _dataAccess is FeatureDataAccess;

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
            layerId,
            relatedLayerId,
            query.Relationship.OriginForeignKeyField,
            query.Relationship.DestinationForeignKeyField,
            cancellationToken).ConfigureAwait(false);
        return await postgres.QueryRelatedPageAsync(new RelatedPageReadRequest(layerId, query, security), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RelatedPageReadSecurity> ResolveRelatedReadSecurityAsync(
        int originLayerId,
        int? relatedLayerId,
        string? originForeignKeyField,
        string? destinationForeignKeyField,
        CancellationToken cancellationToken)
    {
        var originMasks = await ResolveMaskedFieldsAsync(originLayerId, cancellationToken).ConfigureAwait(false);
        var relatedMasks = relatedLayerId is int targetLayerId
            ? await ResolveMaskedFieldsAsync(targetLayerId, cancellationToken).ConfigureAwait(false)
            : ImmutableArray<string>.Empty;
        if (!string.IsNullOrWhiteSpace(originForeignKeyField))
        {
            RejectMaskedRelationshipKey(originMasks, originForeignKeyField);
        }

        if (!string.IsNullOrWhiteSpace(destinationForeignKeyField))
        {
            RejectMaskedRelationshipKey(relatedMasks, destinationForeignKeyField);
        }

        var originFilter = await ResolveEnforcedSqlFilterAsync(originLayerId, cancellationToken).ConfigureAwait(false);
        var relatedFilter = relatedLayerId is int filterLayerId
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
