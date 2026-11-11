// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.ControlPlane;
using Honua.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.SecurityState;

internal sealed class PostgresIdempotencyReservationStore(JobStoreDatabase database)
    : IIdempotencyReservationStore, IExpiredRowSweep
{
    private readonly string _reservations = database.Table("idempotency_reservations");

    public Task<byte[]?> FindReceiptAsync(string scopeKey, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT receipt FROM {_reservations}
                    WHERE scope_hash = @scope_hash AND receipt IS NOT NULL AND expires_at > now()
                    """,
                    connection);
                command.Parameters.Add(ScopeHash(scopeKey));
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as byte[];
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public async Task<IdempotencyReservation?> TryReserveAsync(
        string scopeKey,
        TimeSpan reservationWindow,
        CancellationToken cancellationToken)
    {
        var reservation = new IdempotencyReservation(scopeKey, Guid.NewGuid().ToString("N"));
        var reserved = await database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_reservations} AS held (scope_hash, reservation_token, receipt, expires_at)
                    VALUES (@scope_hash, @reservation_token, NULL, now() + @lifetime)
                    ON CONFLICT (scope_hash) DO UPDATE SET
                        reservation_token = EXCLUDED.reservation_token, receipt = NULL, expires_at = EXCLUDED.expires_at
                    WHERE held.expires_at <= now()
                    """,
                    connection);
                AddReservationParameters(command, reservation, reservationWindow);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);
        return reserved ? reservation : null;
    }

    public Task<bool> CompleteAsync(
        IdempotencyReservation reservation,
        ReadOnlyMemory<byte> receipt,
        TimeSpan receiptRetention,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_reservations} AS held (scope_hash, reservation_token, receipt, expires_at)
                    VALUES (@scope_hash, @reservation_token, @receipt, now() + @lifetime)
                    ON CONFLICT (scope_hash) DO UPDATE SET
                        reservation_token = EXCLUDED.reservation_token, receipt = EXCLUDED.receipt,
                        expires_at = EXCLUDED.expires_at
                    WHERE held.expires_at <= now()
                       OR (held.reservation_token = @reservation_token AND held.receipt IS NULL)
                    """,
                    connection);
                AddReservationParameters(command, reservation, receiptRetention);
                command.Parameters.Add(new NpgsqlParameter("receipt", NpgsqlDbType.Bytea) { Value = receipt });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    public Task ReleaseAsync(IdempotencyReservation reservation, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    DELETE FROM {_reservations}
                    WHERE scope_hash = @scope_hash AND reservation_token = @reservation_token AND receipt IS NULL
                    """,
                    connection);
                command.Parameters.Add(ScopeHash(reservation.ScopeKey));
                command.Parameters.Add(new NpgsqlParameter("reservation_token", NpgsqlDbType.Text) { Value = reservation.ReservationToken });
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
                        SELECT scope_hash FROM {_reservations}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_reservations} AS held USING expired WHERE held.scope_hash = expired.scope_hash
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static void AddReservationParameters(NpgsqlCommand command, IdempotencyReservation reservation, TimeSpan lifetime)
    {
        command.Parameters.Add(ScopeHash(reservation.ScopeKey));
        command.Parameters.Add(new NpgsqlParameter("reservation_token", NpgsqlDbType.Text) { Value = reservation.ReservationToken });
        command.Parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = lifetime });
    }

    private static NpgsqlParameter ScopeHash(string scopeKey)
        => new("scope_hash", NpgsqlDbType.Bytea) { Value = SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey)) };
}
