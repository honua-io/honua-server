// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

internal static partial class FeatureMutationEventLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Feature-change publish failed after write committed for event {EventId}, service {ServiceId}, layer {LayerId}, object {ObjectId}, operation {Operation}, protocol {Protocol}.")]
    public static partial void PublishAfterCommitFailed(
        ILogger logger,
        string eventId,
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        Exception exception);
}
