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

    /// <summary>
    /// Machine-readable extension code (RFC 7807 extension member), for problem families that
    /// need a stable code beyond <see cref="Type"/>/<see cref="Status"/> for clients to branch
    /// on. Used by the Studio package lifecycle authorization problem
    /// (honua-server#3001, REQ-004; for example <c>studio_authorization/cross_user_denied</c>).
    /// Null/omitted for problem responses that do not set it.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Capability-manifest id this refusal disables (RFC 7807 extension member), for example
    /// <c>jobs.runner</c>. Lets a client join the refusal to
    /// <c>GET /api/v1/capabilities/manifest</c> without parsing prose (honua-release#202).
    /// Null/omitted for problem responses that do not set it.
    /// </summary>
    public string? Capability { get; init; }

    /// <summary>
    /// Identifier of the infrastructure dependency that was not composed (RFC 7807 extension
    /// member), for example <c>redis</c>. Null/omitted for problem responses that do not set it.
    /// </summary>
    public string? MissingDependency { get; init; }

    /// <summary>
    /// Identifier of the entitlement whose absence composed the capability out (RFC 7807
    /// extension member), for example <c>caching.redis</c>. Present instead of
    /// <see cref="MissingDependency"/> when the dependency is deployed but not licensed.
    /// </summary>
    public string? MissingEntitlement { get; init; }

    /// <summary>
    /// Operator-facing remediation sentence (RFC 7807 extension member) telling the caller what
    /// to change to make the capability available. Null/omitted when no remediation is known.
    /// </summary>
    public string? Remediation { get; init; }

    /// <summary>
    /// Documentation reference for <see cref="Remediation"/> (RFC 7807 extension member).
    /// Null/omitted when no reference is known.
    /// </summary>
    public string? RemediationRef { get; init; }
}
