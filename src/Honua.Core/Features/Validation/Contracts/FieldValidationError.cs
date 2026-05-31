// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

/// <summary>
/// Shared field-level validation error contract returned by validate and
/// publish endpoints. This is the single wire shape all field-addressable
/// validators converge on; it is modeled on the canonical Studio package
/// diagnostic (<c>{code,severity,path,message}</c>) and adds an optional
/// <see cref="FieldId"/> alias for shapes that address fields by id rather
/// than by JSON Pointer path.
/// </summary>
public sealed record FieldValidationError
{
    /// <summary>Machine-friendly, stable error code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Severity of the error (info/warning/error/blocker).</summary>
    [JsonPropertyName("severity")]
    public required ValidationSeverity Severity { get; init; }

    /// <summary>
    /// JSON Pointer (RFC 6901) or dotted field path identifying the location
    /// of the error within the validated document. Null when the error is not
    /// addressable to a specific location.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Optional field identifier alias. Some validators address fields by a
    /// logical field id rather than a document path; consumers may use this to
    /// bind errors to inputs when <see cref="Path"/> is absent or coarse.
    /// </summary>
    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

    /// <summary>
    /// Creates a field-level error.
    /// </summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="severity">Severity; defaults to <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="path">Optional JSON Pointer or dotted path.</param>
    /// <param name="fieldId">Optional field id alias.</param>
    /// <returns>A populated <see cref="FieldValidationError"/>.</returns>
    public static FieldValidationError Create(
        string code,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error,
        string? path = null,
        string? fieldId = null)
        => new()
        {
            Code = code,
            Message = message,
            Severity = severity,
            Path = path,
            FieldId = fieldId,
        };
}
