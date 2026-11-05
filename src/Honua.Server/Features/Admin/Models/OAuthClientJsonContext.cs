// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

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
