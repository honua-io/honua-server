// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>Validates a complete style against the server's supported MapLibre styling surface.</summary>
public interface IMapStyleValidator
{
    /// <summary>Returns a diagnostic instead of normalizing away unsupported style content.</summary>
    bool TryValidate(JsonElement style, out string? error);
}
