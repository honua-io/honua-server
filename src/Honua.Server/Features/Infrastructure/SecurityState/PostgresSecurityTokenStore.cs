// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.ControlPlane;
using Honua.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.SecurityState;

internal sealed class PostgresSecurityTokenStore(JobStoreDatabase database) : ISecurityTokenStore, IExpiredRowSweep
{
    private const string LiveToken = "kind = @kind AND token_hash = @token_hash AND expires_at > now()";

    private readonly string _tokens = database.Table("security_tokens");

    public Task StoreAsync(SecurityTokenKey key, ReadOnlyMemory<byte> payload, TimeSpan lifetime, CancellationToken cancellationToken)
        => WriteAsync(
            key,
            new TokenWrite(payload, "now() + @lifetime", new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = lifetime }),
            cancellationToken);

    public Task RestoreAsync(SecurityTokenKey key, StoredSecurityToken token, CancellationToken cancellationToken)
        => WriteAsync(
            key,
            new TokenWrite(
                token.Payload,
                "@expires_at",
                new NpgsqlParameter("expires_at", NpgsqlDbType.TimestampTz) { Value = token.ExpiresAt.ToUniversalTime() }),
            cancellationToken);

    public Task<StoredSecurityToken?> FindAsync(SecurityTokenKey key, CancellationToken cancellationToken)
        => ReadAsync(
            $"SELECT payload, expires_at FROM {_tokens} WHERE {LiveToken}",
            key,
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<StoredSecurityToken?> ConsumeAsync(SecurityTokenKey key, CancellationToken cancellationToken)
        => ReadAsync(
            $"DELETE FROM {_tokens} WHERE {LiveToken} RETURNING payload, expires_at",
            key,
            StatementRepeat.NotAfterSend,
            cancellationToken);

    public Task RevokeAsync(SecurityTokenKey key, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"DELETE FROM {_tokens} WHERE kind = @kind AND token_hash = @token_hash",
                    connection);
                AddKeyParameters(command, key);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT kind, token_hash FROM {_tokens}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_tokens} AS stored USING expired
                    WHERE stored.kind = expired.kind AND stored.token_hash = expired.token_hash
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task WriteAsync(SecurityTokenKey key, TokenWrite write, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_tokens} (kind, token_hash, payload, expires_at)
                    VALUES (@kind, @token_hash, @payload, {write.ExpiresAtExpression})
                    ON CONFLICT (kind, token_hash) DO UPDATE SET payload = EXCLUDED.payload, expires_at = EXCLUDED.expires_at
                    """,
                    connection);
                AddKeyParameters(command, key);
                command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea) { Value = write.Payload });
                command.Parameters.Add(write.ExpiryParameter.Clone());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private Task<StoredSecurityToken?> ReadAsync(
        string statement,
        SecurityTokenKey key,
        StatementRepeat repeat,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                AddKeyParameters(command, key);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false)
                    ? new StoredSecurityToken(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<DateTimeOffset>(1))
                    : null;
            },
            repeat,
            cancellationToken);

    private static void AddKeyParameters(NpgsqlCommand command, SecurityTokenKey key)
    {
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = key.Kind.ToString() });
        command.Parameters.Add(new NpgsqlParameter("token_hash", NpgsqlDbType.Bytea)
        {
            Value = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value))
        });
    }

    private sealed record TokenWrite(ReadOnlyMemory<byte> Payload, string ExpiresAtExpression, NpgsqlParameter ExpiryParameter);
}
