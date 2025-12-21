// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Represents an Esri-compatible API error response
/// Follows ArcGIS Server error response format: { "error": { "code": number, "message": "string", "details": [] } }
/// </summary>
public sealed class ApiErrorResponse
{
    public required EsriError Error { get; init; }
}

/// <summary>
/// Represents the error details in Esri-compatible format
/// </summary>
public sealed class EsriError
{
    public required int Code { get; init; }

    public required string Message { get; init; }

    public string[]? Details { get; init; }
}
