// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for admin auth config API models (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AdminAuthConfigResponse))]
[JsonSerializable(typeof(AdminAuthProviderInfo))]
[JsonSerializable(typeof(List<AdminAuthProviderInfo>))]
[JsonSerializable(typeof(AdminAuthAuthorizeUrlRequest))]
[JsonSerializable(typeof(AdminAuthAuthorizeUrlResponse))]
[JsonSerializable(typeof(AdminAuthTokenRequest))]
[JsonSerializable(typeof(AdminAuthTokenResponse))]
[JsonSerializable(typeof(AdminAuthLogoutUrlResponse))]
[JsonSerializable(typeof(AdminAuthOidcDiscoveryDocument))]
internal sealed partial class AdminAuthJsonContext : JsonSerializerContext
{
}
