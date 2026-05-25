// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;

namespace Honua.Core.Features.GeoETL.Services;

/// <summary>
/// In-memory <see cref="IPipelineDefinitionStore"/> for the baseline slice and tests.
/// The durable PostgreSQL-backed store (<c>honua.pipeline_definitions</c>) is Child
/// Ticket A's remaining EF/DbUp work; this implementation preserves the same contract.
/// </summary>
public sealed class InMemoryPipelineDefinitionStore : IPipelineDefinitionStore
{
    private readonly ConcurrentDictionary<string, PipelineDefinition> _store =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task CreateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_store.TryAdd(definition.Id, definition))
        {
            throw new InvalidOperationException($"Pipeline '{definition.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PipelineDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(id, out var definition) ? definition : null);

    /// <inheritdoc />
    public Task<PipelineDefinition?> UpdateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_store.TryGetValue(definition.Id, out var existing))
        {
            return Task.FromResult<PipelineDefinition?>(null);
        }

        var updated = definition with
        {
            Version = existing.Version + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _store[definition.Id] = updated;
        return Task.FromResult<PipelineDefinition?>(updated);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryRemove(id, out _));

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PipelineDefinition>>(_store.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray());
}

/// <summary>
/// In-memory <see cref="IPipelineExecutionStore"/> for the baseline slice and tests.
/// The durable PostgreSQL-backed store (<c>honua.pipeline_executions</c>) is Child
/// Ticket A's remaining EF/DbUp work; this implementation preserves the same contract.
/// </summary>
public sealed class InMemoryPipelineExecutionStore : IPipelineExecutionStore
{
    private readonly ConcurrentDictionary<string, PipelineExecution> _store =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task CreateAsync(PipelineExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!_store.TryAdd(execution.Id, execution))
        {
            throw new InvalidOperationException($"Execution '{execution.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(PipelineExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _store[execution.Id] = execution;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PipelineExecution?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_store.TryGetValue(id, out var execution) ? execution : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineExecution>> ListForPipelineAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PipelineExecution>>(
            _store.Values
                .Where(e => string.Equals(e.PipelineId, pipelineId, StringComparison.Ordinal))
                .OrderByDescending(e => e.CreatedAt)
                .ToArray());
}
