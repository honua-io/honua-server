// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Validation.Contracts;

namespace Honua.Infrastructure.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProblemDetailsResponse))]
[JsonSerializable(typeof(FieldValidationError))]
[JsonSerializable(typeof(IReadOnlyList<FieldValidationError>))]
internal sealed partial class ProblemJsonContext : JsonSerializerContext
{
}
