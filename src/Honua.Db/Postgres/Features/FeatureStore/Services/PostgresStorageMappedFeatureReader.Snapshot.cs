// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private enum FeatureSelectProjection
    {
        Default,
        Json,
        ManagedSnapshot
    }

    private readonly record struct ManagedSnapshotOrdinals
    {
        public required int Geometry { get; init; }

        public required int Attributes { get; init; }
    }

    private string BuildReadStateProjection(FeatureSelectProjection projection)
        => projection == FeatureSelectProjection.ManagedSnapshot
            ? $", ST_AsBinary(\"geometry\"::geometry) AS __honua_snapshot_geometry, {ValidateAndQuoteIdentifier(_mapping.AttributesColumn!)} AS __honua_snapshot_attributes"
            : string.Empty;

    private async Task<Feature?> ReadManagedSnapshotAsync(FeatureQuery query, CancellationToken cancellationToken)
    {
        // Managed partition + objectid is unique. Avoid the page's display-column
        // pruning so the internal snapshot still includes hidden raw columns.
        var sql = BuildFeatureSelectCore(query with { Limit = null }, probeLimit: false, comparison: null,
            projection: FeatureSelectProjection.ManagedSnapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadManagedSnapshotFeature(reader, ResolveSnapshotOrdinals(reader), FeatureAttributeReadContext.Uncached);
    }

    private async Task<QueryResult<Feature>> QueryManagedSnapshotsAsync(
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var hasLimit = query.Limit is int limit && limit > 0;
        var sql = BuildFeatureSelectCore(query, probeLimit: hasLimit, comparison: null,
            projection: FeatureSelectProjection.ManagedSnapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var items = ImmutableArray.CreateBuilder<Feature>();
        await using (var command = CreateReadCommand(connection, sql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinals = ResolveSnapshotOrdinals(reader);
            var attributesReader = new FeatureAttributeReadContext();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadManagedSnapshotFeature(reader, ordinals, attributesReader));
            }
        }

        var hasMore = hasLimit && items.Count > query.Limit!.Value;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return await BuildManagedSnapshotResultAsync(
            connection,
            new ManagedSnapshotPage { Query = query, Items = items.ToImmutable(), HasMore = hasMore },
            cancellationToken)
            .ConfigureAwait(false);
    }

    private readonly record struct ManagedSnapshotPage
    {
        public required FeatureQuery Query { get; init; }

        public required ImmutableArray<Feature> Items { get; init; }

        public required bool HasMore { get; init; }
    }

    private async Task<QueryResult<Feature>> BuildManagedSnapshotResultAsync(
        NpgsqlConnectionLease connection,
        ManagedSnapshotPage page,
        CancellationToken cancellationToken)
    {
        if (!page.HasMore)
        {
            var total = page.Items.Length == 0 ? 0L : page.Query.Offset.GetValueOrDefault() + page.Items.Length;
            return total == 0 ? QueryResult<Feature>.Empty() : QueryResult<Feature>.Create(total, page.Items, hasMoreResults: false);
        }

        var countOptions = new CountExecutionOptions(CountScope.AssociatedFeatureRead, cancellationToken);
        var totalCount = Math.Max(
            await ExecuteCountOnConnectionAsync(connection, page.Query, countOptions).ConfigureAwait(false),
            page.Items.Length);
        return QueryResult<Feature>.Create(totalCount, page.Items, hasMoreResults: true);
    }

    private static ManagedSnapshotOrdinals ResolveSnapshotOrdinals(NpgsqlDataReader reader) => new()
    {
        Geometry = reader.GetOrdinal("__honua_snapshot_geometry"),
        Attributes = reader.GetOrdinal("__honua_snapshot_attributes")
    };

    private Feature ReadManagedSnapshotFeature(
        NpgsqlDataReader reader,
        ManagedSnapshotOrdinals ordinals,
        FeatureAttributeReadContext attributesReader)
    {
        // The displayed schema projection can add nulls or hide stored fields. Only
        // the original row from this same authorized snapshot can match the writer.
        var visible = ReadFeature(reader, new MappedFeatureRowContext { AttributesReader = attributesReader });
        var attributes = _dictionaryPool.Get();
        try
        {
            if (!reader.IsDBNull(ordinals.Attributes))
            {
                FeatureAttributeJsonReader.ReadInto(reader.GetString(ordinals.Attributes), attributes);
            }

            attributes[FieldNames.ObjectId] = visible.Id;
            var rawGeometry = reader.IsDBNull(ordinals.Geometry) ? null : reader.GetFieldValue<byte[]>(ordinals.Geometry);
            var raw = Feature.Create(visible.Id, rawGeometry, attributes.ToImmutableDictionary());
            return visible with
            {
                ReadStateToken = FeatureStateToken.Compute(raw),
                EditValidationAttributes = raw.Attributes
            };
        }
        finally
        {
            attributes.Clear();
            _dictionaryPool.Return(attributes);
        }
    }
}
