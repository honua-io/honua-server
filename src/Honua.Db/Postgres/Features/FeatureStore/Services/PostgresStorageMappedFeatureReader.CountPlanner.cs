// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Transactions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private bool ShouldUseSerialSourceSpatialCount(FeatureQuery query, bool isAssociatedFeatureRead) =>
        _preferSerialSourceSpatialCounts is { } preferSerial
            ? preferSerial && CanUseScopedSourceSpatialCount(query)
            // An omitted option only scopes counts required by an eligible bounded
            // read. Standalone counts keep PostgreSQL planning, even with a limit.
            : isAssociatedFeatureRead && CanUseBoundedSpatialPlan(query);

    private bool CanUseScopedSourceSpatialCount(FeatureQuery query) =>
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

    private static NpgsqlBatch CreateSerialSourceSpatialCountBatch(
        NpgsqlConnection connection, SqlBuilder sql, bool disableJit)
    {
        // Both settings must execute before the count is bound/planned, within
        // the same implicit transaction. A single setting SELECT keeps the count
        // at the second result regardless of which independent options are on.
        var settingSql = disableJit
            ? "SELECT pg_catalog.set_config('jit', 'off', true), " +
              "pg_catalog.set_config('max_parallel_workers_per_gather', '0', true)"
            : "SELECT pg_catalog.set_config('max_parallel_workers_per_gather', '0', true)";
        var batch = CreateScopedPlannerReadBatch(connection, sql, settingSql);
        var queryCommand = batch.BatchCommands[^1];
        // Each policy needs its own generic plan, even on a shared pooled backend.
        // Fixed trusted markers preserve the validated SELECT and its parameters.
        var policyTag = disableJit ? "serial-jit-off-source-count" : "serial-source-count";
        queryCommand.CommandText = $"SELECT ALL /* honua:{policyTag} */" +
            queryCommand.CommandText["SELECT ALL".Length..];
        return batch;
    }
}
