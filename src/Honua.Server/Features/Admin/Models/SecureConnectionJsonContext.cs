// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

// Serialization of these models never includes secret fields.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<SecureConnectionSummary>))]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<SecureConnectionSummary>>))]
[JsonSerializable(typeof(ApiResponse<SecureConnectionDetail>))]
[JsonSerializable(typeof(ApiResponse<ConnectionTestResult>))]
[JsonSerializable(typeof(ApiResponse<EncryptionValidationResult>))]
[JsonSerializable(typeof(ApiResponse<KeyRotationResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateSecureConnectionRequest))]
[JsonSerializable(typeof(UpdateSecureConnectionRequest))]
[JsonSerializable(typeof(SecureConnectionSummary))]
[JsonSerializable(typeof(SecureConnectionDetail))]
[JsonSerializable(typeof(ConnectionTestResult))]
[JsonSerializable(typeof(EncryptionValidationResult))]
[JsonSerializable(typeof(KeyRotationResult))]
internal sealed partial class SecureConnectionJsonContext : JsonSerializerContext
{
}
