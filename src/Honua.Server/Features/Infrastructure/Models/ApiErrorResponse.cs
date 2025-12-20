// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// Represents an API error response with standard error information
/// </summary>
public sealed class ApiErrorResponse
{
    public required string Error { get; init; }

    public string[]? Details { get; init; }
}
