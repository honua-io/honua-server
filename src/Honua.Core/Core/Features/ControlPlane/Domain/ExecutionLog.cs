// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum ExecutionLogLevel
{
    Debug,

    Info,

    Warning,

    Error
}

public sealed record ExecutionLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required ExecutionLogLevel Level { get; init; }

    public required string Message { get; init; }

    public string? Phase { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
