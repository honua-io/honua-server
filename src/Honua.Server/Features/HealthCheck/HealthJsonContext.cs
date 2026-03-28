// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// JSON serialization context for health metrics with AOT support.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(HealthPerformanceMetricsResponse))]
[JsonSerializable(typeof(HealthPerformanceMetrics))]
[JsonSerializable(typeof(HealthMemoryMetrics))]
[JsonSerializable(typeof(HealthGcMetrics))]
[JsonSerializable(typeof(HealthPerformanceErrorResponse))]
[JsonSerializable(typeof(HealthCacheRefreshMetrics))]
[JsonSerializable(typeof(DatabasePerformanceMetricsSnapshot))]
[JsonSerializable(typeof(DatabaseOperationMetricsSnapshot))]
[JsonSerializable(typeof(Dictionary<string, DatabaseOperationMetricsSnapshot>))]
internal sealed partial class HealthJsonContext : JsonSerializerContext
{
}
