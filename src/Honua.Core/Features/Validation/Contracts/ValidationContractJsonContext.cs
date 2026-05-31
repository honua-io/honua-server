// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

/// <summary>
/// Source-generated JSON context for the shared field-level validation contract
/// (<see cref="FieldValidationError"/> / <see cref="FieldValidationResult"/>).
/// Keeps the contract AOT-serializable wherever it is emitted.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FieldValidationError))]
[JsonSerializable(typeof(FieldValidationError[]))]
[JsonSerializable(typeof(IReadOnlyList<FieldValidationError>))]
[JsonSerializable(typeof(FieldValidationResult))]
public sealed partial class ValidationContractJsonContext : JsonSerializerContext
{
}
