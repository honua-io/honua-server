// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record SourceBinding
{
    public required string SourceId { get; init; }

    public required SourceProtocol Protocol { get; init; }

    public required SourceLocator Locator { get; init; }

    public string? Filter { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record SourceLocator
{
    public required string Url { get; init; }

    public string? ServiceId { get; init; }

    public string? LayerId { get; init; }
}
