// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Server-authored decision describing whether the requesting principal may
/// access a top-level Console route.
/// </summary>
public sealed record ConsoleNavigationEntitlement
{
    /// <summary>
    /// Canonical Console route key (e.g. <c>studio</c>, <c>operate</c>, <c>admin</c>).
    /// </summary>
    [JsonPropertyName("routeKey")]
    public required string RouteKey { get; init; }

    /// <summary>
    /// True when the principal is permitted to enter the route.
    /// </summary>
    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }

    /// <summary>
    /// Machine-readable code explaining a denial (e.g. <c>no-license</c>,
    /// <c>insufficient-role</c>). Omitted when <see cref="Allowed"/> is true.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
