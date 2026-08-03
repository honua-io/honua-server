// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Geoprocessing.Raster.Functions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Raster;

/// <summary>PostgreSQL implementation of immutable named raster-function versions.</summary>
internal sealed class PostgresRasterFunctionDefinitionStore : IRasterFunctionDefinitionStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _definitionsTable;
    private readonly string _versionsTable;

    public PostgresRasterFunctionDefinitionStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _definitionsTable = SchemaSearchPath.QualifyTable("raster_function_definitions", schemaName);
        _versionsTable = SchemaSearchPath.QualifyTable("raster_function_definition_versions", schemaName);
    }

    public async Task<RasterFunctionDefinitionCreateResult> CreateVersionAsync(
        RasterFunctionDefinitionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        RasterFunctionDefinitionStoreValidation.Validate(request);
        var definitionJson = RasterFunctionJson.Normalize(request.Definition);
        var definitionHash = RasterFunctionJson.ComputeSha256(request.Definition);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await lease.Connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureNameAsync(lease.Connection, transaction, request, cancellationToken).ConfigureAwait(false);
            var currentVersion = await LockNameAsync(
                lease.Connection,
                transaction,
                request.TenantId,
                request.Name,
                cancellationToken).ConfigureAwait(false);

            var replay = await FindIdempotencyKeyAsync(
                lease.Connection,
                transaction,
                request.TenantId,
                request.Name,
                request.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                var isSameRequest = replay.ExpectedPreviousVersion == request.ExpectedLatestVersion
                    && string.Equals(replay.Version.DefinitionHash, definitionHash, StringComparison.Ordinal)
                    && string.Equals(RasterFunctionJson.Normalize(replay.Version.Definition), definitionJson, StringComparison.Ordinal);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new RasterFunctionDefinitionCreateResult
                {
                    Status = isSameRequest
                        ? RasterFunctionDefinitionCreateStatus.Replayed
                        : RasterFunctionDefinitionCreateStatus.IdempotencyConflict,
                    DefinitionVersion = isSameRequest ? replay.Version : null,
                    CurrentVersion = currentVersion,
                };
            }

            if (currentVersion != request.ExpectedLatestVersion)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new RasterFunctionDefinitionCreateResult
                {
                    Status = RasterFunctionDefinitionCreateStatus.VersionConflict,
                    CurrentVersion = currentVersion,
                };
            }

            var nextVersion = checked(currentVersion + 1);
            var createdAt = await InsertVersionAsync(
                lease.Connection,
                transaction,
                request,
                nextVersion,
                definitionHash,
                definitionJson,
                cancellationToken).ConfigureAwait(false);
            await AdvanceNameAsync(
                lease.Connection,
                transaction,
                request.TenantId,
                request.Name,
                currentVersion,
                nextVersion,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

            return new RasterFunctionDefinitionCreateResult
            {
                Status = RasterFunctionDefinitionCreateStatus.Created,
                DefinitionVersion = new RasterFunctionDefinitionVersion
                {
                    TenantId = request.TenantId,
                    Name = request.Name,
                    Version = nextVersion,
                    DefinitionHash = definitionHash,
                    Definition = request.Definition,
                    CreatedBy = request.CreatedBy,
                    CreatedAt = createdAt,
                },
                CurrentVersion = nextVersion,
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RasterFunctionDefinitionVersion?> GetVersionAsync(
        RasterFunctionDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        RasterFunctionDefinitionStoreValidation.Validate(reference);
        var sql = $"""
            SELECT tenant_id, function_name, version, definition_hash, definition_body,
                   contract_version, created_by, created_at
            FROM {_versionsTable}
            WHERE tenant_id = @tenant_id
              AND function_name = @function_name
              AND version = @version
              AND definition_hash = @definition_hash
            """;

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, lease.Connection);
        AddIdentityParameters(command, reference.TenantId, reference.Name);
        command.Parameters.AddWithValue("@version", reference.Version);
        command.Parameters.AddWithValue("@definition_hash", reference.DefinitionHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAndVerifyVersion(reader)
            : null;
    }

    private async Task EnsureNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterFunctionDefinitionCreateRequest request,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_definitionsTable} (tenant_id, function_name, current_version, created_by)
            VALUES (@tenant_id, @function_name, 0, @created_by)
            ON CONFLICT (tenant_id, function_name) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, request.TenantId, request.Name);
        command.Parameters.AddWithValue("@created_by", (object?)request.CreatedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> LockNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT current_version
            FROM {_definitionsTable}
            WHERE tenant_id = @tenant_id AND function_name = @function_name
            FOR UPDATE
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, tenantId, name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ReplayRow?> FindIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string name,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT tenant_id, function_name, version, definition_hash, definition_body,
                   contract_version, created_by, created_at, expected_previous_version
            FROM {_versionsTable}
            WHERE tenant_id = @tenant_id
              AND function_name = @function_name
              AND idempotency_key = @idempotency_key
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, tenantId, name);
        command.Parameters.AddWithValue("@idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReplayRow(ReadAndVerifyVersion(reader), reader.GetInt32(8));
    }

    private async Task<DateTimeOffset> InsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RasterFunctionDefinitionCreateRequest request,
        int version,
        string definitionHash,
        string definitionJson,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_versionsTable}
                (tenant_id, function_name, version, definition_hash, contract_version,
                 definition_body, expected_previous_version, idempotency_key, created_by)
            VALUES
                (@tenant_id, @function_name, @version, @definition_hash, @contract_version,
                 @definition_body, @expected_previous_version, @idempotency_key, @created_by)
            RETURNING created_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, request.TenantId, request.Name);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@definition_hash", definitionHash);
        command.Parameters.AddWithValue("@contract_version", request.Definition.ContractVersion);
        command.Parameters.Add(new NpgsqlParameter("@definition_body", NpgsqlDbType.Jsonb) { Value = definitionJson });
        command.Parameters.AddWithValue("@expected_previous_version", request.ExpectedLatestVersion);
        command.Parameters.AddWithValue("@idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("@created_by", (object?)request.CreatedBy ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Raster function version insert did not return a creation time.");
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task AdvanceNameAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string name,
        int expectedVersion,
        int nextVersion,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {_definitionsTable}
            SET current_version = @next_version, updated_at = NOW()
            WHERE tenant_id = @tenant_id
              AND function_name = @function_name
              AND current_version = @expected_version
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdentityParameters(command, tenantId, name);
        command.Parameters.AddWithValue("@expected_version", expectedVersion);
        command.Parameters.AddWithValue("@next_version", nextVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("Raster function version changed while its serialization lock was held.");
        }
    }

    private static RasterFunctionDefinitionVersion ReadAndVerifyVersion(NpgsqlDataReader reader)
    {
        var definitionHash = reader.GetString(3);
        var definition = RasterFunctionJson.Deserialize(reader.GetString(4));
        var computedHash = RasterFunctionJson.ComputeSha256(definition);
        var validation = RasterFunctionValidator.Validate(definition);
        if (reader.GetInt32(5) != definition.ContractVersion
            || !validation.IsValid
            || !string.Equals(definitionHash, computedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored raster function definition failed its integrity check.");
        }

        return new RasterFunctionDefinitionVersion
        {
            TenantId = reader.GetString(0),
            Name = reader.GetString(1),
            Version = reader.GetInt32(2),
            DefinitionHash = definitionHash,
            Definition = definition,
            CreatedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        };
    }

    private static void AddIdentityParameters(NpgsqlCommand command, string tenantId, string name)
    {
        command.Parameters.AddWithValue("@tenant_id", tenantId);
        command.Parameters.AddWithValue("@function_name", name);
    }

    private sealed record ReplayRow(RasterFunctionDefinitionVersion Version, int ExpectedPreviousVersion);
}
