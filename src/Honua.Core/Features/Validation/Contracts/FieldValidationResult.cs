// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

/// <summary>
/// Wrapper around a collection of <see cref="FieldValidationError"/> items, the
/// shared field-level validation result used across validate and publish
/// endpoints. Named <c>FieldValidationResult</c> to avoid collision with the
/// pre-existing query <c>ValidationResult</c> in
/// <c>Honua.Core.Features.Validation</c>; it is the "ValidationResult wrapper"
/// the form-validation contract refers to.
/// </summary>
public sealed record FieldValidationResult
{
    /// <summary>Shared empty (valid) result.</summary>
    public static FieldValidationResult Valid { get; } = new();

    /// <summary>The field-level errors.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<FieldValidationError> Errors { get; init; } = Array.Empty<FieldValidationError>();

    /// <summary>
    /// True when there are no error- or blocker-severity entries. Info and
    /// warning entries do not invalidate the result.
    /// </summary>
    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            foreach (var error in Errors)
            {
                if (error.Severity is ValidationSeverity.Error or ValidationSeverity.Blocker)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Creates a result from the supplied errors.
    /// </summary>
    /// <param name="errors">Field-level errors; null is treated as empty.</param>
    /// <returns>A populated <see cref="FieldValidationResult"/>.</returns>
    public static FieldValidationResult FromErrors(IEnumerable<FieldValidationError>? errors)
        => errors is null
            ? Valid
            : new FieldValidationResult { Errors = errors as IReadOnlyList<FieldValidationError> ?? errors.ToList() };
}
