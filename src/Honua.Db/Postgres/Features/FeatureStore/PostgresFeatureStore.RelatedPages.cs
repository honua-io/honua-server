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
        var originMasks = await ResolveMaskedFieldsAsync(layerId, cancellationToken).ConfigureAwait(false);
        var relatedMasks = await ResolveMaskedFieldsAsync(relatedLayerId, cancellationToken).ConfigureAwait(false);
        RejectMaskedRelationshipKey(originMasks, query.Relationship.OriginForeignKeyField!);
        RejectMaskedRelationshipKey(relatedMasks, query.Relationship.DestinationForeignKeyField!);
        var security = new RelatedPageReadSecurity(
            await ResolveEnforcedSqlFilterAsync(layerId, cancellationToken).ConfigureAwait(false),
            await ResolveEnforcedSqlFilterAsync(relatedLayerId, cancellationToken).ConfigureAwait(false),
            relatedMasks);
        return await postgres.QueryRelatedPageAsync(new RelatedPageReadRequest(layerId, query, security), cancellationToken)
            .ConfigureAwait(false);
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
