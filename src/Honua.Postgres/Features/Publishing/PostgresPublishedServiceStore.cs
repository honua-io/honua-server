// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Publishing;

/// <summary>
/// Durable Postgres store for canonical published-service records.
/// </summary>
internal sealed class PostgresPublishedServiceStore : IPublishedServiceStore
{
    private const string Columns =
        "service_id, intent_id, source_kind, source_id, target_kind, status, document, published_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;

    public PostgresPublishedServiceStore(
        NpgsqlDataSource dataSource,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _table = SchemaSearchPath.QualifyTable("promotion_published_services", schemaName);
    }

    public async Task<bool> TryCreateAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var sql = $"""
            INSERT INTO {_table} ({Columns})
            VALUES (@service_id, @intent_id, @source_kind, @source_id, @target_kind, @status,
                    @document, @published_at, @updated_at)
            ON CONFLICT (service_id) DO NOTHING
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, service);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<PublishedServiceRecord?> GetAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT document FROM {_table} WHERE service_id = @service_id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@service_id", NpgsqlDbType.Text, serviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Deserialize(reader.GetString(0))
            : null;
    }

    public async Task SetAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var sql = $"""
            INSERT INTO {_table} ({Columns})
            VALUES (@service_id, @intent_id, @source_kind, @source_id, @target_kind, @status,
                    @document, @published_at, @updated_at)
            ON CONFLICT (service_id) DO UPDATE SET
                intent_id = EXCLUDED.intent_id,
                source_kind = EXCLUDED.source_kind,
                source_id = EXCLUDED.source_id,
                target_kind = EXCLUDED.target_kind,
                status = EXCLUDED.status,
                document = EXCLUDED.document,
                published_at = EXCLUDED.published_at,
                updated_at = EXCLUDED.updated_at
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, service);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PublishedServiceRecord>> ListActiveAsync(
        CancellationToken cancellationToken = default)
        => ListAsync(
            "status <> @decommissioned",
            command => command.Parameters.AddWithValue(
                "@decommissioned",
                NpgsqlDbType.Text,
                PublishedServiceStatus.Decommissioned.ToString()),
            cancellationToken);

    public Task<IReadOnlyList<PublishedServiceRecord>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
        => ListAsync(
            "source_id = @source_id",
            command => command.Parameters.AddWithValue("@source_id", NpgsqlDbType.Text, sourceId),
            cancellationToken);

    private async Task<IReadOnlyList<PublishedServiceRecord>> ListAsync(
        string predicate,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT document FROM {_table} WHERE {predicate} ORDER BY updated_at DESC, service_id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        addParameters(command);
        var services = new List<PublishedServiceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            services.Add(Deserialize(reader.GetString(0)));
        }

        return services;
    }

    private static void AddParameters(NpgsqlCommand command, PublishedServiceRecord service)
    {
        var document = JsonSerializer.Serialize(
            service,
            PublishingJsonContext.Default.PublishedServiceRecord);
        command.Parameters.AddWithValue("@service_id", NpgsqlDbType.Text, service.ServiceId);
        command.Parameters.AddWithValue("@intent_id", NpgsqlDbType.Text, service.IntentId);
        command.Parameters.AddWithValue("@source_kind", NpgsqlDbType.Text, service.SourceKind.ToString());
        command.Parameters.AddWithValue("@source_id", NpgsqlDbType.Text, service.SourceId);
        command.Parameters.AddWithValue("@target_kind", NpgsqlDbType.Text, service.TargetKind.ToString());
        command.Parameters.AddWithValue("@status", NpgsqlDbType.Text, service.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb) { Value = document });
        command.Parameters.AddWithValue("@published_at", NpgsqlDbType.TimestampTz, service.PublishedAt);
        command.Parameters.AddWithValue("@updated_at", NpgsqlDbType.TimestampTz, service.UpdatedAt);
    }

    private static PublishedServiceRecord Deserialize(string document)
        => JsonSerializer.Deserialize(
               document,
               PublishingJsonContext.Default.PublishedServiceRecord)
           ?? throw new InvalidDataException("Published-service document was empty.");
}
