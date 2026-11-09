// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

public sealed record FieldValidationError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("severity")]
    public required ValidationSeverity Severity { get; init; }

    // The location is a JSON Pointer or a dotted path, and is null when the error has no location.
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("fieldId")]
    public string? FieldId { get; init; }

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
