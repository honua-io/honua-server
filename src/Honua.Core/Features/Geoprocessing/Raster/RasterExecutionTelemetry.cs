// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Bounded reason class used for raster admission metrics.</summary>
public enum RasterTelemetryAdmissionClass
{
    /// <summary>The operation passed every admission gate.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>No compatible or available engine capability exists.</summary>
    [JsonStringEnumMemberName("capability")]
    Capability,

    /// <summary>Operator policy denied the engine or placement.</summary>
    [JsonStringEnumMemberName("policy")]
    Policy,

    /// <summary>A transient engine, database, or backend health gate refused work.</summary>
    [JsonStringEnumMemberName("health")]
    Health,

    /// <summary>A concurrency, queue, or worker-capacity gate refused work.</summary>
    [JsonStringEnumMemberName("capacity")]
    Capacity,

    /// <summary>A decoded-cell, memory, scratch, database-work, duration, or byte budget refused work.</summary>
    [JsonStringEnumMemberName("resource")]
    Resource,

    /// <summary>An authorization, credential, residency, or source-access gate refused work.</summary>
    [JsonStringEnumMemberName("security")]
    Security,

    /// <summary>The requested semantic variant or cross-engine compatibility was unavailable.</summary>
    [JsonStringEnumMemberName("semantic")]
    Semantic,

    /// <summary>Input metadata or a reference contract was invalid or incomplete.</summary>
    [JsonStringEnumMemberName("input")]
    Input,

    /// <summary>A configured local or remote execution backend was unavailable or incompatible.</summary>
    [JsonStringEnumMemberName("backend")]
    Backend,

    /// <summary>The refusal did not map to a known bounded class.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>Bounded outcome dimension shared by raster lifecycle metrics.</summary>
public enum RasterTelemetryOutcome
{
    /// <summary>The planner selected a new engine and placement.</summary>
    [JsonStringEnumMemberName("selected")]
    Selected,

    /// <summary>A prior immutable decision was reused.</summary>
    [JsonStringEnumMemberName("reused")]
    Reused,

    /// <summary>Admission refused the operation before execution.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>The measured phase or job succeeded.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>The measured phase or job failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The measured phase or job was cancelled.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>The measured phase or job exceeded its deadline.</summary>
    [JsonStringEnumMemberName("timed-out")]
    TimedOut,
}

/// <summary>Bounded raster lifecycle phase used by duration and outcome metrics.</summary>
public enum RasterTelemetryPhase
{
    /// <summary>Capability, cost, policy, health, and placement planning.</summary>
    [JsonStringEnumMemberName("plan")]
    Plan,

    /// <summary>Durable time waiting for an eligible worker or backend.</summary>
    [JsonStringEnumMemberName("queue")]
    Queue,

    /// <summary>Worker claim, source resolution, staging, and executor provisioning.</summary>
    [JsonStringEnumMemberName("provision")]
    Provision,

    /// <summary>Provider or native algorithm execution.</summary>
    [JsonStringEnumMemberName("execute")]
    Execute,

    /// <summary>Attempt-fenced artifact validation and publication.</summary>
    [JsonStringEnumMemberName("publish")]
    Publish,

    /// <summary>Catalog or PostGIS registration after artifact publication.</summary>
    [JsonStringEnumMemberName("register")]
    Register,

    /// <summary>Attempt scratch and uncommitted artifact cleanup.</summary>
    [JsonStringEnumMemberName("cleanup")]
    Cleanup,

    /// <summary>Cancellation request propagation through the selected backend.</summary>
    [JsonStringEnumMemberName("cancel")]
    Cancel,
}

/// <summary>Bounded backend family; configured backend identifiers are not metric labels.</summary>
public enum RasterTelemetryBackendFamily
{
    /// <summary>Bounded work performed in the request-serving envelope.</summary>
    [JsonStringEnumMemberName("request")]
    Request,

    /// <summary>Dedicated durable PostGIS raster execution.</summary>
    [JsonStringEnumMemberName("postgis")]
    Postgis,

    /// <summary>Isolated local native raster worker.</summary>
    [JsonStringEnumMemberName("local-native")]
    LocalNative,

    /// <summary>AWS Batch remote execution.</summary>
    [JsonStringEnumMemberName("aws-batch")]
    AwsBatch,

    /// <summary>Another qualified remote batch backend family.</summary>
    [JsonStringEnumMemberName("other-remote")]
    OtherRemote,
}

/// <summary>Bounded object or artifact I/O operation.</summary>
public enum RasterTelemetryIoOperation
{
    /// <summary>Whole-object or sequential read.</summary>
    [JsonStringEnumMemberName("read")]
    Read,

    /// <summary>Byte-range read.</summary>
    [JsonStringEnumMemberName("range-read")]
    RangeRead,

    /// <summary>Attempt-scoped object write.</summary>
    [JsonStringEnumMemberName("write")]
    Write,
}

/// <summary>Bounded cache result for raster reference and range I/O.</summary>
public enum RasterTelemetryCacheResult
{
    /// <summary>The requested value was served from cache.</summary>
    [JsonStringEnumMemberName("hit")]
    Hit,

    /// <summary>The requested value was absent and resolved from its source.</summary>
    [JsonStringEnumMemberName("miss")]
    Miss,

    /// <summary>Policy or request shape deliberately bypassed caching.</summary>
    [JsonStringEnumMemberName("bypass")]
    Bypass,

    /// <summary>The cache operation failed without changing source correctness.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>Bounded pricing model attached to approximate Batch cost telemetry.</summary>
public enum RasterBatchPricingModel
{
    /// <summary>Provider on-demand compute price.</summary>
    [JsonStringEnumMemberName("on-demand")]
    OnDemand,

    /// <summary>Provider interruptible or spot compute price.</summary>
    [JsonStringEnumMemberName("spot")]
    Spot,

    /// <summary>A blended or operator-supplied allocation price.</summary>
    [JsonStringEnumMemberName("blended")]
    Blended,

    /// <summary>No reliable pricing model was available.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>
/// Stable raster telemetry vocabulary and bounded metric-tag factories.
/// Correlation identifiers and exact decision metadata belong in authenticated audit records
/// and sampled traces, never in metric labels. Object locators, signed URLs, credentials,
/// tokens, connection strings, SQL text, and native command lines are never telemetry values.
/// </summary>
public static class RasterExecutionTelemetry
{
    /// <summary>Current schema version for durable raster execution telemetry summaries.</summary>
    public const int ContractVersion = 1;

    /// <summary>Stable activity names for the raster execution lifecycle.</summary>
    public static class Activities
    {
        /// <summary>Capability, cost, policy, health, and placement planning.</summary>
        public const string Plan = "raster.execution.plan";

        /// <summary>Canonical job submission and sync-to-async promotion.</summary>
        public const string Submit = "raster.execution.submit";

        /// <summary>Durable time waiting for an eligible worker or backend.</summary>
        public const string Queue = "raster.execution.queue";

        /// <summary>Worker claim, source staging, and executor provisioning.</summary>
        public const string Provision = "raster.execution.provision";

        /// <summary>Source-reference authorization, integrity validation, and resolution.</summary>
        public const string ResolveSource = "raster.source.resolve";

        /// <summary>Provider or native raster algorithm execution.</summary>
        public const string Execute = "raster.execution.execute";

        /// <summary>Attempt-fenced output validation and publication.</summary>
        public const string Publish = "raster.execution.publish";

        /// <summary>Catalog or PostGIS registration.</summary>
        public const string Register = "raster.execution.register";

        /// <summary>Attempt scratch and uncommitted-artifact cleanup.</summary>
        public const string Cleanup = "raster.execution.cleanup";

        /// <summary>Cancellation propagation through the selected backend.</summary>
        public const string Cancel = "raster.execution.cancel";

        /// <summary>Object-store or durable-artifact I/O.</summary>
        public const string ArtifactIo = "raster.artifact.io";
    }

    /// <summary>Stable metric instrument names. Measurements carry only approved bounded tags.</summary>
    public static class Metrics
    {
        /// <summary>Planning selections, pinned-decision reuses, and refusals.</summary>
        public const string PlanningDecisions = "raster.execution.planning.decisions";

        /// <summary>Admission refusals classified by a bounded reason class.</summary>
        public const string AdmissionRejections = "raster.execution.admission.rejections";

        /// <summary>Bounded request work promoted to durable execution before mutation.</summary>
        public const string SyncToAsyncPromotions = "raster.execution.promotions";

        /// <summary>Elapsed seconds for one bounded lifecycle phase.</summary>
        public const string PhaseDuration = "raster.execution.phase.duration";

        /// <summary>Age in seconds of raster work that remains durably queued.</summary>
        public const string QueueAge = "raster.execution.queue.age";

        /// <summary>Conservative cell estimate recorded before execution.</summary>
        public const string EstimatedCells = "raster.execution.estimated.cells";

        /// <summary>Provider-reported cells actually read or written.</summary>
        public const string ActualCells = "raster.execution.actual.cells";

        /// <summary>Conservative byte estimate recorded before execution.</summary>
        public const string EstimatedBytes = "raster.execution.estimated.bytes";

        /// <summary>Provider-reported bytes actually read, decoded, or written.</summary>
        public const string ActualBytes = "raster.execution.actual.bytes";

        /// <summary>Provider-neutral database work units consumed by PostGIS raster execution.</summary>
        public const string DatabaseWork = "raster.execution.database.work";

        /// <summary>Seconds waiting to acquire a governed PostGIS raster connection.</summary>
        public const string PostgisConnectionWait = "raster.postgis.connection.wait.duration";

        /// <summary>Seconds spent executing PostGIS raster SQL.</summary>
        public const string PostgisSqlDuration = "raster.postgis.sql.duration";

        /// <summary>PostGIS temporary bytes attributable to raster execution when observable.</summary>
        public const string PostgisTemporaryBytes = "raster.postgis.temporary.bytes";

        /// <summary>Bytes transferred by an artifact read, range read, or write.</summary>
        public const string ArtifactIoBytes = "raster.artifact.io.bytes";

        /// <summary>Artifact storage requests by bounded operation and outcome.</summary>
        public const string ArtifactIoRequests = "raster.artifact.io.requests";

        /// <summary>Artifact storage request latency in seconds.</summary>
        public const string ArtifactIoDuration = "raster.artifact.io.duration";

        /// <summary>Authenticated raster-reference resolution latency in seconds.</summary>
        public const string SourceResolutionDuration = "raster.source.resolve.duration";

        /// <summary>Raster reference or range-cache operations by bounded cache result.</summary>
        public const string CacheOperations = "raster.artifact.cache.operations";

        /// <summary>Peak resident bytes reported by an isolated worker attempt.</summary>
        public const string WorkerPeakRss = "raster.worker.peak.rss";

        /// <summary>Peak scratch bytes reported by an isolated worker attempt.</summary>
        public const string WorkerPeakScratch = "raster.worker.peak.scratch";

        /// <summary>Requested AWS Batch virtual CPUs.</summary>
        public const string BatchRequestedVcpus = "raster.batch.requested.vcpus";

        /// <summary>Requested AWS Batch memory bytes.</summary>
        public const string BatchRequestedMemory = "raster.batch.requested.memory";

        /// <summary>Requested AWS Batch GPUs.</summary>
        public const string BatchRequestedGpus = "raster.batch.requested.gpus";

        /// <summary>Provider attempts observed for one raster Batch execution.</summary>
        public const string BatchAttempts = "raster.batch.attempts";

        /// <summary>Approximate Batch cost in the deployment's configured reporting currency.</summary>
        public const string BatchEstimatedCost = "raster.batch.estimated.cost";
    }

    /// <summary>Metric tag keys with explicitly bounded vocabularies.</summary>
    public static class Dimensions
    {
        /// <summary>Raster engine: <c>postgis</c>, <c>gdal-native</c>, or <c>none</c>.</summary>
        public const string Engine = "engine";

        /// <summary>Physical placement or <c>none</c>.</summary>
        public const string Placement = "placement";

        /// <summary>Bounded admission reason class.</summary>
        public const string Admission = "admission";

        /// <summary>Bounded lifecycle outcome.</summary>
        public const string Outcome = "outcome";

        /// <summary>Bounded lifecycle phase.</summary>
        public const string Phase = "phase";

        /// <summary>Bounded backend family; never a configured backend identifier.</summary>
        public const string BackendFamily = "backend_family";

        /// <summary>Bounded artifact I/O operation.</summary>
        public const string IoOperation = "io_operation";

        /// <summary>Bounded cache result.</summary>
        public const string CacheResult = "cache_result";

        /// <summary>Bounded Batch pricing model.</summary>
        public const string PricingModel = "pricing_model";
    }

    /// <summary>Trace and authenticated-audit attribute names; these are not metric dimensions.</summary>
    public static class TraceAttributes
    {
        /// <summary>Canonical process identifier.</summary>
        public const string ProcessId = "honua.raster.process_id";

        /// <summary>Selected raster engine.</summary>
        public const string Engine = "honua.raster.engine";

        /// <summary>Selected physical placement.</summary>
        public const string Placement = "honua.raster.placement";

        /// <summary>Bounded admission class.</summary>
        public const string Admission = "honua.raster.admission";

        /// <summary>Bounded outcome.</summary>
        public const string Outcome = "honua.raster.outcome";

        /// <summary>Exact controlled decision or refusal reason code.</summary>
        public const string ReasonCode = "honua.raster.reason_code";

        /// <summary>Engine-independent semantic contract version.</summary>
        public const string SemanticVersion = "honua.raster.semantic_version";

        /// <summary>Engine implementation version.</summary>
        public const string ImplementationVersion = "honua.raster.implementation_version";

        /// <summary>Operator policy snapshot reference.</summary>
        public const string PolicyRef = "honua.raster.policy_ref";

        /// <summary>Health snapshot version.</summary>
        public const string HealthVersion = "honua.raster.health_version";

        /// <summary>Configuration/budget snapshot version.</summary>
        public const string ConfigurationVersion = "honua.raster.configuration_version";
    }

    /// <summary>
    /// Returns whether a tag key is allowed on raster metrics. This is an allowlist: job,
    /// attempt, tenant, trace, process, reason, version, backend identifier, object locator,
    /// credential, and error-message keys all return <see langword="false"/>.
    /// </summary>
    /// <param name="name">Metric tag key.</param>
    /// <returns><see langword="true"/> only for the finite vocabulary above.</returns>
    public static bool IsAllowedMetricDimension(string? name) => name switch
    {
        Dimensions.Engine => true,
        Dimensions.Placement => true,
        Dimensions.Admission => true,
        Dimensions.Outcome => true,
        Dimensions.Phase => true,
        Dimensions.BackendFamily => true,
        Dimensions.IoOperation => true,
        Dimensions.CacheResult => true,
        Dimensions.PricingModel => true,
        _ => false,
    };

    /// <summary>Creates the complete bounded tag set for a planning measurement.</summary>
    /// <param name="engine">Selected engine, or null for refusal.</param>
    /// <param name="placement">Selected placement, or null for refusal.</param>
    /// <param name="admission">Bounded admission class.</param>
    /// <param name="outcome">Planning outcome.</param>
    /// <returns>A metric-safe tag list with exactly four dimensions.</returns>
    public static TagList CreatePlanningMetricTags(
        RasterEngine? engine,
        RasterExecutionPlacement? placement,
        RasterTelemetryAdmissionClass admission,
        RasterTelemetryOutcome outcome) => new()
        {
            { Dimensions.Engine, EngineValue(engine) },
            { Dimensions.Placement, PlacementValue(placement) },
            { Dimensions.Admission, AdmissionValue(admission) },
            { Dimensions.Outcome, OutcomeValue(outcome) },
        };

    /// <summary>Creates a bounded tag set for one raster lifecycle phase.</summary>
    /// <param name="engine">Selected engine.</param>
    /// <param name="placement">Selected placement.</param>
    /// <param name="phase">Lifecycle phase.</param>
    /// <param name="outcome">Phase outcome.</param>
    /// <returns>A metric-safe tag list with exactly four dimensions.</returns>
    public static TagList CreateLifecycleMetricTags(
        RasterEngine engine,
        RasterExecutionPlacement placement,
        RasterTelemetryPhase phase,
        RasterTelemetryOutcome outcome) => new()
        {
            { Dimensions.Engine, EngineValue(engine) },
            { Dimensions.Placement, PlacementValue(placement) },
            { Dimensions.Phase, PhaseValue(phase) },
            { Dimensions.Outcome, OutcomeValue(outcome) },
        };

    /// <summary>Creates a bounded tag set for artifact I/O.</summary>
    /// <param name="engine">Selected engine.</param>
    /// <param name="placement">Selected placement.</param>
    /// <param name="operation">Artifact I/O operation.</param>
    /// <param name="outcome">I/O outcome.</param>
    /// <returns>A metric-safe tag list with exactly four dimensions.</returns>
    public static TagList CreateArtifactIoMetricTags(
        RasterEngine engine,
        RasterExecutionPlacement placement,
        RasterTelemetryIoOperation operation,
        RasterTelemetryOutcome outcome) => new()
        {
            { Dimensions.Engine, EngineValue(engine) },
            { Dimensions.Placement, PlacementValue(placement) },
            { Dimensions.IoOperation, IoOperationValue(operation) },
            { Dimensions.Outcome, OutcomeValue(outcome) },
        };

    /// <summary>Creates a bounded tag set for a raster reference or range-cache operation.</summary>
    /// <param name="engine">Selected engine.</param>
    /// <param name="placement">Selected placement.</param>
    /// <param name="result">Bounded cache result.</param>
    /// <param name="outcome">Cache-operation outcome.</param>
    /// <returns>A metric-safe tag list with exactly four dimensions.</returns>
    public static TagList CreateCacheMetricTags(
        RasterEngine engine,
        RasterExecutionPlacement placement,
        RasterTelemetryCacheResult result,
        RasterTelemetryOutcome outcome) => new()
        {
            { Dimensions.Engine, EngineValue(engine) },
            { Dimensions.Placement, PlacementValue(placement) },
            { Dimensions.CacheResult, CacheResultValue(result) },
            { Dimensions.Outcome, OutcomeValue(outcome) },
        };

    /// <summary>Creates a bounded tag set for Batch resource and estimated-cost measurements.</summary>
    /// <param name="engine">Selected engine.</param>
    /// <param name="placement">Selected remote placement.</param>
    /// <param name="backendFamily">Qualified backend family.</param>
    /// <param name="pricingModel">Bounded pricing model.</param>
    /// <param name="outcome">Batch lifecycle outcome.</param>
    /// <returns>A metric-safe tag list with exactly five dimensions.</returns>
    public static TagList CreateBatchMetricTags(
        RasterEngine engine,
        RasterExecutionPlacement placement,
        RasterTelemetryBackendFamily backendFamily,
        RasterBatchPricingModel pricingModel,
        RasterTelemetryOutcome outcome) => new()
        {
            { Dimensions.Engine, EngineValue(engine) },
            { Dimensions.Placement, PlacementValue(placement) },
            { Dimensions.BackendFamily, BackendFamilyValue(backendFamily) },
            { Dimensions.PricingModel, PricingModelValue(pricingModel) },
            { Dimensions.Outcome, OutcomeValue(outcome) },
        };

    /// <summary>Maps an exact controlled planning refusal to a bounded metric class.</summary>
    /// <param name="reasonCode">Exact refusal reason retained in traces and audit.</param>
    /// <param name="isRetryable">Whether a fresh health/backend snapshot can make it eligible.</param>
    /// <returns>A bounded admission class; arbitrary values become <see cref="RasterTelemetryAdmissionClass.Unknown"/>.</returns>
    public static RasterTelemetryAdmissionClass ClassifyPlanningRefusal(
        string? reasonCode,
        bool isRetryable) => reasonCode switch
        {
            "capability-missing" => RasterTelemetryAdmissionClass.Capability,
            "mutation-decision-missing" or "mutation-decision-mismatch" =>
                RasterTelemetryAdmissionClass.Semantic,
            "no-eligible-raster-placement" when isRetryable => RasterTelemetryAdmissionClass.Health,
            // The current planner aggregates policy, format, residency, capability, and resource
            // eliminations under this code. Do not guess a more specific metric class until the
            // durable decision carries the winning elimination category explicitly.
            "no-eligible-raster-placement" => RasterTelemetryAdmissionClass.Unknown,
            _ => RasterTelemetryAdmissionClass.Unknown,
        };

    /// <summary>Returns the stable metric value for an engine or refusal.</summary>
    public static string EngineValue(RasterEngine? engine) => engine switch
    {
        RasterEngine.Postgis => "postgis",
        RasterEngine.GdalNative => "gdal-native",
        null => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown raster engine."),
    };

    /// <summary>Returns the stable metric value for a placement or refusal.</summary>
    public static string PlacementValue(RasterExecutionPlacement? placement) => placement switch
    {
        RasterExecutionPlacement.Request => "request",
        RasterExecutionPlacement.DurablePostgis => "durable-postgis",
        RasterExecutionPlacement.LocalNativeWorker => "local-native-worker",
        RasterExecutionPlacement.RemoteBackend => "remote-backend",
        null => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown raster placement."),
    };

    /// <summary>Returns the stable metric value for an admission class.</summary>
    public static string AdmissionValue(RasterTelemetryAdmissionClass admission) => admission switch
    {
        RasterTelemetryAdmissionClass.Accepted => "accepted",
        RasterTelemetryAdmissionClass.Capability => "capability",
        RasterTelemetryAdmissionClass.Policy => "policy",
        RasterTelemetryAdmissionClass.Health => "health",
        RasterTelemetryAdmissionClass.Capacity => "capacity",
        RasterTelemetryAdmissionClass.Resource => "resource",
        RasterTelemetryAdmissionClass.Security => "security",
        RasterTelemetryAdmissionClass.Semantic => "semantic",
        RasterTelemetryAdmissionClass.Input => "input",
        RasterTelemetryAdmissionClass.Backend => "backend",
        RasterTelemetryAdmissionClass.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(admission), admission, "Unknown raster admission class."),
    };

    /// <summary>Returns the stable metric value for an outcome.</summary>
    public static string OutcomeValue(RasterTelemetryOutcome outcome) => outcome switch
    {
        RasterTelemetryOutcome.Selected => "selected",
        RasterTelemetryOutcome.Reused => "reused",
        RasterTelemetryOutcome.Refused => "refused",
        RasterTelemetryOutcome.Succeeded => "succeeded",
        RasterTelemetryOutcome.Failed => "failed",
        RasterTelemetryOutcome.Cancelled => "cancelled",
        RasterTelemetryOutcome.TimedOut => "timed-out",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown raster outcome."),
    };

    /// <summary>Returns the stable metric value for a lifecycle phase.</summary>
    public static string PhaseValue(RasterTelemetryPhase phase) => phase switch
    {
        RasterTelemetryPhase.Plan => "plan",
        RasterTelemetryPhase.Queue => "queue",
        RasterTelemetryPhase.Provision => "provision",
        RasterTelemetryPhase.Execute => "execute",
        RasterTelemetryPhase.Publish => "publish",
        RasterTelemetryPhase.Register => "register",
        RasterTelemetryPhase.Cleanup => "cleanup",
        RasterTelemetryPhase.Cancel => "cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown raster lifecycle phase."),
    };

    /// <summary>Returns the stable metric value for a backend family.</summary>
    public static string BackendFamilyValue(RasterTelemetryBackendFamily family) => family switch
    {
        RasterTelemetryBackendFamily.Request => "request",
        RasterTelemetryBackendFamily.Postgis => "postgis",
        RasterTelemetryBackendFamily.LocalNative => "local-native",
        RasterTelemetryBackendFamily.AwsBatch => "aws-batch",
        RasterTelemetryBackendFamily.OtherRemote => "other-remote",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown raster backend family."),
    };

    /// <summary>Returns the stable metric value for an artifact I/O operation.</summary>
    public static string IoOperationValue(RasterTelemetryIoOperation operation) => operation switch
    {
        RasterTelemetryIoOperation.Read => "read",
        RasterTelemetryIoOperation.RangeRead => "range-read",
        RasterTelemetryIoOperation.Write => "write",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown raster I/O operation."),
    };

    /// <summary>Returns the stable metric value for a cache result.</summary>
    public static string CacheResultValue(RasterTelemetryCacheResult result) => result switch
    {
        RasterTelemetryCacheResult.Hit => "hit",
        RasterTelemetryCacheResult.Miss => "miss",
        RasterTelemetryCacheResult.Bypass => "bypass",
        RasterTelemetryCacheResult.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown raster cache result."),
    };

    /// <summary>Returns the stable metric value for a Batch pricing model.</summary>
    public static string PricingModelValue(RasterBatchPricingModel model) => model switch
    {
        RasterBatchPricingModel.OnDemand => "on-demand",
        RasterBatchPricingModel.Spot => "spot",
        RasterBatchPricingModel.Blended => "blended",
        RasterBatchPricingModel.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown raster Batch pricing model."),
    };
}

/// <summary>
/// Provider-neutral conservative estimate captured before raster execution. It deliberately
/// excludes process identifiers and free-form unknown-field names; those remain in the pinned
/// planning decision rather than the metric/audit measurement payload.
/// </summary>
public sealed record RasterExecutionEstimateSummary
{
    /// <summary>Estimated number of independent input sources.</summary>
    public required long SourceCount { get; init; }

    /// <summary>Estimated number of bands read across all inputs.</summary>
    public required long BandCount { get; init; }

    /// <summary>Estimated vector-zone count, or zero when the process is not zonal.</summary>
    public required long ZoneCount { get; init; }

    /// <summary>Estimated input cells examined.</summary>
    public required long InputCells { get; init; }

    /// <summary>Estimated output cells produced.</summary>
    public required long OutputCells { get; init; }

    /// <summary>Estimated decoded bytes consumed by the selected engine.</summary>
    public required long DecodedBytes { get; init; }

    /// <summary>Estimated scratch bytes required by the selected engine.</summary>
    public required long ExpectedScratchBytes { get; init; }

    /// <summary>Estimated provider-neutral database work units.</summary>
    public required long ExpectedDatabaseWork { get; init; }

    /// <summary>Whether at least one value is the conservative unknown sentinel.</summary>
    public required bool UsesConservativeValues { get; init; }
}

/// <summary>
/// Provider-neutral actual resource, duration, and I/O summary for one winning raster attempt.
/// Null means the executor could not observe the value; it never means zero.
/// </summary>
public sealed record RasterExecutionActualSummary
{
    /// <summary>Actual input cells examined.</summary>
    public long? InputCells { get; init; }

    /// <summary>Actual output cells produced.</summary>
    public long? OutputCells { get; init; }

    /// <summary>Actual decoded bytes consumed by the engine.</summary>
    public long? DecodedBytes { get; init; }

    /// <summary>Actual output artifact bytes before optional downstream materialization.</summary>
    public long? OutputBytes { get; init; }

    /// <summary>Actual provider-neutral database work units.</summary>
    public long? DatabaseWork { get; init; }

    /// <summary>PostGIS connection-pool wait in seconds.</summary>
    public double? PostgisConnectionWaitSeconds { get; init; }

    /// <summary>PostGIS raster SQL execution in seconds.</summary>
    public double? PostgisSqlSeconds { get; init; }

    /// <summary>PostGIS temporary bytes when the provider exposes them safely.</summary>
    public long? PostgisTemporaryBytes { get; init; }

    /// <summary>Object and artifact bytes read, including range reads.</summary>
    public long? ArtifactReadBytes { get; init; }

    /// <summary>Object and artifact bytes written to attempt-scoped locations.</summary>
    public long? ArtifactWriteBytes { get; init; }

    /// <summary>Object and artifact request count.</summary>
    public long? ArtifactRequestCount { get; init; }

    /// <summary>Subset of artifact requests issued as byte-range reads.</summary>
    public long? ArtifactRangeRequestCount { get; init; }

    /// <summary>Raster reference or range-cache hits.</summary>
    public long? CacheHitCount { get; init; }

    /// <summary>Raster reference or range-cache misses.</summary>
    public long? CacheMissCount { get; init; }

    /// <summary>Peak worker resident bytes.</summary>
    public long? PeakRssBytes { get; init; }

    /// <summary>Peak worker scratch bytes.</summary>
    public long? PeakScratchBytes { get; init; }

    /// <summary>Durable queue wait in seconds.</summary>
    public double? QueueSeconds { get; init; }

    /// <summary>Worker claim, source resolution, and provisioning in seconds.</summary>
    public double? ProvisioningSeconds { get; init; }

    /// <summary>Provider or native algorithm execution in seconds.</summary>
    public double? RunSeconds { get; init; }

    /// <summary>Output validation and attempt-fenced publication in seconds.</summary>
    public double? PublicationSeconds { get; init; }

    /// <summary>Catalog or PostGIS registration in seconds.</summary>
    public double? RegistrationSeconds { get; init; }

    /// <summary>Cancellation propagation latency in seconds.</summary>
    public double? CancellationSeconds { get; init; }
}

/// <summary>
/// Requested resources and explicitly approximate cost evidence for remote raster execution.
/// This is not a cloud invoice or a billing-reconciliation contract.
/// </summary>
public sealed record RasterBatchCostMetadata
{
    /// <summary>Requested virtual CPUs.</summary>
    public double? RequestedVcpus { get; init; }

    /// <summary>Requested memory bytes.</summary>
    public long? RequestedMemoryBytes { get; init; }

    /// <summary>Requested GPU count.</summary>
    public int? RequestedGpuCount { get; init; }

    /// <summary>Requested ephemeral/scratch bytes.</summary>
    public long? RequestedScratchBytes { get; init; }

    /// <summary>Provider attempts observed for the execution.</summary>
    public int? AttemptCount { get; init; }

    /// <summary>Provider-observed billable or running seconds when available.</summary>
    public double? ObservedRunSeconds { get; init; }

    /// <summary>Approximate cost in <see cref="CurrencyCode"/>; never an invoice amount.</summary>
    public decimal? EstimatedCost { get; init; }

    /// <summary>ISO 4217 reporting currency for <see cref="EstimatedCost"/>.</summary>
    public string? CurrencyCode { get; init; }

    /// <summary>Bounded pricing model used by the estimate.</summary>
    public RasterBatchPricingModel PricingModel { get; init; } = RasterBatchPricingModel.Unknown;

    /// <summary>Provider price-list, rate-card, or operator model version used by the estimate.</summary>
    public string? PricingVersion { get; init; }

    /// <summary>UTC time at which the estimate inputs were priced.</summary>
    public DateTimeOffset? PricedAt { get; init; }
}

/// <summary>
/// Versioned, provider-neutral execution summary for authenticated job/audit projections.
/// It intentionally contains no job, attempt, tenant, object-locator, credential, token,
/// connection-string, SQL-text, or native-command fields; correlation remains in the outer
/// durable job/audit envelope.
/// </summary>
public sealed record RasterExecutionTelemetrySummary
{
    /// <summary>Schema version of this summary.</summary>
    public int Version { get; init; } = RasterExecutionTelemetry.ContractVersion;

    /// <summary>Engine bound to the winning attempt.</summary>
    public required RasterEngine Engine { get; init; }

    /// <summary>Placement bound to the winning attempt.</summary>
    public required RasterExecutionPlacement Placement { get; init; }

    /// <summary>Bounded family of the backend that ran the winning attempt.</summary>
    public required RasterTelemetryBackendFamily BackendFamily { get; init; }

    /// <summary>Bounded input-residency values in stable input order.</summary>
    public required IReadOnlyList<RasterInputResidency> InputResidencies { get; init; }

    /// <summary>Bounded admission class.</summary>
    public required RasterTelemetryAdmissionClass Admission { get; init; }

    /// <summary>Bounded terminal or planning outcome.</summary>
    public required RasterTelemetryOutcome Outcome { get; init; }

    /// <summary>Exact controlled decision/outcome code for authenticated audit and trace use.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Engine-independent raster semantic contract version.</summary>
    public required string SemanticVersion { get; init; }

    /// <summary>Selected engine implementation version.</summary>
    public required string ImplementationVersion { get; init; }

    /// <summary>Conservative estimate pinned before execution.</summary>
    public required RasterExecutionEstimateSummary Estimate { get; init; }

    /// <summary>Actual resource, duration, and I/O values from the winning attempt.</summary>
    public required RasterExecutionActualSummary Actual { get; init; }

    /// <summary>Approximate Batch resource and cost evidence for remote execution.</summary>
    public RasterBatchCostMetadata? Batch { get; init; }
}
