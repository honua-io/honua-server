// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Monitoring;

/// <summary>Versioned, additive evidence-integrity contract shared by REST and MCP ops reads.</summary>
public sealed class EvidencePosture
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<EvidenceSourceEnvelope> Sources { get; init; }
}

/// <summary>Bounded evidence about one implementation actually queried for an ops read.</summary>
public sealed class EvidenceSourceEnvelope
{
    [JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [JsonPropertyName("backendKind")]
    public required string BackendKind { get; init; }

    [JsonPropertyName("backendId")]
    public string? BackendId { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }

    [JsonPropertyName("lastSuccessfulAt")]
    public DateTimeOffset? LastSuccessfulAt { get; init; }

    [JsonPropertyName("completeness")]
    public required string Completeness { get; init; }

    [JsonPropertyName("reasonCodes")]
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    [JsonPropertyName("coverage")]
    public EvidenceSourceCoverage? Coverage { get; init; }

    [JsonPropertyName("maximumAgeSeconds")]
    public long? MaximumAgeSeconds { get; init; }

    [JsonPropertyName("validUntil")]
    public DateTimeOffset? ValidUntil { get; init; }
}

/// <summary>Optional bounded coverage dimensions. Unknown values remain null.</summary>
public sealed class EvidenceSourceCoverage
{
    [JsonPropertyName("requestedFrom")]
    public DateTimeOffset? RequestedFrom { get; init; }

    [JsonPropertyName("requestedTo")]
    public DateTimeOffset? RequestedTo { get; init; }

    [JsonPropertyName("returnedFrom")]
    public DateTimeOffset? ReturnedFrom { get; init; }

    [JsonPropertyName("returnedTo")]
    public DateTimeOffset? ReturnedTo { get; init; }

    [JsonPropertyName("page")]
    public int? Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; init; }

    [JsonPropertyName("hasMore")]
    public bool? HasMore { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }

    [JsonPropertyName("observedReplicaCount")]
    public int? ObservedReplicaCount { get; init; }

    [JsonPropertyName("expectedReplicaCount")]
    public int? ExpectedReplicaCount { get; init; }

    [JsonPropertyName("includedComponentIds")]
    public IReadOnlyList<string>? IncludedComponentIds { get; init; }

    [JsonPropertyName("expectedComponentIds")]
    public IReadOnlyList<string>? ExpectedComponentIds { get; init; }
}

/// <summary>Closed wire vocabularies for evidence envelopes.</summary>
public static class EvidencePostureVocabulary
{
    public static class Completeness
    {
        public const string Complete = "complete";
        public const string Partial = "partial";
        public const string Unavailable = "unavailable";
        public const string NotConfigured = "notConfigured";
    }

    public static class BackendKinds
    {
        public const string InProcess = "inProcess";
        public const string DurableStore = "durableStore";
        public const string ConfigProjection = "configProjection";
        public const string Composite = "composite";
        public const string Unverified = "unverified";
    }

    public static class ReasonCodes
    {
        public const string SourceUnavailable = "sourceUnavailable";
        public const string NeverSucceeded = "neverSucceeded";
        public const string Stale = "stale";
        public const string MissingObservationTime = "missingObservationTime";
        public const string MalformedObservationTime = "malformedObservationTime";
        public const string FutureObservationTime = "futureObservationTime";
        public const string PartialResult = "partialResult";
        public const string IncompleteCoverage = "incompleteCoverage";
        public const string Truncated = "truncated";
        public const string BackendUnverified = "backendUnverified";
        public const string NotConfigured = "notConfigured";
    }

    public static class SourceIds
    {
        public const string OpsHealth = "honua_ops_health";
        public const string OpsHealthChecks = "honua_ops_health.health_checks";
        public const string ServingLatency = "honua_ops_health.serving_latency";
        public const string GpQueue = "honua_ops_health.gp_queue";
        public const string AlertDispatch = "honua_ops_health.alert_dispatch";
        public const string DeployRelease = "honua_ops_health.deploy_release";
        public const string DatabaseCache = "honua_ops_health.database_cache";
        public const string Findings = "honua_ops_findings";
        public const string AlertEvents = "honua_alert_events";
        public const string OperateEvents = "honua_operate_events";
        public const string PlatformReleaseStatus = "honua_platform_release_status";
        public const string DeployOperations = "honua_deploy_operations";
    }
}

internal static class EvidencePostureFactory
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    public static EvidenceSourceEnvelope Complete(
        string sourceId,
        string backendKind,
        string backendId,
        DateTimeOffset observedAt,
        TimeSpan maximumAge,
        EvidenceSourceCoverage? coverage = null) => new()
        {
            SourceId = sourceId,
            BackendKind = backendKind,
            BackendId = backendId,
            ObservedAt = observedAt.ToUniversalTime(),
            LastSuccessfulAt = observedAt.ToUniversalTime(),
            Completeness = EvidencePostureVocabulary.Completeness.Complete,
            Coverage = coverage,
            MaximumAgeSeconds = (long)maximumAge.TotalSeconds,
            ValidUntil = observedAt.ToUniversalTime().Add(maximumAge),
        };

    public static EvidencePosture Build(DateTimeOffset evaluatedAt, params EvidenceSourceEnvelope[] sources)
    {
        var normalized = sources.Select(source => Validate(source, evaluatedAt)).ToArray();
        return new EvidencePosture
        {
            Status = normalized.All(IsActionable)
                ? EvidencePostureVocabulary.Completeness.Complete
                : EvidencePostureVocabulary.Completeness.Partial,
            Sources = normalized,
        };
    }

    public static bool IsActionable(EvidenceSourceEnvelope source) =>
        source.Completeness == EvidencePostureVocabulary.Completeness.Complete &&
        source.BackendKind != EvidencePostureVocabulary.BackendKinds.Unverified &&
        !string.IsNullOrWhiteSpace(source.BackendId) &&
        source.ObservedAt is not null &&
        source.LastSuccessfulAt is not null &&
        source.ReasonCodes.Count == 0;

    public static EvidenceSourceEnvelope Validate(EvidenceSourceEnvelope source, DateTimeOffset evaluatedAt)
    {
        var reasons = new HashSet<string>(source.ReasonCodes, StringComparer.Ordinal);
        if (source.ObservedAt is null)
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.MissingObservationTime);
        }
        else if (source.ObservedAt > evaluatedAt + ClockSkew)
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.FutureObservationTime);
        }

        if (source.LastSuccessfulAt is null)
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.NeverSucceeded);
        }

        if (source.ObservedAt is { } observed && source.LastSuccessfulAt is { } successful && observed > successful + ClockSkew)
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.MalformedObservationTime);
        }

        if (source.ValidUntil is { } validUntil && evaluatedAt > validUntil)
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.Stale);
        }

        if (source.BackendKind == EvidencePostureVocabulary.BackendKinds.Unverified || string.IsNullOrWhiteSpace(source.BackendId))
        {
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.BackendUnverified);
        }

        if (source.Coverage is { } coverage)
        {
            if (coverage.RequestedFrom > coverage.RequestedTo || coverage.ReturnedFrom > coverage.ReturnedTo)
            {
                reasons.Add(EvidencePostureVocabulary.ReasonCodes.MalformedObservationTime);
            }

            if (coverage.ExpectedReplicaCount is { } expected && coverage.ObservedReplicaCount is { } observedReplicas && observedReplicas < expected)
            {
                reasons.Add(EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage);
            }

            if (coverage.Truncated is true || coverage.HasMore is true)
            {
                reasons.Add(EvidencePostureVocabulary.ReasonCodes.Truncated);
            }
        }

        return new EvidenceSourceEnvelope
        {
            SourceId = source.SourceId,
            BackendKind = source.BackendKind,
            BackendId = source.BackendId,
            ObservedAt = source.ObservedAt?.ToUniversalTime(),
            LastSuccessfulAt = source.LastSuccessfulAt?.ToUniversalTime(),
            Completeness = reasons.Count == 0 ? source.Completeness : EvidencePostureVocabulary.Completeness.Unavailable,
            ReasonCodes = reasons.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Coverage = source.Coverage,
            MaximumAgeSeconds = source.MaximumAgeSeconds,
            ValidUntil = source.ValidUntil?.ToUniversalTime(),
        };
    }
}
