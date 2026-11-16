// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ArtifactRef
{
    public required string ArtifactId { get; init; }

    public required ArtifactKind Kind { get; init; }

    public required string Label { get; init; }

    public string? Uri { get; init; }

    public string? ContentType { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
