// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Models;

public sealed class ApiErrorResponse
{
    [JsonPropertyName("error")]
    public required GeoServicesError Error { get; init; }
}

public sealed class GeoServicesError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("details")]
    public string[]? Details { get; init; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }
}
