// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Alerts;

internal static partial class AlertLog
{
    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Debug,
        Message = "Enqueued {ChannelCount} dispatch jobs for event {EventId}")]
    public static partial void DispatchEnqueued(
        ILogger logger,
        int channelCount,
        long eventId);

    [LoggerMessage(
        EventId = 5602,
        Level = LogLevel.Debug,
        Message = "Claimed {Count} pending dispatch jobs")]
    public static partial void DispatchClaimed(
        ILogger logger,
        int count);

    [LoggerMessage(
        EventId = 5603,
        Level = LogLevel.Warning,
        Message = "Dispatch {DispatchId} failed, deadLetter={DeadLetter}")]
    public static partial void DispatchFailed(
        ILogger logger,
        long dispatchId,
        bool deadLetter);

    [LoggerMessage(
        EventId = 5604,
        Level = LogLevel.Debug,
        Message = "Found {Count} dwell candidates due before {DueBefore}")]
    public static partial void DwellCandidatesFound(
        ILogger logger,
        int count,
        DateTimeOffset dueBefore);

    [LoggerMessage(
        EventId = 5605,
        Level = LogLevel.Debug,
        Message = "Loaded {RuleCount} active rules for {KeyCount} lookup keys")]
    public static partial void ActiveRulesLoaded(
        ILogger logger,
        int ruleCount,
        int keyCount);

    [LoggerMessage(
        EventId = 5606,
        Level = LogLevel.Warning,
        Message = "Dispatch {DispatchId} dead-lettered after exhausting retries")]
    public static partial void DispatchDeadLettered(
        ILogger logger,
        long dispatchId);
}
