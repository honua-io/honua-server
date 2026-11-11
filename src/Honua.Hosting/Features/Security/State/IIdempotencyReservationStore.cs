// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Security;

internal readonly record struct IdempotencyReservation(string ScopeKey, string ReservationToken);

// A scope is reserved by one request at a time. A reservation that is still pending has no receipt.
internal interface IIdempotencyReservationStore
{
    Task<byte[]?> FindReceiptAsync(string scopeKey, CancellationToken cancellationToken);

    Task<IdempotencyReservation?> TryReserveAsync(string scopeKey, TimeSpan reservationWindow, CancellationToken cancellationToken);

    // False when the reservation was lost to another request that has reserved or completed the scope.
    Task<bool> CompleteAsync(
        IdempotencyReservation reservation,
        ReadOnlyMemory<byte> receipt,
        TimeSpan receiptRetention,
        CancellationToken cancellationToken);

    Task ReleaseAsync(IdempotencyReservation reservation, CancellationToken cancellationToken);
}
