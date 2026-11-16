// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Durable state record for a named spec resource.
/// </summary>
public sealed record SpecResourceState
{
    /// <summary>Kind of resource.</summary>
    public required SpecResourceKind Kind { get; init; }

    /// <summary>Name (unique within kind).</summary>
    public required string Name { get; init; }

    /// <summary>Semantic version assigned to this entry.</summary>
    public required string Version { get; init; }

    /// <summary>Content hash of the producing compute node.</summary>
    public required string ContentHash { get; init; }

    /// <summary>UTC timestamp the record was produced.</summary>
    public required DateTimeOffset ProducedAt { get; init; }

    /// <summary>Arbitrary metadata attached to the record.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
