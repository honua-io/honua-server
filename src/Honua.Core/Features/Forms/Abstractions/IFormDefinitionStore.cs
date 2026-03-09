// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Domain;

namespace Honua.Core.Features.Forms.Abstractions;

/// <summary>
/// Store for managing form definitions with versioning support.
/// </summary>
public interface IFormDefinitionStore
{
    /// <summary>
    /// Gets a form definition by ID and optional version.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="version">Specific version, or null for latest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Form definition or null if not found.</returns>
    Task<FormDefinition?> GetFormDefinitionAsync(
        string formId,
        string? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new form definition or version.
    /// </summary>
    /// <param name="formDefinition">Form definition to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored form definition with generated IDs.</returns>
    Task<FormDefinition> StoreFormDefinitionAsync(
        FormDefinition formDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets form metadata for catalog/listing purposes.
    /// </summary>
    /// <param name="formIds">Specific form IDs to retrieve, or empty for all.</param>
    /// <param name="tags">Filter by tags.</param>
    /// <param name="serviceId">Filter by target service ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of form metadata.</returns>
    Task<List<FormMetadata>> GetFormMetadataAsync(
        List<string> formIds,
        List<string> tags,
        string? serviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates form status (draft, published, archived).
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="status">New status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateFormStatusAsync(
        string formId,
        FormStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a form definition.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="version">Specific version, or null to delete all versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteFormDefinitionAsync(
        string formId,
        string? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets form definitions created from a specific layer schema.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Forms targeting this service/layer.</returns>
    Task<List<FormDefinition>> GetFormsByTargetLayerAsync(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default);
}
