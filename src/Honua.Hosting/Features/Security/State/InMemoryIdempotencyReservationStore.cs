// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

// For a host with no system database, which is one process by definition, and for tests.
internal sealed class InMemoryIdempotencyReservationStore(TimeProvider? timeProvider = null) : IIdempotencyReservationStore
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<byte[]?> FindReceiptAsync(string scopeKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetLive(scopeKey, out var entry) ? entry.Receipt : null);
        }
    }

    public Task<IdempotencyReservation?> TryReserveAsync(string scopeKey, TimeSpan reservationWindow, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (TryGetLive(scopeKey, out _))
            {
                return Task.FromResult<IdempotencyReservation?>(null);
            }

            var reservation = new IdempotencyReservation(scopeKey, Guid.NewGuid().ToString("N"));
            _entries[scopeKey] = new Entry(reservation.ReservationToken, Receipt: null, _timeProvider.GetUtcNow().Add(reservationWindow));
            return Task.FromResult<IdempotencyReservation?>(reservation);
        }
    }

    public Task<bool> CompleteAsync(
        IdempotencyReservation reservation,
        ReadOnlyMemory<byte> receipt,
        TimeSpan receiptRetention,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (TryGetLive(reservation.ScopeKey, out var current)
                && (current.Receipt is not null || current.ReservationToken != reservation.ReservationToken))
            {
                return Task.FromResult(false);
            }

            _entries[reservation.ScopeKey] = new Entry(
                reservation.ReservationToken, receipt.ToArray(), _timeProvider.GetUtcNow().Add(receiptRetention));
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(IdempotencyReservation reservation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reservation.ScopeKey, out var current)
                && current.Receipt is null
                && current.ReservationToken == reservation.ReservationToken)
            {
                _entries.Remove(reservation.ScopeKey);
            }

            return Task.CompletedTask;
        }
    }

    private bool TryGetLive(string scopeKey, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry)
    {
        if (_entries.TryGetValue(scopeKey, out entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return true;
        }

        _entries.Remove(scopeKey);
        entry = null;
        return false;
    }

    private sealed record Entry(string ReservationToken, byte[]? Receipt, DateTimeOffset ExpiresAt);
}
