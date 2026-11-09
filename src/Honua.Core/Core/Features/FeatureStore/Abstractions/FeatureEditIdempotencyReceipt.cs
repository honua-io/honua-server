// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Opaque idempotency receipt that must commit in the same database transaction as the edit
/// it describes. The scope is a server-built hash, never the client's raw idempotency key.
/// </summary>
public readonly record struct FeatureEditIdempotencyReceipt
{
    /// <summary>Gets the hashed retry scope.</summary>
    public required string Scope { get; init; }

    /// <summary>Gets when a stored receipt stops being replayable.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Gets the factory that builds the replay payload from the staged edit result.
    /// Called only when the batch will commit, and before that commit.
    /// </summary>
    public required Func<FeatureEditResult, ReadOnlyMemory<byte>> Payload { get; init; }
}

/// <summary>
/// An applied edit batch and whether its idempotency receipt committed with it.
/// </summary>
public readonly record struct FeatureEditApplication
{
    /// <summary>Gets the edit result. When <see cref="SupersededByExistingReceipt"/> is set, this result did not commit.</summary>
    public required FeatureEditResult Result { get; init; }

    /// <summary>
    /// Gets whether <see cref="FeatureEditIdempotencyReceipt"/> was committed in the same
    /// transaction as the edit. False means the caller must not treat the edit as
    /// acknowledged to other replicas until some other receipt is durable.
    /// </summary>
    public bool ReceiptDurable { get; init; }

    /// <summary>
    /// Gets whether another committed receipt already owns this scope, and this attempt was
    /// rolled back. The caller must replay the stored receipt instead of <see cref="Result"/>.
    /// </summary>
    public bool SupersededByExistingReceipt { get; init; }
}

/// <summary>
/// Reads a committed edit idempotency receipt. Implementations must not observe a receipt
/// that has not committed.
/// </summary>
public interface IFeatureEditIdempotencyReceipts
{
    /// <summary>
    /// Returns the committed replay payload for <paramref name="scope"/>, or
    /// <see langword="null"/> when none is visible.
    /// </summary>
    /// <param name="scope">Hashed retry scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(string scope, CancellationToken cancellationToken = default);
}
