// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for identity admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IdentityProvidersResponse>))]
[JsonSerializable(typeof(ApiResponse<IdentityProviderTestResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(IdentityProvidersResponse))]
[JsonSerializable(typeof(IdentityProviderTestResult))]
[JsonSerializable(typeof(IdentityProviderStatus))]
[JsonSerializable(typeof(IdentityProviderStatus[]))]
internal sealed partial class IdentityAdminJsonContext : JsonSerializerContext
{
}
