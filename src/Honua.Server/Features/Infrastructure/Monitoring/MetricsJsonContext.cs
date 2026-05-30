// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Licensing;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// JSON serialization context for metrics models with AOT support.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HealthMetrics))]
[JsonSerializable(typeof(MigrationHealthMetrics))]
[JsonSerializable(typeof(PerformanceMetricsResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(HttpRequestMetrics))]
[JsonSerializable(typeof(DatabaseMetrics))]
[JsonSerializable(typeof(DatabaseOperationMetrics))]
[JsonSerializable(typeof(CacheMetrics))]
[JsonSerializable(typeof(CacheTypeMetrics))]
[JsonSerializable(typeof(Dictionary<string, DatabaseOperationMetrics>))]
[JsonSerializable(typeof(Dictionary<string, CacheTypeMetrics>))]
[JsonSerializable(typeof(MemoryUsage))]
[JsonSerializable(typeof(QueryCacheStatisticsResponse))]
[JsonSerializable(typeof(QueryPerformanceStatistics))]
[JsonSerializable(typeof(QueryTypeStatistics))]
[JsonSerializable(typeof(Dictionary<string, QueryTypeStatistics>))]
[JsonSerializable(typeof(SlowQueryResponse))]
[JsonSerializable(typeof(ResourceTrackingStatistics))]
[JsonSerializable(typeof(ResourceLeakInfo))]
[JsonSerializable(typeof(ResourceLeakResponse))]
[JsonSerializable(typeof(ResourceLeakScanResult))]
[JsonSerializable(typeof(ExceptionStatistics))]
[JsonSerializable(typeof(ExceptionRecord))]
[JsonSerializable(typeof(ExceptionHistoryResponse))]
[JsonSerializable(typeof(QueryCacheStatistics))]
[JsonSerializable(typeof(CacheEffectivenessMetrics))]
[JsonSerializable(typeof(CacheInvalidationResponse))]
[JsonSerializable(typeof(PerformanceSummaryResponse))]
[JsonSerializable(typeof(RecentErrorEntry))]
[JsonSerializable(typeof(IReadOnlyList<RecentErrorEntry>))]
[JsonSerializable(typeof(RecentErrorsResponse))]
[JsonSerializable(typeof(ObservabilityRealtimeStatus))]
[JsonSerializable(typeof(ObservabilityStatusResponse))]
[JsonSerializable(typeof(MigrationObservabilityResponse))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(StreamingMetrics))]
[JsonSerializable(typeof(ProductionHealthResponse))]
[JsonSerializable(typeof(LicenseHealthSummary))]
[JsonSerializable(typeof(ConnectionPoolMetricsResponse))]
[JsonSerializable(typeof(QueryAdmissionMetricsResponse))]
[JsonSerializable(typeof(CacheMetricsResponse))]
[JsonSerializable(typeof(ResourceMetricsResponse))]
[JsonSerializable(typeof(ResourceGcInfoResponse))]
[JsonSerializable(typeof(AlertsResponse))]
[JsonSerializable(typeof(AlertCondition))]
[JsonSerializable(typeof(UploadQueueMetricsResponse))]
[JsonSerializable(typeof(ComprehensiveHealthResponse))]
[JsonSerializable(typeof(HealthCheckEntryResponse))]
[JsonSerializable(typeof(DatabaseResilienceMetricsResponse))]
[JsonSerializable(typeof(DatabaseConnectionPoolMetrics))]
[JsonSerializable(typeof(DatabaseResilienceStatus))]
[JsonSerializable(typeof(Dictionary<string, HealthCheckEntryResponse>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class MetricsJsonContext : JsonSerializerContext
{
}
