// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Collaboration.FeatureLocks;

namespace Honua.Server.Features.Collaboration.FeatureLocks;

internal sealed record FeatureLockMutationRequest
{
    public required string ServiceName { get; init; }

    public required int LayerId { get; init; }

    public required string FeatureId { get; init; }

    public required string HolderId { get; init; }

    public string? DisplayName { get; init; }

    public string? SessionId { get; init; }

    public string? TenantId { get; init; }

    public int LeaseSeconds { get; init; } = 120;

    // Canonical form on the way in, so a lease claimed with the URL casing the caller
    // happened to use still matches the key every write path evaluates (#4402).
    public FeatureRef ToFeatureRef() => FeatureRef.Canonical(ServiceName, LayerId, FeatureId);

    /// <summary>
    /// Builds the holder, stamping the authenticated principal over anything the request
    /// body carried. The principal is the only unforgeable part of a lease identity: the
    /// holder id, session and tenant are all caller-chosen and are echoed back in conflict
    /// responses, so a second editor could otherwise replay them to be treated as the owner.
    /// </summary>
    public LockHolder ToHolder(ClaimsPrincipal? principal) =>
        new(HolderId, DisplayName, SessionId, TenantId, ResolvePrincipalName(principal));

    private static string? ResolvePrincipalName(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true ? principal.Identity.Name : null;

    public TimeSpan ToLeaseDuration() => TimeSpan.FromSeconds(LeaseSeconds);
}
