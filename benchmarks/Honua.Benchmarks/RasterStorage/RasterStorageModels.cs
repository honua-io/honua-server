// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Benchmarks.RasterStorage;

[JsonConverter(typeof(JsonStringEnumConverter<RasterStorageLayout>))]
internal enum RasterStorageLayout
{
    PostgisMonolithicExternal,
    PostgisTiled,
    ObjectCog,
    HybridCogPostgis,
    ObjectZarr,
}

[JsonConverter(typeof(JsonStringEnumConverter<RasterStorageWorkload>))]
internal enum RasterStorageWorkload
{
    AlignmentValidation,
    Ingest,
    Tile,
    Export,
    Identify,
    Statistics,
    Mosaic,
    Reproject,
    SurfaceAnalysis,
    ZonalStatistics,
    Backup,
    Restore,
    Vacuum,
    ConcurrentTenant,
}

[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkSupport>))]
internal enum BenchmarkSupport
{
    Runnable,
    ExternalStep,
    Unsupported,
}

[JsonConverter(typeof(JsonStringEnumConverter<GridExpectation>))]
internal enum GridExpectation
{
    Aligned,
    Misaligned,
}

[JsonConverter(typeof(JsonStringEnumConverter<BenchmarkResultStatus>))]
internal enum BenchmarkResultStatus
{
    Completed,
    ExternalStepRequired,
    Unsupported,
    Ineligible,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetricAvailability>))]
internal enum MetricAvailability
{
    Measured,
    NotApplicable,
    Unavailable,
}

internal sealed record RasterGrid(
    int Srid,
    double OriginX,
    double OriginY,
    double ScaleX,
    double ScaleY,
    double SkewX,
    double SkewY,
    int Width,
    int Height);

internal sealed record RasterFixtureDefinition(
    string Id,
    string Description,
    string PixelType,
    int BandCount,
    IReadOnlyList<RasterGrid> Scenes,
    GridExpectation AlignmentExpectation)
{
    [JsonIgnore]
    public long LogicalBytes => checked(Scenes.Sum(scene => (long)scene.Width * scene.Height * BandCount));
}

internal sealed record RasterStorageBenchmarkCell(
    RasterStorageLayout Layout,
    RasterStorageWorkload Workload,
    BenchmarkSupport Support,
    string Reason,
    string? TrackingIssue = null);

internal sealed record RasterStorageThresholdContract(
    string ServingRule,
    string AnalysisRule,
    string ArchiveRule,
    IReadOnlyList<string> RequiredInputs);

internal sealed record RasterStorageProtocolDefinition(
    string ProtocolVersion,
    IReadOnlyList<RasterFixtureDefinition> Fixtures,
    IReadOnlyList<RasterStorageBenchmarkCell> Cells,
    IReadOnlyList<string> RequiredMetrics,
    RasterStorageThresholdContract Thresholds);

internal sealed record MetricObservation(
    string Name,
    double? Value,
    string Unit,
    MetricAvailability Availability,
    string Source,
    string? Reason = null);

internal sealed record RasterStorageWorkloadResult(
    RasterStorageLayout Layout,
    string FixtureId,
    RasterStorageWorkload Workload,
    BenchmarkResultStatus Status,
    int WarmupCount,
    IReadOnlyList<double> LatencySamplesMilliseconds,
    double? LatencyP50Milliseconds,
    double? LatencyP95Milliseconds,
    IReadOnlyList<MetricObservation> Metrics,
    string? Reason = null);

internal sealed record RasterStorageEnvironment(
    string OperatingSystem,
    string Runtime,
    int ProcessorCount,
    string? GitSha,
    string? PostgresVersion,
    string? PostgisVersion,
    string? ObjectProvider,
    string Notes);

internal sealed record RasterStorageBenchmarkRun(
    string ProtocolVersion,
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    RasterStorageEnvironment Environment,
    IReadOnlyList<RasterStorageWorkloadResult> Results);
