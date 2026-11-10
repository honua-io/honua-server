// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public static class OutboxStatuses
{
    public const string Pending = "pending";

    public const string Claimed = "claimed";

    public const string Dispatched = "dispatched";

    public const string Failed = "failed";

    public const string DeadLettered = "dead_lettered";
}
