// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads a provider-bound PostGIS table directly from the storage mapping persisted in Metadata v2.
/// </summary>
internal sealed partial class PostgresStorageMappedFeatureReader : IFeatureReader, IPagedFeatureReader, IStreamingFeatureStore
{
    private const string UnsupportedWhereClauseMessage =
        "WHERE clause format not supported for source-backed PostGIS layers.";
    private const int MaxJsonbBuildObjectPairs = 50;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ObjectPool<Dictionary<string, object?>> _dictionaryPool;
    private readonly MetadataV2Resource _resource;
    private readonly FeatureStorageMapping _mapping;
    private readonly DataConnection? _connection;
    private readonly IConnectionEncryptionService? _connectionEncryptionService;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly IRowLevelSecurityFilterSource? _rlsFilterSource;
    private readonly IFieldMaskSource? _fieldMaskSource;
    private readonly ILogger _logger;
    private readonly string _qualifiedTableName;
    private readonly string _primaryKeyColumn;
    private readonly string? _geometryColumn;
    private readonly int _storageSrid;
    private readonly string? _layerDiscriminatorColumn;
    private readonly int? _layerDiscriminatorValue;
    private string? _resolvedBoundConnectionString;

    public PostgresStorageMappedFeatureReader(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ObjectPool<Dictionary<string, object?>> dictionaryPool,
        MetadataV2Resource resource,
        FeatureStorageMapping mapping,
        DataConnection? connection,
        IConnectionEncryptionService? connectionEncryptionService,
        ILogger? logger = null,
        IFilterExpressionService? filterExpressionService = null,
        IRowLevelSecurityFilterSource? rlsFilterSource = null,
        IFieldMaskSource? fieldMaskSource = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _dictionaryPool = dictionaryPool ?? throw new ArgumentNullException(nameof(dictionaryPool));
        _resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _connection = connection;
        _connectionEncryptionService = connectionEncryptionService;
        _filterExpressionService = filterExpressionService;
        _rlsFilterSource = rlsFilterSource;
        _fieldMaskSource = fieldMaskSource;
        _logger = logger ?? NullLogger.Instance;
        _qualifiedTableName = QuoteQualifiedTableName(_mapping);
        _primaryKeyColumn = ValidateAndQuoteIdentifier(_mapping.PrimaryKeyColumn);
        _geometryColumn = string.IsNullOrWhiteSpace(_mapping.GeometryColumn)
            ? null
            : ValidateAndQuoteIdentifier(_mapping.GeometryColumn!);
        _storageSrid = _mapping.StorageSrid ?? _resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        // When the binding declares a layer-discriminator column (rows for many layers
        // share one physical table, e.g. the shared 'features' table keyed by 'layer_id'),
        // capture the column and the bound layer's id so every read is constrained to this
        // layer's rows. Without it, a query for one layer would leak features of other
        // layers stored in the same table.
        _layerDiscriminatorColumn = string.IsNullOrWhiteSpace(_mapping.LayerDiscriminatorColumn)
            ? null
            : ValidateAndQuoteIdentifier(_mapping.LayerDiscriminatorColumn!);
        _layerDiscriminatorValue = _mapping.LayerDiscriminatorValue;
    }

    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(featureId),
            Limit = 1
        };

        var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.Length == 0 ? null : result.Items[0];
    }

    public async Task<QueryResult<Feature>> QueryAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyReadSecurityAsync(query, cancellationToken).ConfigureAwait(false);

        if (IsNearestNeighborQuery(query))
        {
            var nearestItems = await ExecuteFeatureQueryAsync(query, probeLimit: false, cancellationToken).ConfigureAwait(false);
            return nearestItems.Length == 0
                ? QueryResult<Feature>.Empty()
                : QueryResult<Feature>.Create(nearestItems.Length, nearestItems, hasMoreResults: false);
        }

        var totalCount = await CountAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (totalCount == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var items = await ExecuteFeatureQueryAsync(query, probeLimit: false, cancellationToken).ConfigureAwait(false);
        var offset = query.Offset.GetValueOrDefault();
        var hasMoreResults = offset + items.Length < totalCount;
        return QueryResult<Feature>.Create(totalCount, items, hasMoreResults);
    }

    public Task<byte[]?> QueryFlatGeobufAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => QueryFlatGeobufAsyncCore(query, cancellationToken);

    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var sql = new SqlBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"SELECT {_primaryKeyColumn}::bigint FROM {_qualifiedTableName}");
        AppendFilter(sql, query);
        AppendOrderBy(sql, query);
        AppendPagination(sql, query);

        var objectIds = ImmutableArray.CreateBuilder<long>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objectIds.Add(reader.GetInt64(0));
        }

        return objectIds.ToImmutable();
    }

    public async Task<long> CountAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        query = await ApplyReadSecurityAsync(query, cancellationToken).ConfigureAwait(false);

        var sql = new SqlBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"SELECT COUNT(*)::bigint FROM {_qualifiedTableName}");
        AppendFilter(sql, query);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId,
        FeatureQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        if (_geometryColumn == null)
        {
            return null;
        }

        var effectiveQuery = query ?? new FeatureQuery();
        var geometryExpression = BuildGeometryExpression(effectiveQuery);
        var sql = new SqlBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent({geometryExpression}) AS extent
                FROM {_qualifiedTableName}
            """);
        AppendFilter(sql, effectiveQuery, prefix: "WHERE");
        sql.Append(CultureInfo.InvariantCulture, $"""

                  {BuildWhereJoiner(sql)} {geometryExpression} IS NOT NULL
            ) AS extent_query
            """);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        var srid = effectiveQuery.OutputSrid ?? _storageSrid;
        return FeatureExtent.Create(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            srid);
    }

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.OutStatistics.HasValue || query.OutStatistics.Value.IsDefaultOrEmpty)
        {
            throw new ArgumentException("OutStatistics must be specified for a statistics query.", nameof(query));
        }

        return ExecuteStatisticsQueryAsync(query, cancellationToken);
    }

    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        string fieldName,
        TemporalPropertyType propertyType,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Temporal extents are not supported for source-backed PostGIS layers yet.");

    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var countTask = CountAsync(layerId, new FeatureQuery(), cancellationToken);
        var extentTask = GetExtentAsync(layerId, null, cancellationToken);
        await Task.WhenAll(countTask, extentTask).ConfigureAwait(false);

        return new EstimateResult
        {
            EstimatedCount = await countTask.ConfigureAwait(false),
            Extent = await extentTask.ConfigureAwait(false)
        };
    }

    public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Top-feature queries are not supported for source-backed PostGIS layers yet.");

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Date bins are not supported for source-backed PostGIS layers yet.");

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Bins are not supported for source-backed PostGIS layers yet.");

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("H3 queries are not supported for source-backed PostGIS layers yet.");

    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return PagedQueryResult<Feature>.Create(result.Items, result.HasMoreResults, result.TotalCount);
        }

        var items = await ExecuteFeatureQueryAsync(query, probeLimit: true, cancellationToken).ConfigureAwait(false);
        if (items.Length == 0)
        {
            return PagedQueryResult<Feature>.Empty();
        }

        var hasMoreResults = items.Length > query.Limit.Value;
        if (hasMoreResults)
        {
            items = items.RemoveAt(items.Length - 1);
        }

        return PagedQueryResult<Feature>.Create(items, hasMoreResults);
    }

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sql = BuildFeatureSelect(query, probeLimit: false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadFeature(reader);
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId,
        FeatureQuery query,
        int batchSize = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var batch = new List<Feature>(Math.Max(1, batchSize));
        await foreach (var feature in StreamFeaturesAsync(layerId, query, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            batch.Add(feature);
            if (batch.Count >= batchSize)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    public IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GML streaming is not supported for source-backed PostGIS layers yet.");

    private async Task<ImmutableArray<Feature>> ExecuteFeatureQueryAsync(
        FeatureQuery query,
        bool probeLimit,
        CancellationToken cancellationToken)
    {
        var sql = BuildFeatureSelect(query, probeLimit);
        var features = ImmutableArray.CreateBuilder<Feature>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            features.Add(ReadFeature(reader));
        }

        return features.ToImmutable();
    }

    private SqlBuilder BuildFeatureSelect(FeatureQuery query, bool probeLimit)
    {
        var sql = new SqlBuilder();
        // Preserve Z/M ordinates through extended WKB when the canonical query requests it (returnZ/
        // returnM); ST_AsBinary emits 2D OGC WKB and silently drops higher ordinates. EWKB is read
        // transparently by the WKB consumers, so 2D-only data is unaffected.
        var geometryEncoder = query.IncludeZ || query.IncludeM ? "ST_AsEWKB" : "ST_AsBinary";
        var geometrySelect = _geometryColumn == null
            ? "NULL"
            : $"{geometryEncoder}({BuildGeometryExpression(query)})";
        var attributesSelect = BuildAttributesExpression(query);
        var distanceSelect = BuildDistanceSelectExpression(query, sql);

        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT {_primaryKeyColumn}::bigint AS objectid,
                   {geometrySelect} AS geometry,
                   {attributesSelect} AS attributes{distanceSelect}
            FROM {_qualifiedTableName}
            """);
        AppendFilter(sql, query);
        AppendOrderBy(sql, query);
        AppendPagination(sql, query, probeLimit);
        return sql;
    }

    private string BuildGeometryExpression(FeatureQuery query)
    {
        if (_geometryColumn == null)
        {
            return "NULL";
        }

        var geometryExpression = $"{_geometryColumn}::geometry";
        var storageSrid = _storageSrid;
        if (query.OutputSrid.HasValue && query.OutputSrid.Value != storageSrid)
        {
            geometryExpression = DatumTransformSql.BuildTransformExpression(geometryExpression, query.OutputSrid.Value, query.OutputDatumTransformation);
        }

        return geometryExpression;
    }

    private string BuildAttributesExpression(FeatureQuery query)
    {
        if (query.ExcludeAttributes)
        {
            return "NULL";
        }

        var fields = ResolveAttributeFields(query);
        if (fields.Length == 0)
        {
            return "'{}'::jsonb::text";
        }

        return BuildAttributesExpressionText(fields, useMapping: true);
    }

    // Kept as a static single-arg overload so the existing reflection-driven unit test
    // (PostgresStorageMappedFeatureReaderSqlTests.BuildAttributesExpressionText_With...)
    // continues to invoke the column-per-field shape directly; the production caller goes
    // through the instance overload to pick up an attributes JSONB column when the resource
    // binding declared one.
    private static string BuildAttributesExpressionText(MetadataV2Field[] fields)
        => BuildAttributesExpressionText(fields, attributesColumn: null);

    private string BuildAttributesExpressionText(MetadataV2Field[] fields, bool useMapping)
        => BuildAttributesExpressionText(fields, useMapping ? _mapping.AttributesColumn : null);

    private static string BuildAttributesExpressionText(MetadataV2Field[] fields, string? attributesColumn)
    {
        var chunks = fields
            .Chunk(MaxJsonbBuildObjectPairs)
            .Select(chunk => BuildAttributesExpressionChunk(chunk, attributesColumn));

        return $"({string.Join(" || ", chunks)})::text";
    }

    private static string BuildAttributesExpressionChunk(IEnumerable<MetadataV2Field> fields, string? attributesColumn)
    {
        var parts = new List<string>();
        var jsonbAccessor = string.IsNullOrWhiteSpace(attributesColumn)
            ? null
            : ValidateAndQuoteIdentifier(attributesColumn);
        foreach (var field in fields)
        {
            parts.Add($"'{EscapeSqlLiteral(field.Name)}'");
            // When the resource declares an attributes JSONB column (a single column holding
            // the per-field values), pull the field from that column instead of expecting a
            // column-per-field on the table — the seed-driven test fixture's shared 'features'
            // table follows that shape, and v1 catalog readers used the same projection before
            // the cutover.
            //
            // Use the jsonb-preserving accessor (->) for numeric/boolean/json fields so the
            // value round-trips with its declared type (an integer field returns a JSON
            // number, not a string). Text-like types keep the text accessor (->>) so existing
            // string/date formatting is unchanged.
            if (jsonbAccessor is not null)
            {
                var accessor = PreservesJsonType(field.Type) ? "->" : "->>";
                parts.Add($"{jsonbAccessor} {accessor} '{EscapeSqlLiteral(field.Name)}'");
            }
            else
            {
                parts.Add(ValidateAndQuoteIdentifier(field.Name));
            }
        }

        return $"jsonb_build_object({string.Join(", ", parts)})";
    }

    /// <summary>
    /// Whether a field's value should be projected with the jsonb-preserving
    /// accessor (<c>-&gt;</c>) so its declared numeric/boolean/json type survives,
    /// rather than the text accessor (<c>-&gt;&gt;</c>) which stringifies it.
    /// </summary>
    private static bool PreservesJsonType(MetadataV2FieldType type)
        => type is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float
            or MetadataV2FieldType.Boolean
            or MetadataV2FieldType.Json;

    private MetadataV2Field[] ResolveAttributeFields(FeatureQuery query)
    {
        var maskedFields = query.EnforcedMaskedFields is { IsDefaultOrEmpty: false } fields
            ? fields.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var declaredFields = _resource.SchemaFields
            .Where(field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
                && IsSafeIdentifier(field.Name)
                && !maskedFields.Contains(field.Name))
            .ToArray();

        if (!query.OutFields.HasValue || query.OutFields.Value.IsDefault)
        {
            return declaredFields;
        }

        if (query.OutFields.Value.IsEmpty)
        {
            return [];
        }

        var requested = query.OutFields.Value.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return declaredFields
            .Where(field => requested.Contains(field.Name))
            .ToArray();
    }

    private void AppendFilter(SqlBuilder sql, FeatureQuery query, string prefix = "WHERE")
    {
        var conditions = new List<string>();

        // Constrain shared-table reads to the bound layer's rows. The discriminator
        // (e.g. 'layer_id' on the shared 'features' table) is applied to every read path
        // because they all funnel through AppendFilter, preventing cross-layer leakage.
        if (_layerDiscriminatorColumn is not null && _layerDiscriminatorValue is int discriminator)
        {
            conditions.Add($"{_layerDiscriminatorColumn} = {sql.AddParameter(discriminator)}");
        }

        if (query.EnforcedSqlFilter != null)
        {
            conditions.Add(ConvertSqlFilter(query.EnforcedSqlFilter, sql));
        }

        if (query.SqlFilter != null)
        {
            conditions.Add(ConvertSqlFilter(query.SqlFilter, sql));
        }
        else if (!string.IsNullOrWhiteSpace(query.Where))
        {
            conditions.Add(ParseWhereClause(query.Where!, sql));
        }

        if (query.ObjectIds.HasValue && !query.ObjectIds.Value.IsDefaultOrEmpty)
        {
            conditions.Add($"{_primaryKeyColumn} = ANY({sql.AddParameter(query.ObjectIds.Value.ToArray())})");
        }

        if (query.SpatialFilter.HasValue && _geometryColumn != null)
        {
            conditions.Add(BuildSpatialFilter(query.SpatialFilter.Value, sql));
        }

        if (query.TemporalFilter.HasValue)
        {
            conditions.Add(BuildTemporalFilter(query.TemporalFilter.Value, sql));
        }

        if (conditions.Count == 0)
        {
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $" {prefix} ");
        sql.Append(
            CultureInfo.InvariantCulture,
            string.Join(" AND ", conditions.Select(static condition => $"({condition})")));
    }

    private async Task<FeatureQuery> ApplyReadSecurityAsync(
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var needsFilter = query.EnforcedSqlFilter is null;
        var needsFieldMask = query.EnforcedMaskedFields is null;
        if (!needsFilter && !needsFieldMask)
        {
            return query;
        }

        if (needsFilter &&
            _resource.PermanentFilter is { Expression: { Length: > 0 } } &&
            _filterExpressionService is null)
        {
            throw new InvalidOperationException(
                $"Permanent filter enforcement is unavailable for source-backed resource '{_resource.Metadata.Id}'.");
        }

        if (needsFilter)
        {
            var permanentFilter = PermanentFilterResolver.Resolve(_resource, _filterExpressionService);
            var rlsFilter = _rlsFilterSource is null
                ? null
                : await _rlsFilterSource.ResolveAsync(_resource, cancellationToken).ConfigureAwait(false);
            var enforcedFilter = SqlFragmentHelpers.CombineSqlFilters(permanentFilter, rlsFilter);
            if (enforcedFilter != null)
            {
                query = query with { EnforcedSqlFilter = enforcedFilter };
            }
        }

        if (needsFieldMask)
        {
            var maskedFields = _fieldMaskSource is null
                ? ImmutableArray<string>.Empty
                : await _fieldMaskSource.ResolveAsync(_resource, cancellationToken).ConfigureAwait(false);
            if (!maskedFields.IsDefaultOrEmpty)
            {
                query = query with { EnforcedMaskedFields = maskedFields };
            }
        }

        return query;
    }

    // OGC API Features `datetime` (and any temporal query) lands here as a
    // TemporalFilter. The storage-mapped reader previously dropped it, so the
    // filter was silently ignored. Row interval is [start, COALESCE(end, start)];
    // it must intersect the query interval [Start, End].
    private string BuildTemporalFilter(TemporalFilter filter, SqlBuilder sql)
    {
        var startColumn = WrapEpochAwareTimestamp(ResolveColumnExpression(filter.PropertyName));
        var endColumn = string.IsNullOrWhiteSpace(filter.EndPropertyName)
            ? startColumn
            : WrapEpochAwareTimestamp(ResolveColumnExpression(filter.EndPropertyName!));
        var rowEnd = $"COALESCE({endColumn}, {startColumn})";

        var clauses = new List<string>();
        if (filter.Start.HasValue)
        {
            clauses.Add($"{rowEnd} >= {sql.AddParameter(filter.Start.Value.UtcDateTime)}");
        }

        if (filter.End.HasValue)
        {
            clauses.Add($"{startColumn} <= {sql.AddParameter(filter.End.Value.UtcDateTime)}");
        }

        return clauses.Count == 0 ? "TRUE" : string.Join(" AND ", clauses);
    }

    // The same date attribute can be persisted as an ISO-8601 string (seeded rows)
    // or as epoch-milliseconds rendered as text (Esri applyEdits). A bare
    // `::timestamptz` cast on epoch-ms text raises SQLSTATE 22008. Detect the
    // numeric form and convert via to_timestamp; otherwise cast the ISO text.
    private static string WrapEpochAwareTimestamp(string textExpression)
    {
        var trimmed = $"NULLIF({textExpression}, '')";
        return $"CASE WHEN {trimmed} ~ '^-?[0-9]+$' " +
               $"THEN to_timestamp({trimmed}::double precision / 1000.0) " +
               $"ELSE {trimmed}::timestamptz END";
    }

    private static string BuildWhereJoiner(SqlBuilder sql)
        => sql.TextContainsWhere ? "AND" : "WHERE";

    private async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var sql = new SqlBuilder();
        var groupByExpressions = new List<string>();
        var selectParts = new List<string>();

        if (query.GroupByFields.HasValue && !query.GroupByFields.Value.IsDefaultOrEmpty)
        {
            foreach (var groupByField in query.GroupByFields.Value)
            {
                var groupByExpression = ResolveColumnExpression(groupByField);
                selectParts.Add($"{groupByExpression} AS {ValidateAndQuoteIdentifier(groupByField)}");
                groupByExpressions.Add(groupByExpression);
            }
        }

        foreach (var statistic in query.OutStatistics!.Value)
        {
            var field = ResolveFieldDefinition(statistic.OnStatisticField);
            var fieldExpression = ResolveColumnExpression(statistic.OnStatisticField);
            var aggregateExpression = BuildStatisticsAggregateExpression(
                statistic.StatisticType,
                fieldExpression,
                field.Type);
            selectParts.Add($"{aggregateExpression} AS {ValidateAndQuoteIdentifier(statistic.OutStatisticFieldName)}");
        }

        sql.Append(CultureInfo.InvariantCulture, $"SELECT {string.Join(", ", selectParts)} FROM {_qualifiedTableName}");
        AppendFilter(sql, query);

        if (groupByExpressions.Count > 0)
        {
            sql.Append(CultureInfo.InvariantCulture, $" GROUP BY {string.Join(", ", groupByExpressions)}");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>();
        string[]? fieldNames = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fieldNames ??= Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToArray();
            var row = new Dictionary<string, object?>(fieldNames.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fieldNames.Length; i++)
            {
                row[fieldNames[i]] = reader.IsDBNull(i)
                    ? null
                    : ConvertStatisticsValue(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows.ToImmutable();
    }

    internal static string BuildStatisticsAggregateExpression(
        StatisticType statisticType,
        string fieldExpression,
        MetadataV2FieldType fieldType)
    {
        var numericExpression = BuildNullableNumericExpression(fieldExpression);
        var orderedExpression = IsNumericFieldType(fieldType) ? numericExpression : fieldExpression;
        return statisticType switch
        {
            StatisticType.Count => IsNumericFieldType(fieldType)
                ? $"COUNT({numericExpression})"
                : $"COUNT({fieldExpression})",
            StatisticType.Sum => $"SUM({numericExpression})",
            StatisticType.Min => $"MIN({orderedExpression})",
            StatisticType.Max => $"MAX({orderedExpression})",
            StatisticType.Avg => $"AVG({numericExpression})",
            StatisticType.Stddev => $"STDDEV_SAMP({numericExpression})",
            StatisticType.Var => $"VAR_SAMP({numericExpression})",
            _ => throw new ArgumentOutOfRangeException(nameof(statisticType), statisticType, "Unsupported statistic type")
        };
    }

    private static string BuildNullableNumericExpression(string fieldExpression)
        => $"NULLIF(({fieldExpression})::text, '')::numeric";

    private static bool IsNumericFieldType(MetadataV2FieldType fieldType)
        => fieldType is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float;

    private string ConvertSqlFilter(SqlFragment sqlFilter, SqlBuilder sql)
    {
        var converted = SqlFilterParameterRegex().Replace(
            sqlFilter.Sql,
            match =>
            {
                var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
                if ((uint)index >= (uint)sqlFilter.Parameters.Count)
                {
                    throw new ArgumentException(UnsupportedWhereClauseMessage);
                }

                return sql.AddParameter(sqlFilter.Parameters[index]);
            });

        converted = RewriteAttributeTextAccessExpressions(converted, ResolveColumnExpression, TryResolveFieldType);

        return QuotedIdentifierRegex().Replace(
            converted,
            match =>
            {
                var identifier = match.Groups["identifier"].Value.Replace("\"\"", "\"", StringComparison.Ordinal);
                return ResolveColumnExpression(identifier);
            });
    }

    internal static string RewriteAttributeTextAccessExpressions(
        string sql,
        Func<string, string> resolveColumnExpression,
        Func<string, MetadataV2FieldType?>? resolveFieldType = null)
        => AttributeTextAccessRegex().Replace(
            sql,
            match =>
            {
                var fieldName = match.Groups["field"].Value.Replace("''", "'", StringComparison.Ordinal);
                var column = $"({resolveColumnExpression(fieldName)})::text";

                // The translator emits `attributes ->> 'field'` text accessors for filter
                // operands. For numeric/temporal/boolean/uuid fields the empty-string-to-NULL
                // coercion is required so a downstream `::numeric`/`::timestamptz` cast does
                // not choke on empty text (and so Esri's "empty numeric == null" semantics
                // hold). For text/string fields it must NOT be applied: Esri and PostgreSQL
                // treat an empty string as a non-NULL value, so wrapping a string column in
                // NULLIF(..., '') would make `field IS NOT NULL` (and `field = ''`) silently
                // drop empty-string rows, diverging from the source service (#1703).
                var fieldType = resolveFieldType?.Invoke(fieldName);
                if (PreservesEmptyString(fieldType))
                {
                    return column;
                }

                return $"NULLIF({column}, '')";
            });

    // String-like fields whose empty-string values must survive filter translation as a
    // non-NULL value. Null (unresolved) field type keeps the legacy NULLIF behavior so the
    // change is scoped to fields whose type is known to be textual.
    private static bool PreservesEmptyString(MetadataV2FieldType? fieldType)
        => fieldType is MetadataV2FieldType.String or MetadataV2FieldType.Uuid;

    private MetadataV2FieldType? TryResolveFieldType(string fieldName)
    {
        if (fieldName.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("object_id", StringComparison.OrdinalIgnoreCase))
        {
            return MetadataV2FieldType.BigInteger;
        }

        return _resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))?.Type;
    }

    private string BuildSpatialFilter(SpatialFilter filter, SqlBuilder sql)
    {
        var geometryColumn = $"{_geometryColumn}::geometry";
        if (filter.SpatialRelationship == SpatialRelationship.NearestNeighbor)
        {
            return $"{geometryColumn} IS NOT NULL";
        }

        var filterGeometry = BuildFilterGeometryExpression(filter, sql);

        return filter.SpatialRelationship switch
        {
            // Esri semantics (mirroring the canonical FeatureQueryBuilder path):
            // esriSpatialRelWithin  => filter geometry is within the feature geometry
            //                          => ST_Within(filter, feature).
            // esriSpatialRelContains => filter geometry contains the feature geometry
            //                          => ST_Contains(filter, feature).
            // The operands must lead with the filter geometry; reversing them (feature, filter)
            // makes an envelope/feature .within(bounds) query return 0 because a point feature
            // can never ST_Contains an enclosing envelope.
            SpatialRelationship.Within => $"ST_Within({filterGeometry}, {geometryColumn})",
            SpatialRelationship.Contains => $"ST_Contains({filterGeometry}, {geometryColumn})",
            SpatialRelationship.EnvelopeIntersects => $"{geometryColumn} && {filterGeometry}",
            SpatialRelationship.Crosses => $"ST_Crosses({geometryColumn}, {filterGeometry})",
            SpatialRelationship.Touches => $"ST_Touches({geometryColumn}, {filterGeometry})",
            SpatialRelationship.Overlaps => $"ST_Overlaps({geometryColumn}, {filterGeometry})",
            SpatialRelationship.Disjoint => $"ST_Disjoint({geometryColumn}, {filterGeometry})",
            SpatialRelationship.Equals => $"ST_Equals({geometryColumn}, {filterGeometry})",
            SpatialRelationship.WithinDistance => BuildDistancePredicate(geometryColumn, filterGeometry, filter, sql, beyond: false),
            SpatialRelationship.BeyondDistance => BuildDistancePredicate(geometryColumn, filterGeometry, filter, sql, beyond: true),
            _ => $"ST_Intersects({geometryColumn}, {filterGeometry})"
        };
    }

    private string BuildFilterGeometryExpression(SpatialFilter filter, SqlBuilder sql)
    {
        if (filter.IsSimpleEnvelope &&
            filter.EnvelopeMinX.HasValue &&
            filter.EnvelopeMinY.HasValue &&
            filter.EnvelopeMaxX.HasValue &&
            filter.EnvelopeMaxY.HasValue)
        {
            var minX = sql.AddParameter(filter.EnvelopeMinX.Value);
            var minY = sql.AddParameter(filter.EnvelopeMinY.Value);
            var maxX = sql.AddParameter(filter.EnvelopeMaxX.Value);
            var maxY = sql.AddParameter(filter.EnvelopeMaxY.Value);
            var srid = filter.Srid ?? _storageSrid;
            var envelope = $"ST_MakeEnvelope({minX}, {minY}, {maxX}, {maxY}, {srid})";
            return TransformFilterGeometryIfNeeded(envelope, srid);
        }

        var geometryParameter = sql.AddParameter(filter.Geometry);
        var sourceSrid = filter.Srid ?? _storageSrid;
        var rawGeometry = $"ST_GeomFromEWKB({geometryParameter})";
        var geometry = $"ST_SetSRID({rawGeometry}, COALESCE(NULLIF(ST_SRID({rawGeometry}), 0), {sourceSrid}))";
        return TransformFilterGeometryIfNeeded(geometry, sourceSrid);
    }

    private string TransformFilterGeometryIfNeeded(string geometryExpression, int sourceSrid)
    {
        var targetSrid = _storageSrid;
        return sourceSrid == targetSrid
            ? geometryExpression
            : $"ST_Transform({geometryExpression}, {targetSrid})";
    }

    private string BuildDistancePredicate(
        string geometryColumn,
        string filterGeometry,
        SpatialFilter filter,
        SqlBuilder sql,
        bool beyond)
    {
        var distance = ConvertDistanceToMeters(filter.Distance ?? 0d, filter.DistanceUnit);
        return BuildDistancePredicateSql(geometryColumn, filterGeometry, _storageSrid, distance, beyond, sql.AddParameter);
    }

    /// <summary>
    /// Builds the ST_Distance distance/beyond predicate for a storage-mapped layer. Both operands
    /// arrive in the storage SRID (the column natively, the filter after
    /// <see cref="TransformFilterGeometryIfNeeded"/>). A bare <c>::geography</c> cast requires
    /// lon/lat input and throws for projected-storage layers (a 500), so both operands are
    /// reprojected to WGS84 geography via <see cref="ToWgs84Geography"/> (the same path KNN uses)
    /// so <c>ST_Distance</c> returns metres regardless of the storage CRS. (#2740)
    /// </summary>
    internal static string BuildDistancePredicateSql(
        string geometryColumn,
        string filterGeometry,
        int storageSrid,
        double distanceInMeters,
        bool beyond,
        Func<object?, string> addParameter)
    {
        var operatorText = beyond ? ">" : "<=";
        var geographyColumn = ToWgs84Geography(geometryColumn, storageSrid);
        var geographyFilter = ToWgs84Geography(filterGeometry, storageSrid);
        return $"ST_Distance({geographyColumn}, {geographyFilter}) {operatorText} {addParameter(distanceInMeters)}";
    }

    private static double ConvertDistanceToMeters(double distance, DistanceUnit unit)
        => unit switch
        {
            DistanceUnit.Feet => distance * 0.3048d,
            DistanceUnit.Kilometers => distance * 1000d,
            DistanceUnit.Miles => distance * 1609.344d,
            _ => distance
        };

    private void AppendOrderBy(SqlBuilder sql, FeatureQuery query)
    {
        if (IsNearestNeighborQuery(query))
        {
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {BuildNearestNeighborOrderExpression(query, sql)}");
            return;
        }

        if (query.OrderBy.HasValue && !query.OrderBy.Value.IsDefaultOrEmpty)
        {
            var clauses = new List<string>();
            foreach (var clause in query.OrderBy.Value)
            {
                var column = ResolveSortColumnExpression(clause.Field);
                clauses.Add(
                    $"{column} {(clause.Ascending ? "ASC" : "DESC")}{FeatureQueryBuilder.GetNullOrderingSuffix(clause.NullOrdering)}");
            }

            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {string.Join(", ", clauses)}");
            return;
        }

        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY {_primaryKeyColumn}");
    }

    private static void AppendPagination(SqlBuilder sql, FeatureQuery query, bool probeLimit = false)
    {
        var spatialFilter = query.SpatialFilter;
        var isNearestNeighbor = spatialFilter.HasValue &&
                                spatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;
        var nearestCount = isNearestNeighbor
            ? spatialFilter.GetValueOrDefault().NearestCount
            : null;
        var limit = isNearestNeighbor
            ? nearestCount ?? query.Limit
            : query.Limit;

        if (limit.HasValue)
        {
            var effectiveLimit = probeLimit && !isNearestNeighbor
                ? checked(limit.Value + 1)
                : limit.Value;
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {sql.AddParameter(effectiveLimit)}");
        }

        if (query.Offset.HasValue)
        {
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET {sql.AddParameter(query.Offset.Value)}");
        }
    }

    private string ParseWhereClause(string whereClause, SqlBuilder sql)
    {
        var trimmed = whereClause.Trim();
        if (TrueLiteralRegex().IsMatch(trimmed))
        {
            return "TRUE";
        }

        var expressions = SplitOnAnd(trimmed);
        if (expressions.Count == 0)
        {
            throw new ArgumentException(UnsupportedWhereClauseMessage);
        }

        var parameterizedExpressions = new List<string>(expressions.Count);
        // Not rewritten as .Select(e => e.Trim()): the trim is only the first step of a multi-branch
        // per-expression parse (null-check vs. comparison), not a pure 1:1 mapping.
        foreach (var part in (expressions).Select(expression => expression.Trim()))
        {
            var nullMatch = NullCheckRegex().Match(part);
            if (nullMatch.Success)
            {
                var nullColumn = ResolveColumnExpression(nullMatch.Groups["field"].Value);
                var notClause = string.IsNullOrWhiteSpace(nullMatch.Groups["not"].Value) ? string.Empty : "NOT ";
                parameterizedExpressions.Add($"{nullColumn} IS {notClause}NULL");
                continue;
            }

            var inMatch = InRegex().Match(part);
            if (inMatch.Success)
            {
                var inColumn = ResolveColumnExpression(inMatch.Groups["field"].Value);
                var placeholders = SplitValueTokens(inMatch.Groups["values"].Value)
                    .Select(valueToken => sql.AddParameter(ParseValueToken(valueToken, forceText: false)));
                parameterizedExpressions.Add($"{inColumn} IN ({string.Join(", ", placeholders)})");
                continue;
            }

            var comparisonMatch = ComparisonRegex().Match(part);
            if (!comparisonMatch.Success)
            {
                throw new ArgumentException(UnsupportedWhereClauseMessage);
            }

            var column = ResolveColumnExpression(comparisonMatch.Groups["field"].Value);
            var normalizedOperator = NormalizeOperator(comparisonMatch.Groups["op"].Value);
            var value = ParseValueToken(
                comparisonMatch.Groups["value"].Value,
                normalizedOperator.Contains("LIKE", StringComparison.OrdinalIgnoreCase));
            parameterizedExpressions.Add($"{column} {normalizedOperator} {sql.AddParameter(value)}");
        }

        return string.Join(" AND ", parameterizedExpressions);
    }

    private string ResolveColumnExpression(string fieldName)
    {
        if (fieldName.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("object_id", StringComparison.OrdinalIgnoreCase))
        {
            return _primaryKeyColumn;
        }

        // OGC API Features uses the synthetic property name "geometry" in CQL2 filters
        // to refer to the primary geometry field regardless of the resource's actual
        // geometry field name (e.g. "shape", "geom", "wkb_geometry"). Map it here so
        // SQL fragments emitted by the filter translator resolve to the storage column.
        if (fieldName.Equals("geometry", StringComparison.OrdinalIgnoreCase) &&
            !_resource.SchemaFields.Any(f => f.Name.Equals("geometry", StringComparison.OrdinalIgnoreCase)))
        {
            if (_geometryColumn == null)
            {
                throw new ArgumentException($"Resource '{_resource.Metadata.Name}' does not define a geometry column.");
            }
            return _geometryColumn;
        }

        // When the upstream translator already emitted a JSONB attribute accessor
        // (`"attributes" ->> 'field'`), our regex passes will re-visit the literal
        // `"attributes"` token. That is the storage column itself, not a schema
        // field, so resolve it back to the quoted identifier verbatim.
        if (!string.IsNullOrWhiteSpace(_mapping.AttributesColumn) &&
            fieldName.Equals(_mapping.AttributesColumn, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateAndQuoteIdentifier(_mapping.AttributesColumn!);
        }

        var field = _resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (field == null)
        {
            throw new ArgumentException($"Field '{fieldName}' was not found on resource '{_resource.Metadata.Name}'.");
        }

        if (field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
        {
            if (_geometryColumn == null)
            {
                throw new ArgumentException($"Resource '{_resource.Metadata.Name}' does not define a geometry column.");
            }

            return _geometryColumn;
        }

        // When the storage binding declares an attributes JSONB column, scalar fields
        // are persisted inside that JSONB blob rather than as individual columns. Route
        // WHERE/ORDER BY references through the JSONB accessor so the emitted SQL
        // matches the on-disk shape (the BuildAttributesExpressionChunk projection does
        // the same on the SELECT side).
        if (!string.IsNullOrWhiteSpace(_mapping.AttributesColumn))
        {
            var jsonbAccessor = ValidateAndQuoteIdentifier(_mapping.AttributesColumn!);
            return $"({jsonbAccessor} ->> '{EscapeSqlLiteral(field.Name)}')";
        }

        return ValidateAndQuoteIdentifier(field.Name);
    }

    /// <summary>
    /// Resolves a field reference used in an ORDER BY clause. Scalar fields persisted inside
    /// the attributes JSONB column are read with the text accessor (<c>-&gt;&gt;</c>), which
    /// would otherwise sort numerically- and temporally-typed values lexicographically
    /// (e.g. "10" before "9"). Cast those fields to their declared SQL type so sorting matches
    /// the field's semantics; text/uuid fields keep the plain text accessor.
    /// </summary>
    private string ResolveSortColumnExpression(string fieldName)
    {
        var column = ResolveColumnExpression(fieldName);

        // Only attributes-JSONB scalar accessors need a typed cast. The primary key and
        // physical (non-JSONB) columns are already correctly typed, and geometry columns
        // are not orderable here.
        if (string.IsNullOrWhiteSpace(_mapping.AttributesColumn) ||
            !column.StartsWith('(') ||
            !column.Contains("->>", StringComparison.Ordinal))
        {
            return column;
        }

        var field = _resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return column;
        }

        var sortCast = field.Type switch
        {
            MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger => "::bigint",
            MetadataV2FieldType.Double or MetadataV2FieldType.Float => "::double precision",
            MetadataV2FieldType.Boolean => "::boolean",
            MetadataV2FieldType.DateTime => "::timestamptz",
            MetadataV2FieldType.Date => "::date",
            MetadataV2FieldType.Time => "::time",
            _ => null,
        };

        return sortCast is null ? column : $"{column}{sortCast}";
    }

    private MetadataV2Field ResolveFieldDefinition(string fieldName)
    {
        if (fieldName.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("object_id", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataV2Field { Name = fieldName, Type = MetadataV2FieldType.BigInteger };
        }

        return _resource.SchemaFields.FirstOrDefault(candidate =>
                candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Field '{fieldName}' was not found on resource '{_resource.Metadata.Name}'.");
    }

    private Feature ReadFeature(NpgsqlDataReader reader)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        var attributesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        var attributesDictionary = _dictionaryPool.Get();

        try
        {
            // codeql[cs/useless-assignment-to-local] -- the value is consumed by the following span or dictionary operation despite the conservative analysis.
            var deserialized = string.IsNullOrWhiteSpace(attributesJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize(
                    attributesJson,
                    FeatureAttributesJsonContext.Default.DictionaryStringObject) ?? new Dictionary<string, object?>();

            foreach (var (key, value) in deserialized)
            {
                attributesDictionary[key] = value is JsonElement element
                    ? JsonElementConverter.ConvertToScalar(element)
                    : value;
            }

            attributesDictionary[FieldNames.ObjectId] = id;
            if (reader.FieldCount > 3)
            {
                for (var i = 3; i < reader.FieldCount; i++)
                {
                    var fieldName = reader.GetName(i);
                    if (fieldName.Equals(FeatureQueryEncoding.InternalDistanceColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        attributesDictionary[FeatureQueryEncoding.PublicDistanceColumn] = reader.IsDBNull(i)
                            ? null
                            : reader.GetFieldValue<double>(i);
                    }
                }
            }

            return Feature.Create(id, geometry, attributesDictionary.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            attributesDictionary.Clear();
            _dictionaryPool.Return(attributesDictionary);
        }
    }

    private async Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = await ResolveBoundConnectionStringAsync().ConfigureAwait(false);
        if (connectionString == null)
        {
            // Return the provider's lease intact: for a gated/secure provider the lease's
            // wrapper (e.g. SemaphoreReleasingConnection) releases the QueryConcurrencyGate
            // slot on disposal. Down-casting to NpgsqlConnection here would drop that wrapper
            // and permanently leak the gate slot on every read (#1978).
            return await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // The bound-string connection owns itself: disposing the lease disposes the connection.
        return new NpgsqlConnectionLease(connection, connection);
    }

    private async Task<string?> ResolveBoundConnectionStringAsync()
    {
        if (_connection == null)
        {
            return null;
        }

        // Memoized: a KMS/Data-Protection-backed decrypt would otherwise run twice per
        // read (count + select) on the hot path. Benign race — duplicate resolution is
        // idempotent and the binding is immutable for the reader instance lifetime.
        if (_resolvedBoundConnectionString is { } cached)
        {
            return cached;
        }

        if (!_connection.IsEncrypted && !string.IsNullOrWhiteSpace(_connection.ConnectionString))
        {
            return _resolvedBoundConnectionString = _connection.ConnectionString;
        }

        if (_connection.EncryptedConnectionString.Length > 0 && _connectionEncryptionService != null)
        {
            var decrypted = await _connectionEncryptionService
                .DecryptConnectionStringAsync(_connection.EncryptedConnectionString, _connection.EncryptionKeyVersion)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(decrypted))
            {
                return _resolvedBoundConnectionString = decrypted;
            }
        }

        // No bound connection string could be resolved. Decide whether to fall back to the
        // default catalog connection (the same source render + OGC reads use) or to fail
        // loudly. The single-DB / default-connection deployment is the common case: a layer
        // is bound to a placeholder connection that carries no distinct connection-string
        // material at all, which means "read from the default database". Falling back there
        // is correct and restores the pre-#1662 behavior (the resolver returned null, and
        // OpenConnectionAsync opened the default connection).
        //
        // When the binding *does* carry connection-string material that we simply cannot
        // unlock (encrypted bytes with no/failed decryption service, or an external secret
        // reference), the resource genuinely points at a specific, distinct source. Falling
        // back would silently query a same-named table in the wrong database — a tenant /
        // data-source isolation hazard — so fail loudly instead.
        if (ShouldFailInsteadOfDefaultFallback(_connection, _connectionEncryptionService))
        {
            throw new InvalidOperationException(
                $"Storage-mapped table '{_qualifiedTableName}' is bound to data connection '{_connection.Id}' "
                + "but no usable connection string could be resolved for it. "
                + (_connection.IsEncrypted && _connection.EncryptedConnectionString.Length > 0 && _connectionEncryptionService is null
                    ? "The connection is encrypted and no connection encryption service is registered."
                    : "The connection has encrypted or externally referenced credentials that could not be decrypted."));
        }

        // An empty/whitespace connection ID is the "use the default connection" sentinel
        // (the pre-#1662 behavior). When such a binding also carries leftover credential
        // material we could not unlock — e.g. stale encrypted bytes that won't decrypt with
        // the current master key — we still fall back to the default connection, but log a
        // Warning so a genuine misconfiguration (an empty ID that was meant to be a real,
        // distinct source) remains visible in operations.
        if (string.IsNullOrWhiteSpace(_connection.Id)
            && (_connection.EncryptedConnectionString.Length > 0 || !string.IsNullOrWhiteSpace(_connection.SecretRef)))
        {
            LogEmptyIdFallbackPastUnresolvedCredentials(_logger, _qualifiedTableName);
            return null;
        }

        // Empty/placeholder binding — use the default connection (single-DB deployment).
        LogDefaultConnectionFallback(_logger, _qualifiedTableName, _connection.Id);
        return null;
    }

    [LoggerMessage(
        EventId = 7110,
        Level = LogLevel.Warning,
        Message = "Storage-mapped table {Table} is bound to data connection '{ConnectionId}' which carries no "
            + "connection-string material; falling back to the default database connection.")]
    private static partial void LogDefaultConnectionFallback(ILogger logger, string table, string connectionId);

    [LoggerMessage(
        EventId = 7111,
        Level = LogLevel.Warning,
        Message = "Storage-mapped table {Table} is bound to a data connection with an empty ID (the "
            + "default-connection sentinel) that still carries unresolved encrypted or externally "
            + "referenced credentials. Falling back to the default database connection; if this binding "
            + "was meant to target a distinct source, give it a non-empty connection ID.")]
    private static partial void LogEmptyIdFallbackPastUnresolvedCredentials(ILogger logger, string table);

    /// <summary>
    /// Decides whether an unresolved bound connection represents a genuine misconfiguration
    /// (a layer pinned to a distinct source whose credentials we cannot unlock) that must fail
    /// loudly, versus an empty/placeholder binding that means "use the default connection".
    /// </summary>
    /// <remarks>
    /// Only reached when no plaintext string was present and no encrypted string could be
    /// decrypted. An empty/whitespace connection ID is the "use the default connection"
    /// sentinel: such a binding always falls back to the default database regardless of any
    /// leftover (and unresolvable) credential material, because it never identified a distinct
    /// source. A binding with a *non-empty* ID and encrypted bytes (regardless of whether a
    /// decryption service is available) or an external secret reference is pointing at a
    /// specific source, so we must not silently read from the default database. A binding with
    /// none of that material is a placeholder for the default connection.
    /// </remarks>
    internal static bool ShouldFailInsteadOfDefaultFallback(
        DataConnection connection,
        IConnectionEncryptionService? connectionEncryptionService)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(connection.Id))
        {
            // Empty/whitespace ID is the default-connection sentinel — it never named a
            // distinct source, so fall back to the default database even if stale encrypted
            // bytes or a secret reference linger on the record. (ResolveBoundConnectionStringAsync
            // logs a Warning for that case so a real misconfiguration stays visible.)
            return false;
        }

        if (connection.EncryptedConnectionString.Length > 0)
        {
            // Encrypted credentials exist but could not be turned into a usable string
            // (no service, or decryption returned empty) — a real, distinct source we
            // cannot reach. Fail rather than fall back to the wrong database.
            return true;
        }

        if (!string.IsNullOrWhiteSpace(connection.SecretRef))
        {
            // Credentials live in an external secret store the runtime did not resolve.
            return true;
        }

        // No connection-string material of any kind: an empty/placeholder binding standing
        // in for the default connection. Safe to fall back.
        return false;
    }

    private static NpgsqlCommand CreateReadCommand(NpgsqlConnection connection, SqlBuilder sql)
    {
        var command = PostgresSqlSafety.CreateReadCommand(connection, sql.ToString());
        foreach (var parameter in sql.Parameters)
        {
            command.Parameters.AddWithValue(NormalizeParameterValue(parameter));
        }

        return command;
    }

    private static object NormalizeParameterValue(object? value)
    {
        return value switch
        {
            null => DBNull.Value,
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            // DateTimeOffset.UtcDateTime yields a Kind=Unspecified DateTime, which Npgsql
            // refuses to bind to a timestamptz column (ArgumentException -> HTTP 500). Force
            // a UTC kind so temporal STAC/OGC filters bind correctly.
            DateTime dateTime => DateTime.SpecifyKind(
                dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime,
                DateTimeKind.Utc),
            _ => value
        };
    }

    private static object? ConvertStatisticsValue(object value)
    {
        return value switch
        {
            decimal decimalValue => Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture),
            long longValue => longValue,
            int intValue => (long)intValue,
            double doubleValue => doubleValue,
            float floatValue => (double)floatValue,
            string stringValue => stringValue,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(
                dateTime,
                dateTime.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dateTime.Kind)),
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            Array array => array,
            _ => value.ToString()
        };
    }

    private static string QuoteQualifiedTableName(FeatureStorageMapping mapping)
    {
        var table = ValidateAndQuoteIdentifier(mapping.TableName);
        if (string.IsNullOrWhiteSpace(mapping.SchemaName))
        {
            return table;
        }

        return $"{ValidateAndQuoteIdentifier(mapping.SchemaName!)}.{table}";
    }

    private static string ValidateAndQuoteIdentifier(string identifier)
    {
        var sanitized = identifier.Trim();
        if (!IsSafeIdentifier(sanitized))
        {
            throw new InvalidOperationException($"Invalid PostGIS identifier '{identifier}'.");
        }

        return $"\"{sanitized}\"";
    }

    private static bool IsSafeIdentifier(string identifier)
        => SchemaSearchPath.IsValidIdentifier(identifier);

    private static string NormalizeOperator(string operatorValue)
    {
        return operatorValue.Trim().ToUpperInvariant() switch
        {
            "NOT LIKE" => "NOT LIKE",
            "LIKE" => "LIKE",
            ">=" => ">=",
            "<=" => "<=",
            "!=" => "!=",
            "<>" => "!=",
            "=" => "=",
            ">" => ">",
            "<" => "<",
            _ => throw new ArgumentException(UnsupportedWhereClauseMessage)
        };
    }

    private static object ParseValueToken(string valueToken, bool forceText)
    {
        if (valueToken.StartsWith('\'') && valueToken.EndsWith('\''))
        {
            return UnescapeSqlString(valueToken);
        }

        if (!forceText && decimal.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        return valueToken;
    }

    private static string UnescapeSqlString(string valueToken)
    {
        var content = valueToken[1..^1];
        return content.Replace("''", "'", StringComparison.Ordinal);
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private string BuildDistanceSelectExpression(FeatureQuery query, SqlBuilder sql)
    {
        var spatialFilter = query.SpatialFilter;
        if (!spatialFilter.HasValue ||
            !spatialFilter.Value.ReturnDistance ||
            _geometryColumn == null)
        {
            return string.Empty;
        }

        // `returnDistance` is spec-compliant for the general layer /query operation, not
        // just KNN (esriSpatialRelNearestNeighbor): whenever a spatial filter geometry is
        // supplied, each returned feature carries its geodesic distance (meters) from that
        // query geometry under the `distance` attribute. The distance expression is the
        // same ST_Distance computation the KNN ordering uses; it works for every spatial
        // relationship because it only needs a valid filter geometry.
        return $", {BuildNearestNeighborDistanceExpression(query, sql)} AS {FeatureQueryEncoding.InternalDistanceColumn}";
    }

    private string BuildNearestNeighborOrderExpression(FeatureQuery query, SqlBuilder sql)
    {
        if (_geometryColumn == null)
        {
            throw new ArgumentException($"Resource '{_resource.Metadata.Name}' does not define a geometry column.");
        }

        var geometryColumn = $"{_geometryColumn}::geometry";
        var filterGeometry = BuildFilterGeometryExpression(query.SpatialFilter!.Value, sql);
        return ShouldUseGeodesicNearestNeighbor()
            ? BuildGeodesicDistanceExpression(geometryColumn, filterGeometry)
            : $"{geometryColumn} <-> {filterGeometry}";
    }

    private string BuildNearestNeighborDistanceExpression(FeatureQuery query, SqlBuilder sql)
    {
        var geometryColumn = $"{_geometryColumn}::geometry";
        var filterGeometry = BuildFilterGeometryExpression(query.SpatialFilter!.Value, sql);
        return ShouldUseGeodesicNearestNeighbor()
            ? BuildGeodesicDistanceExpression(geometryColumn, filterGeometry)
            : $"ST_Distance({geometryColumn}, {filterGeometry})";
    }

    private string BuildGeodesicDistanceExpression(string geometryColumn, string filterGeometry)
    {
        var storageSrid = _storageSrid;
        var geographyColumn = ToWgs84Geography(geometryColumn, storageSrid);
        var geographyFilter = ToWgs84Geography(filterGeometry, storageSrid);
        return $"ST_Distance({geographyColumn}, {geographyFilter})";
    }

    private bool ShouldUseGeodesicNearestNeighbor()
    {
        // The curated canonical geographic-SRID list is the primary source of truth. Until the
        // classifier unification (#2732) replaces this heuristic, add a safety net: any SRID in
        // the EPSG geographic 2D range (4000-4999) that is not already on the allowlist is still a
        // lat/lon degree CRS (e.g. 4674 SIRGAS 2000, 4490 CGCS2000), so planar degree KNN would
        // silently produce meaningless "distances". Route those through the same geodesic path as
        // 4326. The narrow 4xxx-3D/geocentric exceptions (4978/4979) are out of scope for 2D KNN.
        return DistanceConversions.IsGeographicSrid(_storageSrid)
            || IsUnlistedGeographicSridRange(_storageSrid);
    }

    private static bool IsUnlistedGeographicSridRange(int srid)
        => srid is >= 4000 and <= 4999 && !DistanceConversions.IsGeographicSrid(srid);

    private static string ToWgs84Geography(string geometryExpression, int srid)
        => srid == SpatialReference.WGS84.Wkid
            ? $"{geometryExpression}::geography"
            : $"ST_Transform({geometryExpression}, {SpatialReference.WGS84.Wkid})::geography";

    private static bool IsNearestNeighborQuery(FeatureQuery query)
        => query.SpatialFilter.HasValue &&
           query.SpatialFilter.Value.SpatialRelationship == SpatialRelationship.NearestNeighbor;

    private static List<string> SplitOnAnd(string whereClause)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < whereClause.Length; i++)
        {
            var c = whereClause[i];
            if (c == '\'')
            {
                inQuotes = !inQuotes;
                current.Append(c);
                continue;
            }

            if (!inQuotes && IsAndTokenAt(whereClause, i))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += 2;
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static List<string> SplitValueTokens(string values)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value == '\'' && inQuotes && index + 1 < values.Length && values[index + 1] == '\'')
            {
                current.Append("''");
                index++;
            }
            else if (value == '\'')
            {
                inQuotes = !inQuotes;
                current.Append(value);
            }
            else if (value == ',' && !inQuotes)
            {
                tokens.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(value);
            }
        }

        tokens.Add(current.ToString().Trim());
        return tokens;
    }

    private static bool IsAndTokenAt(string whereClause, int index)
    {
        if (index + 3 > whereClause.Length)
        {
            return false;
        }

        var candidate = whereClause.Substring(index, 3);
        if (!candidate.Equals("AND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var previous = index > 0 ? whereClause[index - 1] : ' ';
        var next = index + 3 < whereClause.Length ? whereClause[index + 3] : ' ';
        return !IsIdentifierChar(previous) && !IsIdentifierChar(next);
    }

    private static bool IsIdentifierChar(char value)
        => char.IsLetterOrDigit(value) || value == '_';

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>NOT\s+LIKE|LIKE|>=|<=|!=|<>|=|>|<)\s*(?<value>'(?:''|[^'])*'|-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IN\s*\((?<values>(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?)(?:\s*,\s*(?:'(?:''|[^'])*'|-?\d+(?:\.\d+)?))*)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InRegex();

    [GeneratedRegex(
        @"^(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s+IS\s+(?<not>NOT\s+)?NULL$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullCheckRegex();

    [GeneratedRegex(
        @"^(?:1\s*=\s*1|TRUE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrueLiteralRegex();

    [GeneratedRegex(
        @"@p(?<index>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SqlFilterParameterRegex();

    [GeneratedRegex(
        @"(?:""attributes""|attributes)\s*->>\s*'(?<field>(?:''|[^'])+)'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributeTextAccessRegex();

    [GeneratedRegex(
        @"""(?<identifier>(?:[^""]|"""")+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuotedIdentifierRegex();

    private sealed class SqlBuilder
    {
        private readonly StringBuilder _text = new();
        private readonly List<object?> _parameters = [];

        public IReadOnlyList<object?> Parameters => _parameters;

        public bool TextContainsWhere => _text.ToString().Contains(" WHERE ", StringComparison.OrdinalIgnoreCase);

        public void Append(string value) => _text.Append(value);

        public void Append(IFormatProvider provider, string value)
            => _text.Append(value);

        public string AddParameter(object? value)
        {
            _parameters.Add(value);
            return "$" + _parameters.Count.ToString(CultureInfo.InvariantCulture);
        }

        public override string ToString() => _text.ToString();
    }
}
