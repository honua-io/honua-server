// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Postgres.Features.EnrichmentCatalog;

/// <summary>
/// Postgres-backed implementation of <see cref="IEnrichmentDatasetCatalogStore"/>
/// (#2280) over the <c>honua.enrichment_datasets</c> registry table provisioned by
/// migration 071. Mirrors the network-dataset registry store: all values are bound
/// as parameters (never interpolated), and schema isolation for test schemas is
/// handled upstream by the connection's <c>search_path</c>.
/// </summary>
internal sealed class PostgresEnrichmentDatasetCatalogStore : IEnrichmentDatasetCatalogStore
{
    private const string SelectColumns =
        "id, title, category, layer_id, geometry_type, join_attributes, default_predicate, " +
        "distance_meters, provenance, attribution, license, minimum_edition, " +
        "created_at, updated_at, created_by, updated_by";

    private readonly IDatabaseSessionFactory _sessionFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresEnrichmentDatasetCatalogStore"/> class.
    /// </summary>
    /// <param name="sessionFactory">Database session factory.</param>
    public PostgresEnrichmentDatasetCatalogStore(IDatabaseSessionFactory sessionFactory)
        => _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnrichmentDatasetRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.enrichment_datasets
            ORDER BY id;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<EnrichmentDatasetRecord>();
        await foreach (var record in session.QueryAsync(sql, MapRecord, parameters: null, cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(record);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<EnrichmentDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        const string sql = $"""
            SELECT {SelectColumns}
            FROM honua.enrichment_datasets
            WHERE id = @id
            LIMIT 1;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await session.QuerySingleOrDefaultAsync(
                sql,
                MapRecord,
                new Dictionary<string, object?> { ["id"] = id },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EnrichmentDatasetRecord> RegisterAsync(
        EnrichmentDatasetRecord dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        const string sql = """
            INSERT INTO honua.enrichment_datasets
                (id, title, category, layer_id, geometry_type, join_attributes, default_predicate,
                 distance_meters, provenance, attribution, license, minimum_edition, created_by, updated_by)
            VALUES
                (@id, @title, @category, @layer_id, @geometry_type, @join_attributes, @default_predicate,
                 @distance_meters, @provenance, @attribution, @license, @minimum_edition, @created_by, @updated_by)
            ON CONFLICT (id) DO NOTHING;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(sql, BuildParameters(dataset), cancellationToken).ConfigureAwait(false);

        if (affected == 0)
        {
            throw new EnrichmentDatasetAlreadyExistsException(dataset.Id);
        }

        var saved = await GetAsync(dataset.Id, cancellationToken).ConfigureAwait(false);
        return saved ?? throw new InvalidOperationException(
            $"Enrichment dataset '{dataset.Id}' was registered but could not be read back.");
    }

    /// <inheritdoc />
    public async Task<EnrichmentDatasetRecord?> UpdateAsync(
        EnrichmentDatasetRecord dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        const string sql = """
            UPDATE honua.enrichment_datasets
            SET title = @title,
                category = @category,
                layer_id = @layer_id,
                geometry_type = @geometry_type,
                join_attributes = @join_attributes,
                default_predicate = @default_predicate,
                distance_meters = @distance_meters,
                provenance = @provenance,
                attribution = @attribution,
                license = @license,
                minimum_edition = @minimum_edition,
                updated_by = @updated_by,
                updated_at = now()
            WHERE id = @id;
            """;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(sql, BuildParameters(dataset), cancellationToken).ConfigureAwait(false);

        if (affected == 0)
        {
            return null;
        }

        return await GetAsync(dataset.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        const string sql = "DELETE FROM honua.enrichment_datasets WHERE id = @id;";

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await session.ExecuteAsync(
                sql,
                new Dictionary<string, object?> { ["id"] = id },
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    private static Dictionary<string, object?> BuildParameters(EnrichmentDatasetRecord dataset) => new()
    {
        ["id"] = dataset.Id,
        ["title"] = dataset.Title,
        ["category"] = dataset.Category,
        ["layer_id"] = dataset.LayerId,
        ["geometry_type"] = dataset.GeometryType,
        ["join_attributes"] = SerializeAttributes(dataset.JoinAttributes),
        ["default_predicate"] = dataset.DefaultPredicate,
        ["distance_meters"] = dataset.DistanceMeters,
        ["provenance"] = dataset.Provenance,
        ["attribution"] = dataset.Attribution,
        ["license"] = dataset.License,
        ["minimum_edition"] = dataset.MinimumEdition.ToString(),
        ["created_by"] = dataset.CreatedBy,
        ["updated_by"] = dataset.UpdatedBy,
    };

    // Attributes are persisted as a comma-separated TEXT column (the field list is
    // small and order-stable) rather than a TEXT[]/JSONB column, keeping the row
    // mapper a plain ordinal read with no provider-specific array handling.
    private static string? SerializeAttributes(IReadOnlyList<string> attributes)
        => attributes.Count == 0 ? null : string.Join(',', attributes);

    private static string[] DeserializeAttributes(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static EnrichmentDatasetRecord MapRecord(IDatabaseRow row) => new(
        row.GetFieldValue<string>(0),
        row.GetFieldValue<string>(1),
        row.GetFieldValue<string>(2),
        row.GetFieldValue<int>(3),
        row.IsNull(4) ? null : row.GetFieldValue<string>(4),
        DeserializeAttributes(row.IsNull(5) ? null : row.GetFieldValue<string>(5)),
        row.GetFieldValue<string>(6),
        row.IsNull(7) ? null : row.GetFieldValue<double>(7),
        row.IsNull(8) ? null : row.GetFieldValue<string>(8),
        row.IsNull(9) ? null : row.GetFieldValue<string>(9),
        row.IsNull(10) ? null : row.GetFieldValue<string>(10),
        ParseEdition(row.GetFieldValue<string>(11)),
        row.GetFieldValue<DateTimeOffset>(12),
        row.GetFieldValue<DateTimeOffset>(13),
        row.IsNull(14) ? null : row.GetFieldValue<string>(14),
        row.IsNull(15) ? null : row.GetFieldValue<string>(15));

    private static HonuaEdition ParseEdition(string value)
        => Enum.TryParse<HonuaEdition>(value, ignoreCase: true, out var edition)
            ? edition
            : HonuaEdition.Pro;
}
