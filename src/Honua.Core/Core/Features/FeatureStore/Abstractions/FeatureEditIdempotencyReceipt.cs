// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public readonly record struct FeatureEditIdempotencyReceipt
{
    public required string Scope { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required Func<FeatureEditResult, ReadOnlyMemory<byte>> Payload { get; init; }
}

public readonly record struct FeatureEditApplication
{
    public required FeatureEditResult Result { get; init; }

    public bool ReceiptDurable { get; init; }

    public bool SupersededByExistingReceipt { get; init; }
}

public interface IFeatureEditIdempotencyReceipts
{
    ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(string scope, CancellationToken cancellationToken = default);

    async ValueTask<FeatureEditReceiptReplay?> TryReadWithEvidenceAsync(
        string scope, CancellationToken cancellationToken = default)
    {
        var payload = await TryReadAsync(scope, cancellationToken).ConfigureAwait(false);
        return payload is { } bytes ? new FeatureEditReceiptReplay { Payload = bytes } : null;
    }
}
