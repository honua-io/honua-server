// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Forms;

internal sealed class FormPackageAuditEntry
{
    public required string Action { get; init; }

    public required string FormId { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required string Details { get; init; }
}

internal static class FormPackageAudit
{
    public static Task<string?> RecordAsync(IAuditLog auditLog, HttpContext context, FormPackageAuditEntry entry)
        => auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ActorResolver.Resolve(context, ActorResolution.SubjectOrName),
            ActorType = context.User.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "form_package",
            ResourceId = entry.FormId,
            Action = entry.Action,
            Outcome = entry.Outcome,
            CorrelationId = context.TraceIdentifier,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            Details = entry.Details
        }, context.RequestAborted);
}
