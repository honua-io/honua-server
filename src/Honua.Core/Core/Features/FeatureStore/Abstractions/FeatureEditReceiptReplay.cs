// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public readonly record struct FeatureEditReceiptReplay
{
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public FeatureEditReceiptEvidence? Evidence { get; init; }
}

public sealed record FeatureEditReceiptEvidence
{
    // Each outer entry corresponds to one result; default inner entries mean unavailable.
    public required ImmutableArray<ImmutableArray<FeatureOperationEvidence>> Creates { get; init; }
    public required ImmutableArray<ImmutableArray<FeatureOperationEvidence>> Updates { get; init; }
    public required ImmutableArray<ImmutableArray<FeatureOperationEvidence>> Deletes { get; init; }
}
