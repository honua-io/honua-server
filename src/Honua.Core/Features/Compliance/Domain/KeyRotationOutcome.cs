// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public sealed record KeyRotationOutcome
{
    public required bool Succeeded { get; init; }

    public required int PreviousVersion { get; init; }

    public required int NewVersion { get; init; }

    public required DateTimeOffset RotatedAt { get; init; }

    // A failure reason is sanitized and never includes exception text.
    public required string Message { get; init; }

    public bool AuditRecorded { get; init; } = true;
}
