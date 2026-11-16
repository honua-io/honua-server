// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed record CanonicalSpecDocument
{
    public required string GrammarVersion { get; init; }

    public required string ProcessFamilyVersion { get; init; }

    public string? SpecId { get; init; }

    public required IReadOnlyList<CanonicalSpecNode> Nodes { get; init; }
}

public sealed record CanonicalSpecNode
{
    public required string Id { get; init; }

    public required SpecResourceKind Kind { get; init; }

    public string? Op { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? CanonicalFragment { get; init; }

    public IReadOnlyDictionary<string, string> SourcePins { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Nondeterministic { get; init; }
}

public enum SpecResourceKind
{
    Compute,

    Report,

    Dataset,

    Service,

    App
}
