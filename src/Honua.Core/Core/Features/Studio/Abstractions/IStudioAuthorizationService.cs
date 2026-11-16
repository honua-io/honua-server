// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy;

namespace Honua.Core.Features.Studio.Abstractions;

public enum StudioAuthorizationOperation
{
    CreateDraft,

    ReadDraft,

    UpdateDraft,

    DeleteDraft,

    ValidateDraft,

    CreateVersion,

    ListOwn,

    ReadContentItem,

    ReopenVersion,

    PublishRequest,

    Rollback,

    Generate,
}

public sealed record StudioAuthorizationDecision
{
    public required bool IsAllowed { get; init; }

    public bool IsElevated { get; init; }

    public string? Code { get; init; }

    public string? Reason { get; init; }

    public static StudioAuthorizationDecision Allow(bool elevated = false) => new()
    {
        IsAllowed = true,
        IsElevated = elevated,
    };

    public static StudioAuthorizationDecision Deny(string code, string reason, bool elevated = false) => new()
    {
        IsAllowed = false,
        IsElevated = elevated,
        Code = code,
        Reason = reason,
    };
}

public interface IStudioAuthorizationService
{
    bool IsEndUserAuthorizationEnabled { get; }

    bool IsAdmin(ClaimsPrincipal principal);

    string? ResolveCallerId(ClaimsPrincipal principal);

    TenantScopeFilter? CreateTenantScopeFilter(ClaimsPrincipal principal);

    Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string? resourceTenantId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default);
}
