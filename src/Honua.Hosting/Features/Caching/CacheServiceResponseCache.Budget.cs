// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;

namespace Honua.Infrastructure.Caching;

internal sealed partial class CacheServiceResponseCache
{
    private readonly object _budgetLock = new();
    private readonly Dictionary<string, ResponseReservation> _reservations = new(StringComparer.Ordinal);
    private readonly SortedSet<ResponseReservation> _expirationOrder = new(Comparer<ResponseReservation>.Create((left, right) =>
    {
        var expiration = left.ExpiresAt.CompareTo(right.ExpiresAt);
        return expiration != 0 ? expiration : StringComparer.Ordinal.Compare(left.BudgetKey, right.BudgetKey);
    }));
    private long _reservedBytes;

    private void CompleteWrite(ResponseReservation? reservation, TimeSpan expiration)
    {
        if (reservation is null)
        {
            return;
        }

        lock (_budgetLock)
        {
            reservation.Writes.Pending--;
            reservation.Writes.HasPayload = true;
            reservation.Writes.CompletedBytes = Math.Max(reservation.Writes.CompletedBytes, reservation.RequestedBytes);
            if (_reservations.TryGetValue(reservation.BudgetKey, out var current) &&
                ReferenceEquals(current.Writes, reservation.Writes))
            {
                ReleaseExcessBytes(current);
                _expirationOrder.Remove(current);
                var expiresAt = DateTimeOffset.UtcNow.Add(expiration);
                current.ExpiresAt = expiresAt > current.ExpiresAt ? expiresAt : current.ExpiresAt;
                _expirationOrder.Add(current);
            }
        }
    }

    private void FailWrite(ResponseReservation reservation)
    {
        lock (_budgetLock)
        {
            reservation.Writes.Pending--;
            if (_reservations.TryGetValue(reservation.BudgetKey, out var current) &&
                ReferenceEquals(current.Writes, reservation.Writes))
            {
                if (reservation.Writes.Pending == 0 && !reservation.Writes.HasPayload)
                {
                    RemoveReservation(current);
                }
                else
                {
                    ReleaseExcessBytes(current);
                }
            }
        }
    }

    private void ReleaseExcessBytes(ResponseReservation reservation)
    {
        if (reservation.Writes.Pending == 0 && reservation.Writes.CompletedBytes > 0)
        {
            // Overlapping backend writes can finish in either order, so retain their largest successful payload.
            _reservedBytes -= reservation.Bytes - reservation.Writes.CompletedBytes;
            reservation.Bytes = reservation.Writes.CompletedBytes;
        }
    }

    private ResponseReservation[] BeginReclamation(string[] namespaces)
    {
        var scope = CurrentScopePrefix();
        lock (_budgetLock)
        {
            var obsolete = _reservations.Values.Where(entry => entry.ScopePrefix == scope &&
                entry.Writes.Pending == 0 && !entry.Writes.Reclaiming &&
                GetNamespaceKeysForCacheKey(entry.StorageKey).Intersect(namespaces, StringComparer.Ordinal).Any()).ToArray();
            foreach (var reservation in obsolete)
            {
                // Prevent a bound-key replacement from landing after physical eviction.
                reservation.Writes.Reclaiming = true;
            }

            return obsolete;
        }
    }

    private void EndReclamation(ResponseReservation[] reservations)
    {
        lock (_budgetLock)
        {
            foreach (var reservation in reservations)
            {
                reservation.Writes.Reclaiming = false;
            }
        }
    }

    private ResponseReservation? TryReserve(string storageKey, int payloadBytes, TimeSpan expiration)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = CurrentScopePrefix();
        var budgetKey = scope + storageKey;
        var bytes = payloadBytes + ((long)budgetKey.Length * sizeof(char)) + ReservationOverheadBytes;
        lock (_budgetLock)
        {
            PruneExpiredReservations(now);
            _reservations.TryGetValue(budgetKey, out var current);
            if (current?.Writes.Reclaiming == true)
            {
                return null;
            }

            var retainedBytes = _reservedBytes - (current?.Bytes ?? 0);
            if ((current is null && _reservations.Count >= _maxEntries) || bytes > _maxBytes - retainedBytes)
            {
                return null;
            }

            if (current is not null)
            {
                RemoveReservation(current);
            }

            var expiresAt = RetainedExpiration(now.Add(expiration), current);

            var reservation = new ResponseReservation(budgetKey, Math.Max(bytes, current?.Bytes ?? 0), expiresAt)
            {
                StorageKey = storageKey,
                ScopePrefix = scope,
                RequestedBytes = bytes,
                Writes = current?.Writes ?? new ResponseWrites()
            };
            return AddReservation(reservation);
        }
    }

    private static DateTimeOffset RetainedExpiration(DateTimeOffset expiresAt, ResponseReservation? current)
        => current is not null && current.ExpiresAt > expiresAt ? current.ExpiresAt : expiresAt;

    private ResponseReservation AddReservation(ResponseReservation reservation)
    {
        if (reservation.Writes.Pending == 0)
        {
            reservation.Writes.CompletedBytes = 0;
        }

        reservation.Writes.Pending++;
        _reservations[reservation.BudgetKey] = reservation;
        _expirationOrder.Add(reservation);
        _reservedBytes += reservation.Bytes;
        return reservation;
    }

    private ResponseReservation? BeginRemoval(string storageKey)
    {
        var budgetKey = CurrentScopePrefix() + storageKey;
        lock (_budgetLock)
        {
            if (_reservations.TryGetValue(budgetKey, out var current) && current.Writes.Pending == 0 && !current.Writes.Reclaiming)
            {
                current.Writes.Reclaiming = true;
                return current;
            }

            return null;
        }
    }

    private void ReleaseReservation(ResponseReservation expected)
    {
        lock (_budgetLock)
        {
            if (_reservations.TryGetValue(expected.BudgetKey, out var current) && ReferenceEquals(current, expected))
            {
                RemoveReservation(current);
            }
        }
    }

    private void PruneExpiredReservations(DateTimeOffset now)
    {
        foreach (var expired in _expirationOrder.TakeWhile(entry => entry.ExpiresAt <= now)
                     .Where(entry => entry.Writes.Pending == 0 && !entry.Writes.Reclaiming).ToArray())
        {
            RemoveReservation(expired);
        }
    }

    private void RemoveReservation(ResponseReservation reservation)
    {
        _reservations.Remove(reservation.BudgetKey);
        _expirationOrder.Remove(reservation);
        _reservedBytes -= reservation.Bytes;
    }

    private static string CurrentScopePrefix() => CacheScopeKeys.GetScopePrefix(SchemaContext.AmbientCurrentSchema);

    private sealed class ResponseReservation(string budgetKey, long bytes, DateTimeOffset expiresAt)
    {
        public string BudgetKey { get; } = budgetKey;
        public long Bytes { get; set; } = bytes;
        public required long RequestedBytes { get; init; }
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public required string StorageKey { get; init; }
        public required string ScopePrefix { get; init; }
        public required ResponseWrites Writes { get; init; }
    }

    private sealed class ResponseWrites
    {
        public int Pending { get; set; }
        public long CompletedBytes { get; set; }
        public bool HasPayload { get; set; }
        public bool Reclaiming { get; set; }
    }
}
