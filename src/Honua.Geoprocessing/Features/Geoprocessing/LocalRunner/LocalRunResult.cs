// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.LocalRunner;

public sealed record LocalRunResult
{
    public required string ProcessId { get; init; }

    public required ExecutionJobStatus Status { get; init; }

    public bool Succeeded => Status == ExecutionJobStatus.Succeeded;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Artifacts { get; init; } = [];

    public IReadOnlyList<ExecutionLogEntry> Logs { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<LocalRunPhase> Phases { get; init; } = [];

    public TimeSpan Elapsed { get; init; }

    // Unsanitized commands and full logs are filled only when glass-box mode was explicitly requested.
    public GlassBoxReport? GlassBox { get; init; }

    public static LocalRunResult UnknownProcess(string processId, string supportedProcessIds) => new()
    {
        ProcessId = processId,
        Status = ExecutionJobStatus.Failed,
        ErrorMessage =
            $"Process id '{processId}' is not registered with the local runner. "
            + $"Available process ids: {supportedProcessIds}.",
    };
}
