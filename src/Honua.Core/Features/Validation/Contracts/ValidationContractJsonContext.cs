// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Validation.Contracts;

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
