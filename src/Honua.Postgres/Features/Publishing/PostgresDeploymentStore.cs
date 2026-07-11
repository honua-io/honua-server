// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Publishing;

/// <summary>
/// Durable Postgres store for canonical deployment lifecycle records.
/// </summary>
internal sealed class PostgresDeploymentStore : IDeploymentStore
{
    private const string Columns =
        "deployment_id, source_kind, source_id, target_id, status, document, created_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;

    public PostgresDeploymentStore(
        NpgsqlDataSource dataSource,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _table = SchemaSearchPath.QualifyTable("promotion_deployments", schemaName);
    }

    public async Task<bool> TryCreateAsync(
        Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var sql = $"""
            INSERT INTO {_table} ({Columns})
            VALUES (@deployment_id, @source_kind, @source_id, @target_id, @status,
                    @document, @created_at, @updated_at)
            ON CONFLICT (deployment_id) DO NOTHING
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, deployment);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<Deployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT document FROM {_table} WHERE deployment_id = @deployment_id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@deployment_id", NpgsqlDbType.Text, deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Deserialize(reader.GetString(0))
            : null;
    }

    public async Task SetAsync(
        Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var sql = $"""
            INSERT INTO {_table} ({Columns})
            VALUES (@deployment_id, @source_kind, @source_id, @target_id, @status,
                    @document, @created_at, @updated_at)
            ON CONFLICT (deployment_id) DO UPDATE SET
                source_kind = EXCLUDED.source_kind,
                source_id = EXCLUDED.source_id,
                target_id = EXCLUDED.target_id,
                status = EXCLUDED.status,
                document = EXCLUDED.document,
                created_at = EXCLUDED.created_at,
                updated_at = EXCLUDED.updated_at
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, deployment);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Deployment>> ListActiveAsync(
        CancellationToken cancellationToken = default)
        => ListAsync(
            "status <> @retired AND status <> @superseded",
            command =>
            {
                command.Parameters.AddWithValue("@retired", NpgsqlDbType.Text, DeploymentStatus.Retired.ToString());
                command.Parameters.AddWithValue("@superseded", NpgsqlDbType.Text, DeploymentStatus.Superseded.ToString());
            },
            cancellationToken);

    public Task<IReadOnlyList<Deployment>> ListBySourceAsync(
        DeploymentSourceKind sourceKind,
        string sourceId,
        CancellationToken cancellationToken = default)
        => ListAsync(
            "source_kind = @source_kind AND source_id = @source_id",
            command =>
            {
                command.Parameters.AddWithValue("@source_kind", NpgsqlDbType.Text, sourceKind.ToString());
                command.Parameters.AddWithValue("@source_id", NpgsqlDbType.Text, sourceId);
            },
            cancellationToken);

    public Task<IReadOnlyList<Deployment>> ListByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default)
        => ListAsync(
            "target_id = @target_id",
            command => command.Parameters.AddWithValue("@target_id", NpgsqlDbType.Text, targetId),
            cancellationToken);

    private async Task<IReadOnlyList<Deployment>> ListAsync(
        string predicate,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT document FROM {_table} WHERE {predicate} ORDER BY updated_at DESC, deployment_id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        addParameters(command);
        var deployments = new List<Deployment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            deployments.Add(Deserialize(reader.GetString(0)));
        }

        return deployments;
    }

    private static void AddParameters(NpgsqlCommand command, Deployment deployment)
    {
        var document = JsonSerializer.Serialize(
            deployment,
            DeploymentJsonContext.Default.Deployment);
        command.Parameters.AddWithValue("@deployment_id", NpgsqlDbType.Text, deployment.DeploymentId);
        command.Parameters.AddWithValue("@source_kind", NpgsqlDbType.Text, deployment.Source.Kind.ToString());
        command.Parameters.AddWithValue("@source_id", NpgsqlDbType.Text, deployment.Source.SourceId);
        command.Parameters.AddWithValue("@target_id", NpgsqlDbType.Text, deployment.Target.TargetId);
        command.Parameters.AddWithValue("@status", NpgsqlDbType.Text, deployment.Status.ToString());
        command.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb) { Value = document });
        command.Parameters.AddWithValue("@created_at", NpgsqlDbType.TimestampTz, deployment.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", NpgsqlDbType.TimestampTz, deployment.UpdatedAt);
    }

    private static Deployment Deserialize(string document)
        => JsonSerializer.Deserialize(
               document,
               DeploymentJsonContext.Default.Deployment)
           ?? throw new InvalidDataException("Deployment document was empty.");
}
