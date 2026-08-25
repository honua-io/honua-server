// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Endpoint-facing Studio authorization seam. Keeps the policy decision and its mandatory
/// audit record together so handlers cannot accidentally authorize without auditing.
/// Lives in shared infrastructure (not the Studio slice) because multiple feature slices
/// adapt to it — the Studio lifecycle endpoints and the saved-map collaboration surface
/// (honua-server#2999) both authorize Studio-owned resources through this seam — and the
/// vertical-slice rule forbids one feature referencing another feature's types directly.
/// </summary>
internal sealed class StudioEndpointAuthorization(
    IStudioAuthorizationService authorizationService,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    private readonly IStudioAuthorizationService _authorizationService = authorizationService;
    private readonly IAuditLog _auditLog = auditLog;
    private readonly TimeProvider _timeProvider = timeProvider;

    public bool IsEndUserAuthorizationEnabled => _authorizationService.IsEndUserAuthorizationEnabled;

    public bool IsAdmin(ClaimsPrincipal principal) => _authorizationService.IsAdmin(principal);

    public string? ResolveCallerId(ClaimsPrincipal principal) => _authorizationService.ResolveCallerId(principal);

    public async Task<StudioAuthorizationDecision> AuthorizeAsync(
        HttpContext context,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string resourceType,
        string? resourceId,
        bool isPubliclyReadable = false)
    {
        var callerId = ResolveCallerId(context.User);
        var decision = await _authorizationService.AuthorizeAsync(
            context.User,
            callerId,
            operation,
            resourceOwnerId,
            isPubliclyReadable,
            resourceId,
            context.RequestAborted).ConfigureAwait(false);

        await StudioAuthorizationAudit.RecordDecisionAsync(
            context,
            _auditLog,
            _timeProvider,
            operation,
            resourceType,
            resourceId,
            decision).ConfigureAwait(false);

        return decision;
    }

    public async Task<StudioAuthorizationDecision> DenyAsync(
        HttpContext context,
        StudioAuthorizationOperation operation,
        string resourceType,
        string? resourceId,
        string code,
        string reason)
    {
        var decision = StudioAuthorizationDecision.Deny(code, reason);
        await StudioAuthorizationAudit.RecordDecisionAsync(
            context,
            _auditLog,
            _timeProvider,
            operation,
            resourceType,
            resourceId,
            decision).ConfigureAwait(false);
        return decision;
    }
}
