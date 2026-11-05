// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Security;

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

    public static string? ResolveRequestTenantId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequestServices.GetService<ITenantContext>()?.TenantId;
    }

    public TenantScopeFilter? CreateTenantScopeFilter(ClaimsPrincipal principal)
        => _authorizationService.CreateTenantScopeFilter(principal);

    public async Task<StudioAuthorizationDecision> AuthorizeAsync(
        HttpContext context,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        string? resourceTenantId,
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
            resourceTenantId,
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
