// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for license admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<LicenseStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<EntitlementResponse>>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(LicenseStatusResponse))]
[JsonSerializable(typeof(EntitlementResponse))]
internal sealed partial class LicenseJsonContext : JsonSerializerContext
{
}
