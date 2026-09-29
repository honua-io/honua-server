// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Transactions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private bool ShouldUseSerialSourceSpatialCount(FeatureQuery query) =>
        _preferSerialSourceSpatialCounts &&
        _mapping.IsSourceBacked &&
        _geometryColumn != null &&
        _resource.ReadGeometryType() == MetadataV2GeometryType.Point &&
        !query.Distinct &&
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
        Transaction.Current == null;

    private static NpgsqlBatch CreateSerialSourceSpatialCountBatch(NpgsqlConnection connection, SqlBuilder sql)
    {
        // Reuse the parameter binding and implicit-transaction cleanup of the
        // serial feature-read path. The fixed tag additionally isolates counts
        // from other scoped policies (such as JIT-only SELECT ALL counts).
        var batch = CreateSerialSpatialReadBatch(connection, sql);
        batch.BatchCommands[0].CommandText =
            "SELECT pg_catalog.set_config('max_parallel_workers_per_gather', '0', true)";
        var queryCommand = batch.BatchCommands[^1];
        queryCommand.CommandText = "SELECT ALL /* honua:serial-source-count */" +
            queryCommand.CommandText["SELECT ALL".Length..];
        return batch;
    }
}
