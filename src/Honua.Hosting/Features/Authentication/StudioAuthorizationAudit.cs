// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Middleware;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Shared audit writer for canonical Studio authorization decisions. REST and protocol
/// adapters use this seam so a denied decision always records the same actor, resource,
/// operation, and stable policy code.
/// </summary>
internal static class StudioAuthorizationAudit
{
    public static async Task RecordDecisionAsync(
        HttpContext context,
        IAuditLog auditLog,
        TimeProvider timeProvider,
        StudioAuthorizationOperation operation,
        string resourceType,
        string? resourceId,
        StudioAuthorizationDecision decision)
    {
        if (decision.IsAllowed && !decision.IsElevated)
        {
            return;
        }

        var auditEvent = new AuditEvent
        {
            Timestamp = timeProvider.GetUtcNow(),
            EventType = AuditEventType.Authorization,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = $"studio.{ToSnakeCase(operation)}",
            Outcome = decision.IsAllowed ? AuditOutcome.Success : AuditOutcome.Denied,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = decision.Code is null ? string.Empty : $"{{\"code\":\"{decision.Code}\"}}",
        };

        await auditLog.RecordAsync(auditEvent, context.RequestAborted).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            AuditContextResolver.MarkAuthorizationFailureAudited(context);
        }
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
        StudioAuthorizationOperation.Generate => "generate",
        _ => operation.ToString(),
    };
}
