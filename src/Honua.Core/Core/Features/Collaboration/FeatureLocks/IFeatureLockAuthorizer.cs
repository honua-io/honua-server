// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Collaboration.FeatureLocks;

public enum FeatureLockAuthorizationStatus
{
    Authorized,

    RequiresAuthentication,

    Forbidden
}

public sealed record FeatureLockAuthorizationResult
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

// The shipped authorizer denies every claim, so no lease is granted until a deployment registers its own.
public interface IFeatureLockAuthorizer
{
    ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
        string mapId,
        FeatureRef feature,
        ClaimsPrincipal principal,
        string operation,
        CancellationToken cancellationToken);
}
