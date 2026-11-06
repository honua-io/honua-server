// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public sealed record AuditActionDescriptor
{
    public required AuditEventType EventType { get; init; }

    public required string Action { get; init; }

    public required string ResourceType { get; init; }

    // A read path audited only on security-relevant failure does not write a record for every successful request.
    public bool AuditOnSuccess { get; init; } = true;
}
