// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// String constants for the feature-change outbox row status machine.
/// Status transitions: pending → claimed → dispatched | failed → claimed (retry) | dead_lettered.
/// </summary>
public static class OutboxStatuses
{
    /// <summary>The entry is awaiting dispatch.</summary>
    public const string Pending = "pending";

    /// <summary>The entry has been claimed by a dispatcher and is being processed.</summary>
    public const string Claimed = "claimed";

    /// <summary>The entry was successfully dispatched.</summary>
    public const string Dispatched = "dispatched";

    /// <summary>The entry failed dispatch and may be retried.</summary>
    public const string Failed = "failed";

    /// <summary>The entry exhausted its retries and was moved to the dead-letter state.</summary>
    public const string DeadLettered = "dead_lettered";
}
