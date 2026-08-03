// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Physical placement selected for one raster execution attempt.</summary>
public enum RasterExecutionPlacement
{
    /// <summary>Bounded execution in the request envelope. Native GDAL is never eligible.</summary>
    [JsonStringEnumMemberName("request")]
    Request,

    /// <summary>Durable work in a governed PostGIS raster worker.</summary>
    [JsonStringEnumMemberName("durablePostgis")]
    DurablePostgis,

    /// <summary>Durable work in the isolated local native worker.</summary>
    [JsonStringEnumMemberName("localNativeWorker")]
    LocalNativeWorker,

    /// <summary>Durable native work on a configured remote batch backend.</summary>
    [JsonStringEnumMemberName("remoteBackend")]
    RemoteBackend,
}

/// <summary>Observed availability of the database raster execution budget.</summary>
public enum RasterDatabaseHealth
{
    /// <summary>The database is healthy and its raster budget has capacity.</summary>
    [JsonStringEnumMemberName("healthy")]
    Healthy,

    /// <summary>The database is reachable but raster work would threaten its current SLO.</summary>
    [JsonStringEnumMemberName("pressured")]
    Pressured,

    /// <summary>The database raster execution lane is unavailable.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>Immutable resource ceilings applied while making one planning decision.</summary>
public sealed record RasterExecutionBudgetSnapshot
{
    /// <summary>Stable identifier for the configuration snapshot.</summary>
    public required string Version { get; init; }

    /// <summary>Maximum decoded bytes allowed in the request envelope.</summary>
    public required long MaxRequestDecodedBytes { get; init; }

    /// <summary>Maximum scratch bytes allowed in the request envelope.</summary>
    public required long MaxRequestScratchBytes { get; init; }

    /// <summary>Maximum database work units allowed in the request envelope.</summary>
    public required long MaxRequestDatabaseWork { get; init; }

    /// <summary>Maximum decoded bytes admitted to durable PostGIS execution.</summary>
    public required long MaxDatabaseDecodedBytes { get; init; }

    /// <summary>Maximum scratch bytes admitted to durable PostGIS execution.</summary>
    public required long MaxDatabaseScratchBytes { get; init; }

    /// <summary>Maximum database work units admitted to durable PostGIS execution.</summary>
    public required long MaxDatabaseWork { get; init; }

    /// <summary>Maximum decoded bytes admitted to the local native worker.</summary>
    public required long MaxLocalDecodedBytes { get; init; }

    /// <summary>Maximum scratch bytes admitted to the local native worker.</summary>
    public required long MaxLocalScratchBytes { get; init; }
}

/// <summary>Immutable health and backend-availability observations used for one decision.</summary>
public sealed record RasterExecutionHealthSnapshot
{
    /// <summary>Stable identifier for the health snapshot.</summary>
    public required string Version { get; init; }

    /// <summary>Current database raster-lane health.</summary>
    public required RasterDatabaseHealth Database { get; init; }

    /// <summary>Whether an isolated local native worker can accept this job.</summary>
    public required bool LocalNativeWorkerAvailable { get; init; }

    /// <summary>Whether a configured remote native backend can accept this job.</summary>
    public required bool RemoteNativeBackendAvailable { get; init; }

    /// <summary>Stable backend identifier when remote native placement is available.</summary>
    public string? RemoteBackend { get; init; }

    /// <summary>Stable native-capable workload identifier selected for remote placement.</summary>
    public string? RemoteWorkloadId { get; init; }
}

/// <summary>Operator engine and placement controls pinned to a planning decision.</summary>
public sealed record RasterExecutionPolicySnapshot
{
    /// <summary>Stable operator policy reference.</summary>
    public required string PolicyRef { get; init; }

    /// <summary>Engines the operator allows for this workload.</summary>
    public required IReadOnlyList<RasterEngine> AllowedEngines { get; init; }

    /// <summary>Placements the operator allows for this workload.</summary>
    public required IReadOnlyList<RasterExecutionPlacement> AllowedPlacements { get; init; }

    /// <summary>Optional engine that must be used or refused.</summary>
    public RasterEngine? RequiredEngine { get; init; }

    /// <summary>Optional placement that must be used or refused.</summary>
    public RasterExecutionPlacement? RequiredPlacement { get; init; }

    /// <summary>Optional engine preference applied after capability and locality gates.</summary>
    public RasterEngine? PreferredEngine { get; init; }
}

/// <summary>All immutable inputs consumed by the raster execution planner.</summary>
public sealed record RasterExecutionPlanningRequest
{
    /// <summary>Canonical raster process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Residency of every raster input in stable parameter order.</summary>
    public required IReadOnlyList<RasterInputResidency> InputResidencies { get; init; }

    /// <summary>IANA media type of every input in stable parameter order.</summary>
    public required IReadOnlyList<string> InputMediaTypes { get; init; }

    /// <summary>Required durable output sink.</summary>
    public required RasterOutputSink OutputSink { get; init; }

    /// <summary>Required output media type when the caller pins one.</summary>
    public string? OutputMediaType { get; init; }

    /// <summary>Metadata-only cost vector supplied to each capable engine estimator.</summary>
    public required RasterCostEstimatorInput Cost { get; init; }

    /// <summary>Resource ceilings pinned for this decision.</summary>
    public required RasterExecutionBudgetSnapshot Budgets { get; init; }

    /// <summary>Health and backend availability pinned for this decision.</summary>
    public required RasterExecutionHealthSnapshot Health { get; init; }

    /// <summary>Operator engine and placement controls pinned for this decision.</summary>
    public required RasterExecutionPolicySnapshot Policy { get; init; }

    /// <summary>Whether this caller owns a bounded request-execution envelope.</summary>
    public bool AllowRequestExecution { get; init; }

    /// <summary>Decision already pinned to a prior attempt, when one exists.</summary>
    public RasterExecutionDecision? ExistingDecision { get; init; }

    /// <summary>Whether an externally visible mutation may already have begun.</summary>
    public bool MutatingAttemptStarted { get; init; }
}

/// <summary>Durable, explainable raster engine and placement decision.</summary>
public sealed record RasterExecutionDecision
{
    /// <summary>Version of this durable decision schema.</summary>
    public int DecisionVersion { get; init; } = 1;

    /// <summary>Canonical process identifier.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Selected raster engine.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Selected physical execution placement.</summary>
    public required RasterExecutionPlacement Placement { get; init; }

    /// <summary>Input residency snapshot used by the decision.</summary>
    public required IReadOnlyList<RasterInputResidency> InputResidencies { get; init; }

    /// <summary>Selected output sink.</summary>
    public required RasterOutputSink OutputSink { get; init; }

    /// <summary>Normalized conservative cost estimate for the selected engine.</summary>
    public required RasterCostEstimate Cost { get; init; }

    /// <summary>Engine-independent semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Selected engine implementation version.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Stable machine-readable selection reason.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Actionable human-readable selection reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Operator policy reference applied by the planner.</summary>
    public required string PolicyRef { get; init; }

    /// <summary>Configuration snapshot used by the planner.</summary>
    public required string ConfigurationVersion { get; init; }

    /// <summary>Health snapshot used by the planner.</summary>
    public required string HealthVersion { get; init; }

    /// <summary>Selected remote backend identifier, when remotely placed.</summary>
    public string? Backend { get; init; }

    /// <summary>Selected remote workload identifier, when remotely placed.</summary>
    public string? RemoteWorkloadId { get; init; }
}

/// <summary>Thrown when no semantically capable engine and placement passes admission.</summary>
public sealed class RasterExecutionPlanningException : Exception
{
    /// <summary>Creates an actionable planning refusal.</summary>
    public RasterExecutionPlanningException(
        string reasonCode,
        string message,
        bool isRetryable = false)
        : base(message)
    {
        ReasonCode = reasonCode;
        IsRetryable = isRetryable;
    }

    /// <summary>Stable machine-readable refusal reason.</summary>
    public string ReasonCode { get; }

    /// <summary>
    /// Whether a fresh health or backend-availability snapshot can make the same request eligible.
    /// Capability, compatibility, budget, and operator-policy refusals are permanent for the request.
    /// </summary>
    public bool IsRetryable { get; }
}

/// <summary>Selects a raster engine and placement from immutable capability, cost, and health inputs.</summary>
public interface IRasterExecutionPlanner
{
    /// <summary>Produces a durable decision or throws an actionable admission refusal.</summary>
    RasterExecutionDecision Plan(RasterExecutionPlanningRequest request);
}
