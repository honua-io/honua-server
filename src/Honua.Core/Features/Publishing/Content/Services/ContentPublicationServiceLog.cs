// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Publishing.Content.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Publishing.Content.Services;

/// <summary>
/// Source-generated logging for the content publication service. Identifiers are
/// stable so logs/traces can be joined on publication, version, route, and operation.
/// </summary>
internal static partial class ContentPublicationServiceLog
{
    [LoggerMessage(EventId = 5300, Level = LogLevel.Information,
        Message = "Published content {PublicationId} v{Revision} ({Kind}) on route {RouteSlug} (version {VersionId})")]
    public static partial void Published(ILogger logger, string publicationId, long revision, ContentPublicationKind kind, string routeSlug, string versionId);

    [LoggerMessage(EventId = 5301, Level = LogLevel.Information,
        Message = "Republished content {PublicationId} to v{Revision} on route {RouteSlug} (version {VersionId})")]
    public static partial void Republished(ILogger logger, string publicationId, long revision, string routeSlug, string versionId);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Information,
        Message = "Rolled back content {PublicationId} route {RouteSlug} to v{Revision} (version {VersionId})")]
    public static partial void RolledBack(ILogger logger, string publicationId, long revision, string routeSlug, string versionId);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Information,
        Message = "Updated policy for content {PublicationId} route {RouteSlug} (operation {Operation})")]
    public static partial void PolicyUpdated(ILogger logger, string publicationId, string routeSlug, ContentPublicationOperation operation);
}
