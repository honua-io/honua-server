// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

public sealed record ConsolePersistenceNotice
{
    public const string InMemoryMode = "in-memory";

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("durable")]
    public bool Durable { get; init; }

    [JsonPropertyName("sharedAcrossReplicas")]
    public bool SharedAcrossReplicas { get; init; }

    public static ConsolePersistenceNotice InMemory { get; } = new()
    {
        Mode = InMemoryMode,
        Durable = false,
        SharedAcrossReplicas = false,
    };
}
