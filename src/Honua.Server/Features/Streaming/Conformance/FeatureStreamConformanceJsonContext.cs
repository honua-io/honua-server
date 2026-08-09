// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// Source-generated JSON serialization context for the controlled-conformance surface
/// (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(FeatureStreamConformanceRunRequest))]
[JsonSerializable(typeof(FeatureStreamConformanceMutationRequest))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamConformanceRunResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamConformanceMutationResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamConformanceCleanupResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamConformanceResetResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamConformanceCapability>))]
internal sealed partial class FeatureStreamConformanceJsonContext : JsonSerializerContext
{
}
