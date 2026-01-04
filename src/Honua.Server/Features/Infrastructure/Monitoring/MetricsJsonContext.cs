// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// JSON serialization context for metrics models with AOT support.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HealthMetrics))]
[JsonSerializable(typeof(PerformanceMetricsResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(DatabaseMetrics))]
[JsonSerializable(typeof(DatabaseOperationMetrics))]
[JsonSerializable(typeof(CacheMetrics))]
[JsonSerializable(typeof(CacheTypeMetrics))]
[JsonSerializable(typeof(Dictionary<string, DatabaseOperationMetrics>))]
[JsonSerializable(typeof(Dictionary<string, CacheTypeMetrics>))]
[JsonSerializable(typeof(MemoryUsage))]
[JsonSerializable(typeof(QueryCacheStatisticsResponse))]
[JsonSerializable(typeof(QueryCachePerformanceMetrics))]
internal partial class MetricsJsonContext : JsonSerializerContext
{
}
