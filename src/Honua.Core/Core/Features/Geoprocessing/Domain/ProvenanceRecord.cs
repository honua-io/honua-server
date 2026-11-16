// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ProvenanceRecord
{
    public required IReadOnlyList<ProvenanceSource> Sources { get; init; }

    public required IReadOnlyList<string> ProcessDefinitions { get; init; }

    public IReadOnlyList<string> Assumptions { get; init; } = [];

    public IReadOnlyList<string> ClarificationsAsked { get; init; } = [];

    public IReadOnlyList<string> ClarificationsAnswered { get; init; } = [];

    public DateTimeOffset? ExecutedAt { get; init; }

    public IReadOnlyList<string> GeneratedArtifactIds { get; init; } = [];
}

public sealed record ProvenanceSource
{
    public required string SourceId { get; init; }

    public string? Version { get; init; }

    public string? Description { get; init; }
}
