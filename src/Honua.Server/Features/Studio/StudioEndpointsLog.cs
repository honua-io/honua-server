// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Studio;

internal static partial class StudioEndpointsLog
{
    [LoggerMessage(EventId = 118000, Level = LogLevel.Information, Message = "Studio package capabilities returned with persistence mode {PersistenceMode}.")]
    public static partial void CapabilitiesReturned(ILogger logger, Honua.Core.Features.Studio.Domain.StudioPackagePersistenceMode persistenceMode);

    [LoggerMessage(EventId = 118001, Level = LogLevel.Information, Message = "Studio package draft {DraftId} created for item {ItemId} and family {Family}.")]
    public static partial void DraftCreated(ILogger logger, Guid draftId, Guid itemId, Honua.Core.Features.Studio.Domain.StudioPackageFamily family);

    [LoggerMessage(EventId = 118002, Level = LogLevel.Information, Message = "Studio package draft {DraftId} updated to generation {Generation}.")]
    public static partial void DraftUpdated(ILogger logger, Guid draftId, long generation);

    [LoggerMessage(EventId = 118003, Level = LogLevel.Information, Message = "Studio content version {VersionId} created for item {ItemId}.")]
    public static partial void VersionCreated(ILogger logger, Guid itemId, Guid versionId);

    [LoggerMessage(EventId = 118004, Level = LogLevel.Information, Message = "Studio publication request {RequestId} created for item {ItemId} version {VersionId} with status {Status}.")]
    public static partial void PublicationRequestCreated(ILogger logger, Guid requestId, Guid itemId, Guid versionId, Honua.Core.Features.Studio.Domain.StudioPublicationRequestStatus status);

    [LoggerMessage(EventId = 118005, Level = LogLevel.Information, Message = "Studio rollback request {RequestId} moved item {ItemId} pointer {Target} to version {VersionId}.")]
    public static partial void RollbackCreated(ILogger logger, Guid requestId, Guid itemId, Honua.Core.Features.Studio.Domain.StudioRollbackPointer target, Guid versionId);

    [LoggerMessage(EventId = 118050, Level = LogLevel.Error, Message = "Studio package endpoint {Operation} failed.")]
    public static partial void EndpointFailed(ILogger logger, string operation, Exception exception);
}
