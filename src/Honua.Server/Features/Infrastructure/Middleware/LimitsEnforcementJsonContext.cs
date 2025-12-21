// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Middleware;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LimitsErrorResponse))]
internal sealed partial class LimitsEnforcementJsonContext : JsonSerializerContext
{
}

internal sealed class LimitsErrorResponse
{
    public required LimitsErrorDetails Error { get; init; }
}

internal sealed class LimitsErrorDetails
{
    public int Code { get; init; }

    public required string Message { get; init; }

    public required string[] Details { get; init; }
}
