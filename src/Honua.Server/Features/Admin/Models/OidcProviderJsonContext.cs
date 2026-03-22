// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for OIDC provider admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<OidcProviderResponse>>))]
[JsonSerializable(typeof(ApiResponse<OidcProviderResponse>))]
[JsonSerializable(typeof(ApiResponse<OidcProviderTestResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateOidcProviderRequest))]
[JsonSerializable(typeof(UpdateOidcProviderRequest))]
[JsonSerializable(typeof(OidcProviderResponse))]
[JsonSerializable(typeof(OidcProviderTestResponse))]
internal sealed partial class OidcProviderJsonContext : JsonSerializerContext
{
}
