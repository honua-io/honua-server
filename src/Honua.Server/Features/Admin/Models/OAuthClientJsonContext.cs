// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for OAuth2 client registration and scope
/// catalogue admin models (ADR-0053 Increment 2, #1888).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<OAuthClientResponse>>))]
[JsonSerializable(typeof(ApiResponse<OAuthClientResponse>))]
[JsonSerializable(typeof(ApiResponse<OAuthClientSecretResponse>))]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<OAuthScopeResponse>>))]
[JsonSerializable(typeof(ApiResponse<OAuthScopeResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateOAuthClientRequest))]
[JsonSerializable(typeof(OAuthClientResponse))]
[JsonSerializable(typeof(OAuthClientSecretResponse))]
[JsonSerializable(typeof(DefineOAuthScopeRequest))]
[JsonSerializable(typeof(OAuthScopeResponse))]
internal sealed partial class OAuthClientJsonContext : JsonSerializerContext
{
}
