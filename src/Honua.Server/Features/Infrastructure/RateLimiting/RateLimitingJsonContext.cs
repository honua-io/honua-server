// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.RateLimiting;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RateLimitExceededResponse))]
internal sealed partial class RateLimitingJsonContext : JsonSerializerContext
{
}

internal sealed record RateLimitExceededResponse
{
    public required string Error { get; init; }

    public required string Message { get; init; }

    public required RateLimitExceededDetails Details { get; init; }
}

internal sealed record RateLimitExceededDetails
{
    public required int Limit { get; init; }

    [JsonPropertyName("window_reset")]
    public required long WindowReset { get; init; }
}
