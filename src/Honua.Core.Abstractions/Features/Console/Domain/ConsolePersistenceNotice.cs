// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Tells the Console bootstrap whether content, share, and open-data state
/// survives process restart and is shared by every replica.
/// </summary>
public sealed record ConsolePersistenceNotice
{
    /// <summary>Mode string recorded for the process-local stores.</summary>
    public const string InMemoryMode = "in-memory";

    /// <summary>Storage mode, for example <c>in-memory</c>.</summary>
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    /// <summary>True when a restart keeps the stored rows.</summary>
    [JsonPropertyName("durable")]
    public bool Durable { get; init; }

    /// <summary>True when every replica reads and writes the same rows.</summary>
    [JsonPropertyName("sharedAcrossReplicas")]
    public bool SharedAcrossReplicas { get; init; }

    /// <summary>
    /// Notice for the process-local content, share, and open-data stores.
    /// </summary>
    public static ConsolePersistenceNotice InMemory { get; } = new()
    {
        Mode = InMemoryMode,
        Durable = false,
        SharedAcrossReplicas = false,
    };
}
