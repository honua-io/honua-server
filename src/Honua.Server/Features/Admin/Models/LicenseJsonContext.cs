// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Models;

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
[JsonSerializable(typeof(LicenseCapacityState))]
[JsonSerializable(typeof(LicenseCapacityTerms))]
[JsonSerializable(typeof(LicenseSurgeModeState))]
[JsonSerializable(typeof(EntitlementResponse))]
internal sealed partial class LicenseJsonContext : JsonSerializerContext
{
}
