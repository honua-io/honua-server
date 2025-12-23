// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Represents a GeoServices-compatible API error response
/// Follows GeoServices REST error response format: { "error": { "code": number, "message": "string", "details": [] } }
/// </summary>
public sealed class ApiErrorResponse
{
    public required GeoServicesError Error { get; init; }
}

/// <summary>
/// Represents the error details in GeoServices-compatible format
/// </summary>
public sealed class GeoServicesError
{
    public required int Code { get; init; }

    public required string Message { get; init; }

    public string[]? Details { get; init; }
}
