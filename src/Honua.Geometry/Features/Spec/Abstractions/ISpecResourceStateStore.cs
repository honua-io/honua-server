// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public sealed record SpecResourceState
{
    public required SpecResourceKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ContentHash { get; init; }

    public required DateTimeOffset ProducedAt { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
