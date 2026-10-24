// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for field-level security (column masking) admin API
/// models (#1940).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<FieldMaskPolicyResponse>>))]
[JsonSerializable(typeof(ApiResponse<FieldMaskPolicyResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateFieldMaskPolicyRequest))]
[JsonSerializable(typeof(FieldMaskPolicyResponse))]
internal sealed partial class FieldMaskPolicyJsonContext : JsonSerializerContext
{
}
