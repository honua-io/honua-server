// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

// A policy denial short-circuits before audit middleware, so this handler records it, except a Studio lifecycle denial that already emits its own event.
internal sealed class HonuaAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private const string StudioProblemType = "https://honua.io/problems/studio";
    private const string EndUserModeDisabledDetail = "Studio package lifecycle operations require the admin role.";
    private const string InteractivePrincipalRequiredDetail =
        "Studio AI proxy operations require an interactive user session or the admin role.";

    // Bounds the recorded policy reason; audit details are read by operators and exported to
    // line-oriented SIEM sinks.
    private const int MaxDenialCodeLength = 128;
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        var denialCode = ResolvePolicyDenialCode(policy, authorizeResult);
        if (denialCode is not null)
        {
            await RecordPolicyDenialAuditAsync(context, denialCode).ConfigureAwait(false);
        }
        else if (authorizeResult.Challenged || authorizeResult.Forbidden)
        {
            await RecordAuthorizationOutcomeAsync(context, authorizeResult).ConfigureAwait(false);
        }

        var denialDetail = denialCode switch
        {
            StudioAuthorizationService.EndUserModeDisabledCode => EndUserModeDisabledDetail,
            StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode => InteractivePrincipalRequiredDetail,
            _ => null,
        };
        if (authorizeResult.Forbidden && denialDetail is not null)
        {
            await ProblemDetailsHelpers.CreateProblem(
                context,
                StudioProblemType,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                denialDetail,
                denialCode!).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Forbidden &&
            policy.Requirements.Any(static requirement => requirement is AdminApproveRequirement) &&
            HasFailureReason(authorizeResult, AdminApproveAuthorizationHandler.MissingGrantCode))
        {
            await ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status403Forbidden,
                $"The '{AdminApiKeyPermission.ApproveGrant}' grant is required to approve or reject proposals.")
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }

    private static string? ResolvePolicyDenialCode(
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!policy.Requirements.Any(static requirement => requirement is StudioLifecycleRequirement))
        {
            return null;
        }

        // Authentication challenges are policy denials too, but retain the existing 401
        // challenge response. Give them the same stable code the endpoint authorization
        // service uses when no authenticated caller can be resolved.
        if (authorizeResult.Challenged)
        {
            return StudioAuthorizationService.AuthenticationRequiredCode;
        }

        if (!authorizeResult.Forbidden)
        {
            return null;
        }

        if (HasFailureReason(authorizeResult, StudioAuthorizationService.EndUserModeDisabledCode))
        {
            return StudioAuthorizationService.EndUserModeDisabledCode;
        }

        if (HasFailureReason(authorizeResult, StudioLifecycleAuthorizationHandler.ScopedAdminPermissionDeniedCode))
        {
            return StudioLifecycleAuthorizationHandler.ScopedAdminPermissionDeniedCode;
        }

        if (HasFailureReason(authorizeResult, StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode))
        {
            return StudioAiProxyAuthorizationHandler.InteractivePrincipalRequiredCode;
        }

        // Future requirements added to the Studio policy must not silently create a new
        // unaudited short-circuit. Preserve their normal response while recording one stable
        // generic policy-denial code until a more specific reason is introduced.
        return StudioLifecycleAuthorizationHandler.PolicyDeniedCode;
    }

    private static bool HasFailureReason(
        PolicyAuthorizationResult authorizeResult,
        string code)
        => authorizeResult.AuthorizationFailure?.FailureReasons.Any(reason =>
            string.Equals(reason.Message, code, StringComparison.Ordinal)) == true;

    private static Task RecordPolicyDenialAuditAsync(HttpContext context, string code)
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return Task.CompletedTask;
        }

        var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var auditEvent = new AuditEvent
        {
            Timestamp = timeProvider.GetUtcNow(),
            EventType = AuditEventType.Authorization,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = "studio",
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = "studio.lifecycle",
            Outcome = AuditOutcome.Denied,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = $"{{\"code\":\"{code}\"}}",
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    private static async Task RecordAuthorizationOutcomeAsync(
        HttpContext context,
        PolicyAuthorizationResult authorizeResult)
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        // A domain seam that already recorded this request's denial owns the record.
        if (AuditContextResolver.IsAuthorizationFailureAudited(context))
        {
            return;
        }

        var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var status = authorizeResult.Forbidden
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;

        var auditEvent = HttpAuditEventFactory.CreateAuthOutcomeEvent(
            context,
            status,
            timeProvider.GetUtcNow(),
            ResolveGenericDenialCode(authorizeResult),
            // UseAuthorization runs before lineage attestation. A rejected request
            // never reaches that middleware, so its lineage headers are untrusted.
            includeLineage: false);

        AuditContextResolver.MarkAuthorizationFailureAudited(context);

        try
        {
            await auditLog.RecordAsync(auditEvent, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // The audit sink owns its own error handling; never let a sink failure change the
            // authorization response the caller receives.
        }
    }

    // Authorization handlers publish their reason as a stable code string. Carrying the first
    // one through makes the record say which requirement refused. It is bounded and stripped of
    // control characters on the way in, because a handler is free to compose its message from
    // request values.
    private static string? ResolveGenericDenialCode(PolicyAuthorizationResult authorizeResult)
    {
        var reason = authorizeResult.AuthorizationFailure?.FailureReasons.FirstOrDefault();
        return string.IsNullOrWhiteSpace(reason?.Message)
            ? null
            : LogValueRedactor.SanitizeForLog(reason.Message, MaxDenialCodeLength);
    }
}
