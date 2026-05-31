// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Contracts;

namespace Honua.Infrastructure.Models;

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

    /// <summary>
    /// Correlation identifier for tracing the request.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Timestamp for when the error occurred (UTC, ISO 8601).
    /// </summary>
    public string? Timestamp { get; init; }

    /// <summary>
    /// Field-level validation errors (RFC 7807 extension member). Emitted only
    /// by validation problems; null/omitted for all other problem responses.
    /// </summary>
    public IReadOnlyList<FieldValidationError>? Errors { get; init; }
}
