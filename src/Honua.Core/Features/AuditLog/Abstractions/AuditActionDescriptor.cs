// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public sealed record AuditActionDescriptor
{
    public required AuditEventType EventType { get; init; }

    public required string Action { get; init; }

    public required string ResourceType { get; init; }

    // Auditing every successful read would flood the sink, so a read path records only a security-relevant failure.
    public bool AuditOnSuccess { get; init; } = true;
}
