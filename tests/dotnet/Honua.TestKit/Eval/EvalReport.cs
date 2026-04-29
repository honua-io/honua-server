// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Eval;

/// <summary>
/// Report artifact produced by <see cref="EvalRunner"/> and consumed by downstream
/// devops automation (honua-devops-31) as the canonical server-side integration gate.
/// </summary>
public sealed record EvalReport
{
    /// <summary>Schema version of this report document.</summary>
    [JsonPropertyName("reportSchemaVersion")]
    public string ReportSchemaVersion { get; init; } = EvalReportSchema.Version;

    /// <summary>Timestamp the report was emitted.</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Environment context captured at run time.</summary>
    [JsonPropertyName("environment")]
    public EvalReportEnvironment Environment { get; init; } = new();

    /// <summary>Per-scenario result rows.</summary>
    [JsonPropertyName("scenarios")]
    public IReadOnlyList<EvalScenarioResult> Scenarios { get; init; } = [];

    /// <summary>Top-level rollup for quick CI readouts.</summary>
    [JsonPropertyName("rollup")]
    public EvalReportRollup Rollup { get; init; } = new();
}

/// <summary>Schema version constant for the eval report.</summary>
public static class EvalReportSchema
{
    /// <summary>Current schema version.</summary>
    public const string Version = "1";
}

/// <summary>Environment context captured at run time.</summary>
public sealed record EvalReportEnvironment
{
    /// <summary>Corpus version string (from shared corpus metadata or <c>local-seed</c>).</summary>
    [JsonPropertyName("corpusVersion")]
    public string CorpusVersion { get; init; } = "local-seed";

    /// <summary>Corpus source identifier: <c>shared</c> or <c>local-seed</c>.</summary>
    [JsonPropertyName("corpusSource")]
    public string CorpusSource { get; init; } = "local-seed";

    /// <summary>Whether a Redis fixture was available during the run.</summary>
    [JsonPropertyName("redisAvailable")]
    public bool RedisAvailable { get; init; }

    /// <summary>Resolved filesystem path of the corpus root used, when applicable.</summary>
    [JsonPropertyName("corpusPath")]
    public string? CorpusPath { get; init; }
}

/// <summary>Top-level rollup for quick CI readouts.</summary>
public sealed record EvalReportRollup
{
    /// <summary>Total scenarios executed.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>Scenarios that passed all stages.</summary>
    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    /// <summary>Scenarios where at least one stage failed.</summary>
    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    /// <summary>Scenarios where all non-skipped stages passed but some were skipped.</summary>
    [JsonPropertyName("passedWithSkips")]
    public int PassedWithSkips { get; init; }

    /// <summary>Pointer to the first failed scenario, if any.</summary>
    [JsonPropertyName("firstFailure")]
    public string? FirstFailure { get; init; }

    /// <summary>Combined wall-clock duration across scenarios, in milliseconds.</summary>
    [JsonPropertyName("totalElapsedMs")]
    public long TotalElapsedMs { get; init; }
}

/// <summary>Per-scenario result row.</summary>
public sealed record EvalScenarioResult
{
    /// <summary>Scenario identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Scenario human-readable name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Scenario mode.</summary>
    [JsonPropertyName("mode")]
    public EvalScenarioMode Mode { get; init; }

    /// <summary>Overall status: <c>Passed</c>, <c>Failed</c>, or <c>PassedWithSkips</c>.</summary>
    [JsonPropertyName("status")]
    public EvalOverallStatus Status { get; init; }

    /// <summary>Per-stage outcomes, in execution order.</summary>
    [JsonPropertyName("stages")]
    public IReadOnlyList<EvalStageOutcome> Stages { get; init; } = [];

    /// <summary>Protocol parity summary across gRPC and REST adapters.</summary>
    [JsonPropertyName("protocolParity")]
    public EvalProtocolParityOutcome ProtocolParity { get; init; } = new();

    /// <summary>Wall-clock duration of the scenario, in milliseconds.</summary>
    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }
}

/// <summary>Per-stage outcome for a scenario.</summary>
public sealed record EvalStageOutcome
{
    /// <summary>Stage identifier.</summary>
    [JsonPropertyName("stage")]
    public EvalStageKind Stage { get; init; }

    /// <summary>Stage status.</summary>
    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; }

    /// <summary>Reason text for skipped or failed stages.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Stage wall-clock duration, in milliseconds.</summary>
    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    /// <summary>Captured diagnostic detail when the stage failed.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>Protocol-parity outcome for a scenario.</summary>
public sealed record EvalProtocolParityOutcome
{
    /// <summary>Overall parity status.</summary>
    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; } = EvalStageStatus.Skipped;

    /// <summary>Protocol comparison rows keyed by probe name.</summary>
    [JsonPropertyName("probes")]
    public IReadOnlyList<EvalProtocolProbe> Probes { get; init; } = [];

    /// <summary>Reason describing any divergence between probes.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Single protocol probe outcome (one protocol × one assertion).</summary>
public sealed record EvalProtocolProbe
{
    /// <summary>Protocol identifier (matches <see cref="Constants.Protocols"/>).</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Probe label (e.g. <c>plan-shape-accepted</c>).</summary>
    [JsonPropertyName("assertion")]
    public string Assertion { get; init; } = string.Empty;

    /// <summary>Observed outcome for this probe.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Probe-specific status.</summary>
    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; }
}

/// <summary>Stage identifiers tracked in the report.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalStageKind>))]
public enum EvalStageKind
{
    /// <summary>Capture an analyst intent.</summary>
    CaptureIntent,

    /// <summary>Compile an executable plan from the intent.</summary>
    CompilePlan,

    /// <summary>Validate the plan through the canonical runtime.</summary>
    ValidatePlan,

    /// <summary>Dry-run the plan through the canonical runtime.</summary>
    DryRun,

    /// <summary>Cross-check plan acceptance across REST protocol adapters.</summary>
    ProtocolParity,

    /// <summary>Submit the plan for asynchronous execution.</summary>
    SubmitJob,

    /// <summary>Poll the execution job until it reaches a terminal state.</summary>
    PollJob,

    /// <summary>Retrieve and validate the result package shape.</summary>
    GetJobResult,

    /// <summary>Compose and validate the map package binding.</summary>
    ComposeMapPackage,

    /// <summary>Compose and validate the app package binding.</summary>
    ComposeAppPackage,

    /// <summary>Run the publish and deployment promotion flow.</summary>
    PromoteDeployment
}

/// <summary>Stage outcome status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalStageStatus>))]
public enum EvalStageStatus
{
    /// <summary>Stage not yet executed.</summary>
    Pending,

    /// <summary>Stage completed with expected outcomes.</summary>
    Passed,

    /// <summary>Stage was intentionally skipped (reason captured).</summary>
    Skipped,

    /// <summary>Stage failed an assertion or raised an unexpected exception.</summary>
    Failed
}

/// <summary>Overall scenario status rollup.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalOverallStatus>))]
public enum EvalOverallStatus
{
    /// <summary>All stages passed.</summary>
    Passed,

    /// <summary>At least one stage failed.</summary>
    Failed,

    /// <summary>All executed stages passed but some were skipped.</summary>
    PassedWithSkips
}
