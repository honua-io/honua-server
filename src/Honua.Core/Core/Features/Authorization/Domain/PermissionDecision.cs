// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public enum PermissionResult
{
    NoMatchingGrant = 0,

    Allow = 1,

    RequiresAuthentication = 2,
}

public readonly record struct PermissionDecision
{
    private PermissionDecision(PermissionResult result, PermissionGrant? matchedGrant)
    {
        Result = result;
        MatchedGrant = matchedGrant;
    }

    public PermissionResult Result { get; }

    public PermissionGrant? MatchedGrant { get; }

    public bool IsAllowed => Result == PermissionResult.Allow;

    public bool HasNoMatchingGrant => Result == PermissionResult.NoMatchingGrant;

    public static PermissionDecision Allow(PermissionGrant matchedGrant)
        => new(PermissionResult.Allow, matchedGrant);

    public static PermissionDecision NoMatch()
        => new(PermissionResult.NoMatchingGrant, null);

    public static PermissionDecision RequiresAuthentication()
        => new(PermissionResult.RequiresAuthentication, null);
}
