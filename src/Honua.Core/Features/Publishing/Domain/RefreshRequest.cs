// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Publishing.Domain;

public sealed record RefreshRequest
{
    public required string RequestId { get; init; }

    public required string ServiceId { get; init; }

    public required RefreshScope Scope { get; init; }

    public OperationAuditInfo Audit { get; init; } = new();

    public required DateTimeOffset RequestedAt { get; init; }

    public static RefreshRequest Create(
        string requestId,
        string serviceId,
        RefreshScope scope,
        OperationAuditInfo? audit = null)
        => new()
        {
            RequestId = requestId,
            ServiceId = serviceId,
            Scope = scope,
            Audit = audit ?? new OperationAuditInfo(),
            RequestedAt = DateTimeOffset.UtcNow
        };
}
