// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// A one-way pull request to refresh a published service from its upstream source.
/// The source is always authoritative; changes flow from source into the published copy.
/// </summary>
public sealed record RefreshRequest
{
    /// <summary>
    /// Stable identifier for this refresh request.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Identifier of the published service to refresh.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Whether the refresh replaces all content or applies only changes.
    /// </summary>
    public required RefreshScope Scope { get; init; }

    /// <summary>
    /// Operator identity and request metadata.
    /// </summary>
    public OperationAuditInfo Audit { get; init; } = new();

    /// <summary>
    /// When the refresh was requested.
    /// </summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// Creates a new refresh request.
    /// </summary>
    public static RefreshRequest Create(
        string requestId,
        string serviceId,
        RefreshScope scope,
        OperationAuditInfo? audit = null)
        => new()
        {
            RequestId = requestId,
            ServiceId = serviceId,
            Scope = scope,
            Audit = audit ?? new OperationAuditInfo(),
            RequestedAt = DateTimeOffset.UtcNow
        };
}
