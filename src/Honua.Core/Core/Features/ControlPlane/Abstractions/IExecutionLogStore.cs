// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IExecutionLogStore
{
    Task AppendAsync(
        string operationId,
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default);

    Task AppendOnceAsync(string operationId, string eventId, ExecutionLogEntry entry, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Durable idempotent log ingestion is required.");

    Task<IReadOnlyList<ExecutionLogEntry>> GetLogsAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<ExecutionLogTail> TailAsync(
        string operationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ExecutionLogPage> QueryAsync(
        string operationId,
        ExecutionLogQuery query,
        CancellationToken cancellationToken = default);

    Task SetRetentionAsync(
        string operationId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}
