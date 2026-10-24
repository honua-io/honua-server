// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for row-level security (RLS) admin API models (#502).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<RlsPolicyResponse>>))]
[JsonSerializable(typeof(ApiResponse<RlsPolicyResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateRlsPolicyRequest))]
[JsonSerializable(typeof(RlsPolicyResponse))]
internal sealed partial class RlsPolicyJsonContext : JsonSerializerContext
{
}
