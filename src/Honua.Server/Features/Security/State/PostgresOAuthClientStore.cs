// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Security.State;

internal sealed class PostgresOAuthClientStore(JobStoreDatabase database, TimeProvider? timeProvider = null) : IOAuthClientStore
{
    private const string SelectColumns = "record, last_used_at";

    // Minting a client (identifier, secret, display prefix) stays in one place until the in-memory
    // store becomes a test double and the minting moves out of it.
    private readonly InMemoryOAuthClientStore _minting = new(timeProvider);
    private readonly string _clients = database.Table("oauth_clients");

    public async Task<IReadOnlyList<OAuthClientRecord>> ListAsync(CancellationToken cancellationToken)
        => await ReadAsync(
            $"SELECT {SelectColumns} FROM {_clients} ORDER BY created_at, id",
            parameter: null,
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public async Task<OAuthClientCreateResult> CreateAsync(
        OAuthClientRegistration registration,
        CancellationToken cancellationToken)
    {
        var minted = await _minting.CreateAsync(registration, cancellationToken).ConfigureAwait(false);
        var record = minted.Record;
        await database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_clients} (id, client_id, secret_hash, created_at, expires_at, record)
                    VALUES (@id, @client_id, @secret_hash, @created_at, @expires_at, @record)
                    """,
                    connection);
                var parameters = command.Parameters;
                parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = record.Id });
                parameters.Add(new NpgsqlParameter("client_id", NpgsqlDbType.Text) { Value = record.ClientId });
                parameters.Add(new NpgsqlParameter("secret_hash", NpgsqlDbType.Bytea) { Value = (object?)record.SecretHash ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter("created_at", NpgsqlDbType.TimestampTz) { Value = record.CreatedAt.ToUniversalTime() });
                parameters.Add(new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz)
                {
                    Value = record.ExpiresAt.HasValue ? record.ExpiresAt.Value.ToUniversalTime() : DBNull.Value
                });
                parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
                {
                    Value = JsonSerializer.Serialize(record, OAuthClientStoreJsonContext.Default.OAuthClientRecord)
                });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);
        return minted;
    }

    public Task<OAuthClientRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
        => ReadFirstAsync(
            $"SELECT {SelectColumns} FROM {_clients} WHERE id = @id",
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<OAuthClientRecord?> DeleteAsync(Guid id, CancellationToken cancellationToken)
        => ReadFirstAsync(
            $"DELETE FROM {_clients} WHERE id = @id RETURNING {SelectColumns}",
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    // A public client has no secret hash and an expired client has no live row, so neither matches.
    public async Task<OAuthClientRecord?> ValidateSecretAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        var validated = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_clients} SET last_used_at = now()
                    WHERE client_id = @client_id AND secret_hash = @secret_hash
                      AND (expires_at IS NULL OR expires_at > now())
                    RETURNING {SelectColumns}
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("client_id", NpgsqlDbType.Text) { Value = clientId });
                command.Parameters.Add(new NpgsqlParameter("secret_hash", NpgsqlDbType.Bytea)
                {
                    Value = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret))
                });
                return await ReadRecordsAsync(command, token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
        return validated.Count == 0 || validated[0].RevokedAt is not null ? null : validated[0];
    }

    private async Task<OAuthClientRecord?> ReadFirstAsync(
        string statement,
        NpgsqlParameter parameter,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
    {
        var records = await ReadAsync(statement, parameter, repeat, cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    private Task<List<OAuthClientRecord>> ReadAsync(
        string statement,
        NpgsqlParameter? parameter,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                if (parameter is not null)
                {
                    command.Parameters.Add(parameter.Clone());
                }

                return await ReadRecordsAsync(command, token).ConfigureAwait(false);
            },
            repeat,
            cancellationToken);

    private static async Task<List<OAuthClientRecord>> ReadRecordsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var records = new List<OAuthClientRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = JsonSerializer.Deserialize(reader.GetString(0), OAuthClientStoreJsonContext.Default.OAuthClientRecord)
                ?? throw new InvalidDataException("A stored OAuth client record is empty.");
            records.Add(await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? record
                : record with { LastUsedAt = reader.GetFieldValue<DateTimeOffset>(1), UpdatedAt = reader.GetFieldValue<DateTimeOffset>(1) });
        }

        return records;
    }
}
