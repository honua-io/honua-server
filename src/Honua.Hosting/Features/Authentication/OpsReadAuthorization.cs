// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Authorization requirement for the read-only operational-observability surfaces (aggregated
/// operate status, ops-health, findings, and alert reads). It admits either the admin family (full
/// admin or <c>admin:read</c>) or a dedicated read-only <c>ops:</c> credential for safe
/// (GET/HEAD/OPTIONS) requests, while a mutating request (rollback, promote, submit, suppress, …)
/// still requires full admin write.
/// </summary>
/// <remarks>
/// This is the ops-reader half of the split the ops review called for: "no read-only ops role — all
/// ops endpoints share one admin scope, so any credential that can read status can also POST
/// /rollback." A key minted with only an <c>ops:read</c> grant satisfies this requirement on the read
/// surfaces but is denied every mutating ops endpoint — those keep the admin policy, which an
/// ops-reader key (authenticated as a non-admin scoped principal) can never satisfy. The evaluation
/// is delegated to <see cref="AdminApiKeyPermission.IsOpsReadAuthorized"/> so the ops-reader grammar
/// stays a single source of truth alongside the admin grant grammar (no parallel auth mechanism).
/// </remarks>
internal sealed class OpsReadRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="OpsReadRequirement"/> against the principal's admin/ops permission grants and
/// the current request's HTTP method.
/// </summary>
internal sealed class OpsReadAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<OpsReadAuthorizationHandler> logger)
    : AuthorizationHandler<OpsReadRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<OpsReadAuthorizationHandler> _logger = logger;

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OpsReadRequirement requirement)
    {
        // Mirror AdminPermissionAuthorizationHandler: the HTTP method is the behavior-changing input;
        // prefer the resource the middleware supplies and fall back to the ambient accessor.
        var httpContext = context.Resource as HttpContext ?? _httpContextAccessor.HttpContext;
        var method = httpContext?.Request.Method;

        if (AdminApiKeyPermission.IsOpsReadAuthorized(context.User, method))
        {
            context.Succeed(requirement);
        }
        else
        {
            AuthenticationLog.OpsReaderKeyDenied(_logger, method ?? "(unknown)");
            // Leave the requirement unmet (do not Fail): yields a 403 and lets other
            // handlers/requirements report their own outcome, matching the admin handler.
        }

        return Task.CompletedTask;
    }
}
