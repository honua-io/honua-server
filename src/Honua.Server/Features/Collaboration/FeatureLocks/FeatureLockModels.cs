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

    public FeatureRef ToFeatureRef() => new(ServiceName, LayerId, FeatureId);

    public LockHolder ToHolder() => new(HolderId, DisplayName, SessionId, TenantId);

    public TimeSpan ToLeaseDuration() => TimeSpan.FromSeconds(LeaseSeconds);
}

internal enum FeatureLockAuthorizationStatus
{
    Authorized,
    RequiresAuthentication,
    Forbidden
}

internal sealed record FeatureLockAuthorizationResult
{
    private FeatureLockAuthorizationResult(
        FeatureLockAuthorizationStatus status,
        FeatureLockAccessContext access,
        string? detail)
    {
        Status = status;
        Access = access;
        Detail = detail;
    }

    public FeatureLockAuthorizationStatus Status { get; }

    public FeatureLockAccessContext Access { get; }

    public string? Detail { get; }

    public bool Authorized => Status == FeatureLockAuthorizationStatus.Authorized;

    public static FeatureLockAuthorizationResult AllowWrite() =>
        new(FeatureLockAuthorizationStatus.Authorized, FeatureLockAccessContext.AuthorizedWrite, detail: null);

    public static FeatureLockAuthorizationResult RequireAuthentication(string detail) =>
        new(FeatureLockAuthorizationStatus.RequiresAuthentication, FeatureLockAccessContext.Unauthorized, detail);

    public static FeatureLockAuthorizationResult Forbid(string detail) =>
        new(FeatureLockAuthorizationStatus.Forbidden, FeatureLockAccessContext.ReadOnly, detail);
}

internal interface IFeatureLockAuthorizer
{
    ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId,
        FeatureRef feature,
        ClaimsPrincipal principal,
        string operation,
        CancellationToken cancellationToken);
}
