// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Eval;

public sealed record EvalReport
{
    [JsonPropertyName("reportSchemaVersion")]
    public string ReportSchemaVersion { get; init; } = EvalReportSchema.Version;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("environment")]
    public EvalReportEnvironment Environment { get; init; } = new();

    [JsonPropertyName("scenarios")]
    public IReadOnlyList<EvalScenarioResult> Scenarios { get; init; } = [];

    [JsonPropertyName("rollup")]
    public EvalReportRollup Rollup { get; init; } = new();
}

public static class EvalReportSchema
{
    public const string Version = "1";
}

public sealed record EvalReportEnvironment
{
    [JsonPropertyName("corpusVersion")]
    public string CorpusVersion { get; init; } = "local-seed";

    [JsonPropertyName("corpusSource")]
    public string CorpusSource { get; init; } = "local-seed";

    [JsonPropertyName("redisAvailable")]
    public bool RedisAvailable { get; init; }

    [JsonPropertyName("corpusPath")]
    public string? CorpusPath { get; init; }
}

public sealed record EvalReportRollup
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("passedWithSkips")]
    public int PassedWithSkips { get; init; }

    [JsonPropertyName("firstFailure")]
    public string? FirstFailure { get; init; }

    [JsonPropertyName("totalElapsedMs")]
    public long TotalElapsedMs { get; init; }
}

public sealed record EvalScenarioResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public EvalScenarioMode Mode { get; init; }

    [JsonPropertyName("status")]
    public EvalOverallStatus Status { get; init; }

    [JsonPropertyName("stages")]
    public IReadOnlyList<EvalStageOutcome> Stages { get; init; } = [];

    [JsonPropertyName("protocolParity")]
    public EvalProtocolParityOutcome ProtocolParity { get; init; } = new();

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }
}

public sealed record EvalStageOutcome
{
    [JsonPropertyName("stage")]
    public EvalStageKind Stage { get; init; }

    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record EvalProtocolParityOutcome
{
    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; } = EvalStageStatus.Skipped;

    [JsonPropertyName("probes")]
    public IReadOnlyList<EvalProtocolProbe> Probes { get; init; } = [];

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record EvalProtocolProbe
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("assertion")]
    public string Assertion { get; init; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public EvalStageStatus Status { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EvalStageKind>))]
public enum EvalStageKind
{
    CaptureIntent,

    CompilePlan,

    ValidatePlan,

    DryRun,

    ProtocolParity,

    SubmitJob,

    PollJob,

    GetJobResult,

    ComposeMapPackage,

    ComposeAppPackage,

    PromoteDeployment
}

[JsonConverter(typeof(JsonStringEnumConverter<EvalStageStatus>))]
public enum EvalStageStatus
{
    Pending,

    Passed,

    Skipped,

    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<EvalOverallStatus>))]
public enum EvalOverallStatus
{
    Passed,

    Failed,

    PassedWithSkips
}
