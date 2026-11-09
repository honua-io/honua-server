// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

public sealed record ConsoleProvenanceRef
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }
}
