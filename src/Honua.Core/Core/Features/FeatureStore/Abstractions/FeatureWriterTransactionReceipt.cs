// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public readonly record struct FeatureWriterTransactionReceipt
{
    public required string Scope { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }
}
