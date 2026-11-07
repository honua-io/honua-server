// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Console.Models;

internal sealed record PublishedContentMetadata
{
    public required string ServiceId { get; init; }
    public string? ResourceId { get; init; }
    public string? Url { get; init; }
    public required string Access { get; init; }
    public string? FolderPath { get; init; }
}
