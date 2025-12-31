// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Models;

/// <summary>
/// RFC 7807 problem details response payload.
/// </summary>
internal sealed record ProblemDetailsResponse
{
    /// <summary>
    /// Problem type identifier.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Short, human-readable summary of the problem.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// HTTP status code for the response.
    /// </summary>
    public required int Status { get; init; }

    /// <summary>
    /// Detailed description of the problem.
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>
    /// URI reference that identifies the specific occurrence of the problem.
    /// </summary>
    public string? Instance { get; init; }
}
