// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Studio.Models;

/// <summary>A map-local appearance edit against an observed draft generation.</summary>
public sealed record UpdateStudioDraftStyleRequest
{
    /// <summary>Required optimistic concurrency generation.</summary>
    public long? Generation { get; init; }

    /// <summary>Resolved MapLibre appearance, with unchanged authorized sources and assets.</summary>
    public JsonElement MapSpec { get; init; }
}

/// <summary>Initializes server-resolved styling for the current composition.</summary>
public sealed record InitializeStudioDraftStyleRequest
{
    /// <summary>Required optimistic concurrency generation.</summary>
    public long? Generation { get; init; }
}
