// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Transactions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private bool ShouldUseSerialSpatialPlan(FeatureQuery query) =>
        _preferSerialBoundedSpatialReads &&
        _mapping.IsSourceBacked &&
        _geometryColumn != null &&
        _resource.ReadGeometryType() == MetadataV2GeometryType.Point &&
        query.Limit is > 0 and <= 100 &&
        query.Offset.GetValueOrDefault() == 0 &&
        (query.OrderBy == null || query.OrderBy.Value.IsDefaultOrEmpty) &&
        !query.Distinct &&
        !query.IncludeNullGeometry &&
        query.VersionContext is not { IsDefault: false } &&
        query.SpatialFilter is
        {
            IsSimpleEnvelope: true,
            EnvelopeMinX: not null,
            EnvelopeMinY: not null,
            EnvelopeMaxX: not null,
            EnvelopeMaxY: not null,
            SpatialRelationship: SpatialRelationship.Intersects or SpatialRelationship.EnvelopeIntersects
        } &&
        // SET LOCAL would outlive the batch in an outer transaction. Exclude
        // ambient scopes here and borrowed explicit transactions after opening
        // the connection lease in ExecuteFeatureQueryAsync.
        Transaction.Current == null;

    private static NpgsqlBatch CreateSerialSpatialReadBatch(NpgsqlConnection connection, SqlBuilder sql)
    {
        var commandText = sql.ToString();
        PostgresSqlSafety.ValidateReadOnlySingleStatement(commandText);
        if (!commandText.StartsWith("SELECT ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The scoped spatial query must begin with SELECT.");
        }

        // ALL is the default SELECT modifier. It preserves the query while
        // giving auto-preparation a distinct identity: a generic parallel plan
        // prepared by the ordinary path cannot be reused inside this batch.
        var queryCommand = new NpgsqlBatchCommand("SELECT ALL" + commandText["SELECT".Length..]);
        foreach (var parameter in sql.Parameters)
        {
            queryCommand.Parameters.AddWithValue(NormalizeParameterValue(parameter));
        }

        // One Sync/implicit transaction keeps SET LOCAL scoped to this batch,
        // including cancellation, SQL errors and reader disposal. Error barriers
        // would introduce transaction boundaries between these two statements.
        var batch = new NpgsqlBatch(connection) { EnableErrorBarriers = false };
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "SELECT set_config('max_parallel_workers_per_gather', '0', true)"));
        batch.BatchCommands.Add(queryCommand);
        return batch;
    }
}
