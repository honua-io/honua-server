// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Console.Publications;

/// <summary>
/// Source-generated logging for content publication endpoints. Identifiers are stable
/// for joining logs/traces (publicationId, routeSlug, revision, operation).
/// </summary>
internal static partial class ContentPublicationEndpointsLog
{
    [LoggerMessage(EventId = 5310, Level = LogLevel.Information,
        Message = "Published content {PublicationId} on route {RouteSlug}")]
    public static partial void Published(ILogger logger, string publicationId, string routeSlug);

    [LoggerMessage(EventId = 5311, Level = LogLevel.Information,
        Message = "Republished content {PublicationId} on route {RouteSlug} (revision {Revision})")]
    public static partial void Republished(ILogger logger, string publicationId, string routeSlug, long revision);

    [LoggerMessage(EventId = 5312, Level = LogLevel.Information,
        Message = "Rolled back content {PublicationId} on route {RouteSlug} (revision {Revision})")]
    public static partial void RolledBack(ILogger logger, string publicationId, string routeSlug, long revision);

    [LoggerMessage(EventId = 5313, Level = LogLevel.Information,
        Message = "Updated policy for content {PublicationId} on route {RouteSlug}")]
    public static partial void PolicyUpdated(ILogger logger, string publicationId, string routeSlug);

    [LoggerMessage(EventId = 5314, Level = LogLevel.Warning,
        Message = "Denied public-link read for route {RouteSlug}")]
    public static partial void PublicLinkDenied(ILogger logger, string routeSlug);

    [LoggerMessage(EventId = 5315, Level = LogLevel.Warning,
        Message = "Denied embed read for route {RouteSlug}")]
    public static partial void EmbedDenied(ILogger logger, string routeSlug);

    [LoggerMessage(EventId = 5316, Level = LogLevel.Error,
        Message = "Content publication endpoint failure: {Operation}")]
    public static partial void EndpointFailed(ILogger logger, string operation, Exception exception);
}

/// <summary>
/// Telemetry activity names for content publication flows. Lowercase dotted to match
/// the <c>honua.*</c> convention.
/// </summary>
internal static class ContentPublicationTelemetry
{
    public const string Publish = "honua.publication.publish";
    public const string Republish = "honua.publication.republish";
    public const string Rollback = "honua.publication.rollback";
    public const string PolicyUpdate = "honua.publication.policy";
    public const string RouteResolve = "honua.publication.route.resolve";
    public const string RevisionPreview = "honua.publication.revision.preview";

    public const string TagPublicationId = "honua.publication.id";
    public const string TagRouteSlug = "honua.publication.route";
    public const string TagRevision = "honua.publication.revision";
    public const string TagKind = "honua.publication.kind";
    public const string TagCacheHit = "honua.publication.cache_hit";
    public const string TagDependencyCount = "honua.publication.dependency_count";
}

/// <summary>
/// Writes content publication audit events through the shared <see cref="IAuditLog"/>.
/// Resource type is <c>content-publication</c>; details are caller-sanitized.
/// </summary>
internal static class ContentPublicationAudit
{
    public const string ResourceType = "content-publication";

    public static Task RecordAsync(
        IAuditLog auditLog,
        HttpContext context,
        string action,
        AuditOutcome outcome,
        string? publicationId,
        string? detail = null,
        AuditEventType eventType = AuditEventType.AdminAction)
    {
        var (actor, actorType) = ResolveActor(context.User);
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = eventType,
            Actor = actor,
            ActorType = actorType,
            ResourceType = ResourceType,
            ResourceId = publicationId,
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            Details = detail ?? string.Empty,
        };

        return auditLog.RecordAsync(auditEvent, context.RequestAborted);
    }

    private static (string Actor, AuditActorType ActorType) ResolveActor(ClaimsPrincipal user)
    {
        var actorId = ConsolePrincipal.ResolveActorId(user);
        return actorId is null
            ? (AuditEvent.AnonymousActor, AuditActorType.Anonymous)
            : (actorId, AuditActorType.UserId);
    }
}
