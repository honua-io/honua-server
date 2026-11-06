// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

public sealed record FieldValidationResult
{
    public static FieldValidationResult Valid { get; } = new();

    [JsonPropertyName("errors")]
    public IReadOnlyList<FieldValidationError> Errors { get; init; } = Array.Empty<FieldValidationError>();

    [JsonIgnore]
    public bool IsValid
        => !Errors.Any(error => error.Severity is ValidationSeverity.Error or ValidationSeverity.Blocker);

    public static FieldValidationResult FromErrors(IEnumerable<FieldValidationError>? errors)
        => errors is null
            ? Valid
            : new FieldValidationResult { Errors = errors as IReadOnlyList<FieldValidationError> ?? errors.ToList() };
}
