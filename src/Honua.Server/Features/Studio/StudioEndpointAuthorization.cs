// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Middleware;

namespace Honua.Server.Features.Studio;

/// <summary>
/// Endpoint-facing Studio authorization seam. Keeps the policy decision and its mandatory
/// audit record together so handlers cannot accidentally authorize without auditing.
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

        if (!decision.IsAllowed || decision.IsElevated)
        {
            await RecordAuditAsync(
                context,
                operation,
                resourceType,
                resourceId,
                decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Denied,
                decision.Code).ConfigureAwait(false);
        }

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
        await RecordAuditAsync(
            context,
            operation,
            resourceType,
            resourceId,
            AuditOutcome.Denied,
            code).ConfigureAwait(false);
        return decision;
    }

    private Task RecordAuditAsync(
        HttpContext context,
        StudioAuthorizationOperation operation,
        string resourceType,
        string? resourceId,
        AuditOutcome outcome,
        string? code)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = _timeProvider.GetUtcNow(),
            EventType = AuditEventType.Authorization,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = $"studio.{ToSnakeCase(operation)}",
            Outcome = outcome,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = code is null ? string.Empty : $"{{\"code\":\"{code}\"}}",
        };

        return _auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    private static string ToSnakeCase(StudioAuthorizationOperation operation) => operation switch
    {
        StudioAuthorizationOperation.CreateDraft => "create_draft",
        StudioAuthorizationOperation.ReadDraft => "read_draft",
        StudioAuthorizationOperation.UpdateDraft => "update_draft",
        StudioAuthorizationOperation.DeleteDraft => "delete_draft",
        StudioAuthorizationOperation.ValidateDraft => "validate_draft",
        StudioAuthorizationOperation.CreateVersion => "create_version",
        StudioAuthorizationOperation.ListOwn => "list_own",
        StudioAuthorizationOperation.ReadContentItem => "read_content_item",
        StudioAuthorizationOperation.ReopenVersion => "reopen_version",
        StudioAuthorizationOperation.PublishRequest => "publish_request",
        StudioAuthorizationOperation.Rollback => "rollback",
        _ => operation.ToString(),
    };
}
