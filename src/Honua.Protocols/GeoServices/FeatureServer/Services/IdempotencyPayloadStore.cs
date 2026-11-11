// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// Both at-most-once stores share this reservation mechanic: concurrent same-key requests race for
// one reservation, the loser gets no token, and a store failure is reported without failing the request.
internal sealed class IdempotencyPayloadStore(IIdempotencyReservationStore reservations)
{
    internal static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    internal static readonly TimeSpan ReservationWindow = TimeSpan.FromSeconds(60);

    // A lookup failure means the retry is applied again rather than deduplicated.
    public async Task<byte[]?> TryGetAsync(string key, Action<Exception> onStoreUnavailable, CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.FindReceiptAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            onStoreUnavailable(storeFailure);
            return null;
        }
    }

    // A store failure lets the request proceed, so an outage costs at most a duplicate. The token
    // is still returned so the caller's release path is uniform; releasing it is a no-op.
    public async Task<string?> TryReserveAsync(
        string key,
        TimeSpan reservationWindow,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        try
        {
            var reservation = await reservations.TryReserveAsync(key, reservationWindow, cancellationToken).ConfigureAwait(false);
            return reservation?.ReservationToken;
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            onStoreUnavailable(storeFailure);
            return Guid.NewGuid().ToString("N");
        }
    }

    // The receipt is written only when the key is free or still holds this request's reservation, so
    // an expired owner cannot overwrite a newer reservation or receipt.
    public async Task<bool> SetIfAbsentOrOwnedAsync(
        IdempotencyReservation reservation,
        byte[] payload,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reservation.ReservationToken);
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            return await reservations.CompleteAsync(reservation, payload, DedupeWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            onStoreUnavailable(storeFailure);
            return false;
        }
    }

    // Release takes no cancellation token, so a cancelled owner can still free its key, and removes
    // only this request's pending reservation.
    public async Task ReleaseAsync(IdempotencyReservation reservation, Action<Exception> onStoreUnavailable)
    {
        ArgumentException.ThrowIfNullOrEmpty(reservation.ReservationToken);
        try
        {
            await reservations.ReleaseAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException)
        {
            onStoreUnavailable(storeFailure);
        }
    }
}
