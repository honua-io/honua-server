// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private enum FeatureSelectProjection
    {
        Default,
        Json,
        ManagedSnapshot
    }

    private string BuildReadStateProjection(FeatureSelectProjection projection)
        => projection == FeatureSelectProjection.ManagedSnapshot
            ? $", ST_AsBinary(\"geometry\"::geometry) AS __honua_snapshot_geometry, {ValidateAndQuoteIdentifier(_mapping.AttributesColumn!)} AS __honua_snapshot_attributes"
            : string.Empty;

    private async Task<Feature?> ReadManagedSnapshotAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        var sql = BuildFeatureSelectCore(query, probeLimit: false, comparison: null,
            projection: FeatureSelectProjection.ManagedSnapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The displayed schema projection can add nulls or hide stored fields. Only
        // the original row from this same authorized snapshot can match the writer.
        var visible = ReadFeature(reader, textAttributes: false, nativeAttributes: null);
        var attributes = _dictionaryPool.Get();
        try
        {
            if (!reader.IsDBNull(4))
            {
                FeatureAttributeJsonReader.ReadInto(reader.GetString(4), attributes);
            }
            attributes[FieldNames.ObjectId] = visible.Id;
            var raw = Feature.Create(visible.Id,
                reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3), attributes.ToImmutableDictionary());
            return visible with { ReadStateToken = FeatureStateToken.Compute(raw) };
        }
        finally
        {
            attributes.Clear();
            _dictionaryPool.Return(attributes);
        }
    }
}
