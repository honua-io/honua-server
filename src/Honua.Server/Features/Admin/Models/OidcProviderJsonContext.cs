// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

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
