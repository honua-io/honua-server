// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

public sealed record ConsoleNavigationEntitlement
{
    [JsonPropertyName("routeKey")]
    public required string RouteKey { get; init; }

    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
