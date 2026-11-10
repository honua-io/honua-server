// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

public sealed record AuditRetentionPolicy
{
    public required TimeSpan RetentionWindow { get; init; }

    public bool IsBounded => RetentionWindow > TimeSpan.Zero;

    public DateTimeOffset CutoffUtc(DateTimeOffset now)
        => IsBounded ? now.ToUniversalTime() - RetentionWindow : DateTimeOffset.MinValue;

    public bool IsExpired(DateTimeOffset eventTimestamp, DateTimeOffset now)
        => IsBounded && eventTimestamp.ToUniversalTime() < CutoffUtc(now);
}
