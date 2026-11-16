// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Infrastructure;

public sealed record CertEnvelope
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("run_date")]
    public string RunDate { get; init; } = string.Empty;

    [JsonPropertyName("server_version")]
    public string ServerVersion { get; init; } = string.Empty;

    [JsonPropertyName("client_lane")]
    public string ClientLane { get; init; } = string.Empty;

    [JsonPropertyName("client_version")]
    public string ClientVersion { get; init; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    [JsonPropertyName("results")]
    public IReadOnlyList<CertResult> Results { get; init; } = [];

    [JsonPropertyName("summary")]
    public CertSummary Summary { get; init; } = new();

    [JsonPropertyName("cite_results")]
    public string? CiteResults { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyList<CertExtension> Extensions { get; init; } = [];
}

public sealed record CertResult
{
    [JsonPropertyName("test_case_id")]
    public string TestCaseId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("measured_count")]
    public long? MeasuredCount { get; init; }

    [JsonPropertyName("measured_delta")]
    public double? MeasuredDelta { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("evidence_ref")]
    public string EvidenceRef { get; init; } = string.Empty;
}

public sealed record CertSummary
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("not_applicable")]
    public int NotApplicable { get; init; }
}

public sealed record CertExtension
{
    [JsonPropertyName("test_case_id")]
    public string TestCaseId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("measured_count")]
    public long? MeasuredCount { get; init; }

    [JsonPropertyName("measured_delta")]
    public double? MeasuredDelta { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("evidence_ref")]
    public string EvidenceRef { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(CertEnvelope))]
[JsonSerializable(typeof(CertResult))]
[JsonSerializable(typeof(CertSummary))]
[JsonSerializable(typeof(CertExtension))]
public sealed partial class CertEnvelopeJsonContext : JsonSerializerContext
{
}
