// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Admin;

internal static class LayerNamePattern
{
    internal static string? ForIlike(string? nameQuery)
    {
        if (string.IsNullOrWhiteSpace(nameQuery))
        {
            return null;
        }

        var escaped = nameQuery.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }
}

internal sealed partial class PostgreSqlLayerPublishingService
{
    private const string PublishedLayerPageSql = $"""
        WITH matched AS (
            SELECT DISTINCT l.layer_id
            FROM honua.layers l
            INNER JOIN honua.service_layers sl
                ON sl.layer_id = l.layer_id
            WHERE sl.service_name = @serviceName
              AND (@namePattern IS NULL OR l.layer_name ILIKE @namePattern ESCAPE '\')
        ),
        totals AS (
            SELECT COUNT(*)::int AS total_count FROM matched
        ),
        page AS (
            SELECT layer_id
            FROM matched
            ORDER BY layer_id
            LIMIT @pageSize
            OFFSET @offset
        )
        SELECT
            {PublishedLayerSelect},
            totals.total_count
        FROM totals
        LEFT JOIN page ON true
        LEFT JOIN honua.layers l
            ON l.layer_id = page.layer_id
        LEFT JOIN honua.layer_fields f
            ON f.layer_id = l.layer_id
        GROUP BY
            {PublishedLayerColumns},
            totals.total_count
        ORDER BY l.layer_id;
        """;

    public async Task<PublishedLayerPage> ListPublishedLayersPageAsync(
        string connectionString,
        PublishedLayerPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (CatalogListLimits.DescribeOutOfRange(query.Page, query.PageSize, query.NameQuery) is { } rejection)
        {
            throw new ArgumentException(rejection);
        }

        var normalizedService = NormalizeServiceName(query.ServiceName);
        await ValidateTenantAccessAsync(normalizedService, null, null, cancellationToken).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (layers, total) = await ReadPublishedLayerPageAsync(
            connection, normalizedService, query, cancellationToken).ConfigureAwait(false);
        var items = layers.Count == 0
            ? layers
            : await ApplyListedLayerScopeAsync(
                new ListedLayerScope(connection, normalizedService, layers, query.ConnectionId),
                cancellationToken).ConfigureAwait(false);
        return new PublishedLayerPage
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            Total = total,
        };
    }

    private static async Task<(List<PublishedLayerSummary> Layers, int Total)> ReadPublishedLayerPageAsync(
        NpgsqlConnection connection,
        string normalizedService,
        PublishedLayerPageQuery query,
        CancellationToken cancellationToken)
    {
        var layers = new List<PublishedLayerSummary>();
        var total = 0;
        var sawTotal = false;
        await using var command = new NpgsqlCommand(PublishedLayerPageSql, connection);
        command.Parameters.AddWithValue("@serviceName", normalizedService);
        command.Parameters.Add(new NpgsqlParameter("@namePattern", NpgsqlDbType.Text)
        {
            Value = (object?)LayerNamePattern.ForIlike(query.NameQuery) ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("@pageSize", query.PageSize);
        command.Parameters.AddWithValue("@offset", (query.Page - 1) * query.PageSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!sawTotal)
            {
                total = reader.GetInt32(10);
                sawTotal = true;
            }

            if (!reader.IsDBNull(0))
            {
                layers.Add(ReadPublishedLayer(reader, normalizedService));
            }
        }

        return (layers, total);
    }
}
