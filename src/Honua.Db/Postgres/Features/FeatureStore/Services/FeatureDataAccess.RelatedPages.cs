// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed record RelatedPageReadSecurity(
    SqlFragment? OriginFilter, SqlFragment? RelatedFilter, ImmutableArray<string> RelatedMasks);

internal sealed record RelatedPageReadRequest(int LayerId, RelatedPageQuery Query, RelatedPageReadSecurity Security);

internal sealed partial class FeatureDataAccess
{
    public async Task<RelatedFeaturePage> QueryRelatedPageAsync(RelatedPageReadRequest request, CancellationToken cancellationToken)
    {
        request.Query.Validate();
        ValidateRelatedPageFields(request);
        var geometryType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometry = _geometryProcessor.GetGeometrySelectExpression(geometryType, new FeatureQuery { OutputSrid = request.Query.OutputSrid });
        var (sql, parameters) = RelatedPageSqlBuilder.Build(request, _tableName, geometry);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSafeCommand(connection, sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var positions = ImmutableArray.CreateBuilder<RelatedFeaturePosition>();
        var attributesReader = new FeatureAttributeReadContext();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (positions.Count == request.Query.PositionLimit)
            {
                return new RelatedFeaturePage(positions.ToImmutable()) { HasMore = true };
            }
            Feature? feature = reader.IsDBNull(0) ? null : ProjectRelatedFeature(ReadFeature(reader, attributesReader), request);
            positions.Add(new RelatedFeaturePosition(reader.GetInt64(3), feature));
        }
        return new RelatedFeaturePage(positions.ToImmutable());
    }

    private static void ValidateRelatedPageFields(RelatedPageReadRequest request)
    {
        var relationship = request.Query.Relationship;
        if (!FeatureQueryBuilder.IsValidFieldName(relationship.OriginForeignKeyField!) ||
            !FeatureQueryBuilder.IsValidFieldName(relationship.DestinationForeignKeyField!))
        {
            throw new ArgumentException("Invalid relationship key field.");
        }
        FeatureQuerySecurity.Validate(new FeatureQuery
        {
            Where = relationship.Where,
            SqlFilter = relationship.SqlFilter,
            OutFields = relationship.OutFields,
            EnforcedMaskedFields = request.Security.RelatedMasks
        });
    }

    private static Feature ProjectRelatedFeature(Feature feature, RelatedPageReadRequest request)
    {
        var fields = request.Query.Relationship.OutFields;
        if (fields is { IsDefaultOrEmpty: false } selected && !selected.Contains("*"))
        {
            feature = FilterFeatureFields(feature, selected);
        }
        var masked = request.Security.RelatedMasks;
        if (!masked.IsDefaultOrEmpty)
        {
            var visible = feature.Attributes.Where(pair => !masked.Contains(pair.Key, StringComparer.OrdinalIgnoreCase));
            feature = feature with { Attributes = visible.ToImmutableDictionary() };
        }
        return feature;
    }
}
