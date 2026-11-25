// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Transactions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private volatile bool _hasNonPointGeometry;

    private enum CountScope
    {
        Standalone,
        AssociatedFeatureRead
    }

    private enum CountSpatialPredicate
    {
        Default,
        PointEnvelope
    }

    private readonly record struct CountExecutionOptions(
        CountScope Scope,
        CancellationToken CancellationToken,
        CountSpatialPredicate SpatialPredicate = CountSpatialPredicate.Default);

    private bool ShouldUseSerialSourceSpatialCount(FeatureQuery query, bool isAssociatedFeatureRead) =>
        _preferSerialSourceSpatialCounts is { } preferSerial
            ? preferSerial && CanUseScopedSourceSpatialCount(query)
            // An omitted option only scopes counts required by an eligible bounded
            // first page of up to 100 features. Standalone counts keep PostgreSQL
            // planning, even with a limit. A count's cost does not shrink with the
            // page, so the wider bounded-read limits do not extend to counts.
            : isAssociatedFeatureRead && CanUseBoundedSpatialPlan(query) &&
              query.Limit <= MaxAutomaticSerialCountLimit && query.Offset.GetValueOrDefault() == 0;

    private const int MaxAutomaticSerialCountLimit = 100;

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

    // For source-backed Point layers, ST_Intersects(point, envelope) inflates PostgreSQL's
    // planner cost above parallel/JIT thresholds (~179K on 100K points). The GiST bbox operator
    // plus exact float8 ST_X/ST_Y bounds matches ST_Intersects bit-for-bit on 2D Points (including
    // POINT EMPTY and collapsed boxes) at ~46x lower cost with zero JIT and zero GEOS overhead.
    private bool CanUsePointEnvelopeCountPredicate(FeatureQuery query) =>
        !_hasNonPointGeometry &&
        !query.IncludeNullGeometry &&
        CanUseScopedSourceSpatialCount(query) &&
        query.SpatialFilter is { AntimeridianSplit: false } filter &&
        (filter.Srid ?? _storageSrid) == _storageSrid &&
        query.SpatialFilterDatumTransformation is null;

    private string BuildPointEnvelopeCountFilter(FeatureQuery query, SqlBuilder sql)
    {
        var filter = query.SpatialFilter!.Value;
        var geometryColumn = $"{_geometryColumn}::geometry";
        var minX = sql.AddParameter(filter.EnvelopeMinX!.Value);
        var minY = sql.AddParameter(filter.EnvelopeMinY!.Value);
        var maxX = sql.AddParameter(filter.EnvelopeMaxX!.Value);
        var maxY = sql.AddParameter(filter.EnvelopeMaxY!.Value);
        var srid = filter.Srid ?? _storageSrid;
        return $"{geometryColumn} && ST_MakeEnvelope({minX}, {minY}, {maxX}, {maxY}, {srid}) " +
               $"AND ST_X({geometryColumn}) >= {minX} AND ST_X({geometryColumn}) <= {maxX} " +
               $"AND ST_Y({geometryColumn}) >= {minY} AND ST_Y({geometryColumn}) <= {maxY}";
    }

    private static bool IsNonPointGeometryCountError(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InternalError &&
        (exception.MessageText.Contains("ST_X", StringComparison.Ordinal) ||
         exception.MessageText.Contains("ST_Y", StringComparison.Ordinal));

    private async Task<long> ExecuteCountOnConnectionAsync(
        NpgsqlConnectionLease connection,
        FeatureQuery query,
        CountExecutionOptions options)
    {
        var usePointEnvelope = connection.Transaction is null && CanUsePointEnvelopeCountPredicate(query);
        var initialPredicate = usePointEnvelope ? CountSpatialPredicate.PointEnvelope : CountSpatialPredicate.Default;
        try
        {
            return await ExecuteCountSqlAsync(connection, query, options with { SpatialPredicate = initialPredicate })
                .ConfigureAwait(false);
        }
        catch (PostgresException ex) when (
            usePointEnvelope &&
            !options.CancellationToken.IsCancellationRequested &&
            IsNonPointGeometryCountError(ex))
        {
            _hasNonPointGeometry = true;
            return await ExecuteCountSqlAsync(connection, query, options with { SpatialPredicate = CountSpatialPredicate.Default })
                .ConfigureAwait(false);
        }
    }

    private async Task<long> ExecuteCountSqlAsync(
        NpgsqlConnectionLease connection,
        FeatureQuery query,
        CountExecutionOptions options)
    {
        var sql = options.SpatialPredicate == CountSpatialPredicate.PointEnvelope
            ? BuildPointEnvelopeCountSql(query)
            : BuildCountSql(query);
        // A caller-owned transaction would retain SET LOCAL beyond this count.
        var useSerialPlan = ShouldUseSerialSourceSpatialCount(query, options.Scope == CountScope.AssociatedFeatureRead);
        var disableJit = ShouldDisableJitForSpatialCount(query);
        if (connection.Transaction is null && (useSerialPlan || disableJit))
        {
            await using var batch = useSerialPlan
                ? CreateSerialSourceSpatialCountBatch(connection, sql, disableJit)
                : CreateScopedPlannerReadBatch(connection, sql,
                    "SELECT pg_catalog.set_config('jit', 'off', true)");
            await using var reader = await batch.ExecuteReaderAsync(options.CancellationToken).ConfigureAwait(false);
            if (!await reader.NextResultAsync(options.CancellationToken).ConfigureAwait(false) ||
                !await reader.ReadAsync(options.CancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The scoped planner batch did not return a count.");
            }

            return reader.GetInt64(0);
        }

        await using var command = CreateReadCommand(connection, sql);
        var result = await command.ExecuteScalarAsync(options.CancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

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
