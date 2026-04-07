// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Cross-client certification evidence envelope (schema v1.0).
/// Mirrors the JSON shape defined in
/// <c>docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md</c>.
/// </summary>
/// <remarks>
/// Field ordering matches the spec example so a pretty-printed envelope is
/// trivially diff-able against the manual template. Properties typed as
/// nullable are serialized with explicit <c>null</c> values rather than
/// being omitted, per the "nullable field convention" in the spec.
/// </remarks>
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

/// <summary>
/// Result row for one common-core CERT-* test case in a certification envelope.
/// </summary>
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

/// <summary>
/// Aggregated counts for an envelope's <see cref="CertEnvelope.Results"/>.
/// Lane-specific extensions are tracked separately on
/// <see cref="CertEnvelope.Extensions"/> per the spec.
/// </summary>
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

/// <summary>
/// Lane-specific extension result row (e.g., CLI-EXT-01, BI-EXT-02).
/// </summary>
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

/// <summary>
/// Source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
/// for the certification envelope. Keeps the helper AOT/trim-friendly so it can be
/// reused outside test projects without runtime reflection.
/// </summary>
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
