// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// String constants for the feature-change outbox row status machine.
/// Status transitions: pending → claimed → dispatched | failed → claimed (retry) | dead_lettered.
/// </summary>
public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Dispatched = "dispatched";
    public const string Failed = "failed";
    public const string DeadLettered = "dead_lettered";
}
