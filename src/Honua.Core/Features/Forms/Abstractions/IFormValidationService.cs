// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Domain;

namespace Honua.Core.Features.Forms.Abstractions;

/// <summary>
/// Service for validating form definitions and instances.
/// </summary>
public interface IFormValidationService
{
    /// <summary>
    /// Validates a form instance against its definition.
    /// </summary>
    /// <param name="formDefinition">Form definition with validation rules.</param>
    /// <param name="instance">Form instance to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with issues.</returns>
    Task<FormValidationResult> ValidateFormInstanceAsync(
        FormDefinition formDefinition,
        object instance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets validation rules for a form.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of validation rules.</returns>
    Task<List<ValidationRule>> GetValidationRulesAsync(
        string formId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a form definition for completeness and consistency.
    /// </summary>
    /// <param name="formDefinition">Form definition to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    Task<FormValidationResult> ValidateFormDefinitionAsync(
        FormDefinition formDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a form is compatible with its target layer schema.
    /// </summary>
    /// <param name="formDefinition">Form definition.</param>
    /// <param name="layerDefinition">Target layer schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Schema compatibility result.</returns>
    Task<SchemaCompatibilityResult> ValidateSchemaCompatibilityAsync(
        FormDefinition formDefinition,
        object layerDefinition,
        CancellationToken cancellationToken = default);
}
