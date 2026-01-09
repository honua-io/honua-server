// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for secure connection admin API models.
/// </summary>
/// <remarks>
/// Provides AOT-compatible JSON serialization for all secure connection management
/// API models while ensuring sensitive information is never serialized.
/// </remarks>
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
internal partial class SecureConnectionJsonContext : JsonSerializerContext
{
}
