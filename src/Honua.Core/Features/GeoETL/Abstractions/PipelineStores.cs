// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;

namespace Honua.Core.Features.GeoETL.Abstractions;

/// <summary>
/// Durable store for pipeline definitions. Backed by <c>honua.pipeline_definitions</c>
/// in PostgreSQL (Child Ticket A) with an in-memory implementation for the baseline slice.
/// </summary>
public interface IPipelineDefinitionStore
{
    /// <summary>
    /// Persists a new pipeline definition.
    /// </summary>
    /// <param name="definition">Definition to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current version of a pipeline definition, or null when not found.
    /// </summary>
    /// <param name="id">Pipeline identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDefinition?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a pipeline definition, incrementing its version. Returns the stored
    /// definition with the new version, or null when the pipeline does not exist.
    /// </summary>
    /// <param name="definition">Updated definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineDefinition?> UpdateAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a pipeline definition. Returns true when a definition was removed.
    /// </summary>
    /// <param name="id">Pipeline identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all pipeline definitions (current versions).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable store for pipeline execution records. Backed by <c>honua.pipeline_executions</c>
/// in PostgreSQL (Child Ticket A) with an in-memory implementation for the baseline slice.
/// </summary>
public interface IPipelineExecutionStore
{
    /// <summary>
    /// Persists a new execution record.
    /// </summary>
    /// <param name="execution">Execution to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateAsync(PipelineExecution execution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest execution state.
    /// </summary>
    /// <param name="execution">Execution to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateAsync(PipelineExecution execution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an execution record, or null when not found.
    /// </summary>
    /// <param name="id">Execution identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineExecution?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists executions for a pipeline, newest first.
    /// </summary>
    /// <param name="pipelineId">Pipeline identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PipelineExecution>> ListForPipelineAsync(
        string pipelineId,
        CancellationToken cancellationToken = default);
}
