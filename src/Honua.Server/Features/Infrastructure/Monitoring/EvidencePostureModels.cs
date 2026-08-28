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
        public const string FindingsAlertDispatch = "honua_ops_findings.alert_dispatch";
        public const string FindingsControlPlane = "honua_ops_findings.control_plane";
        public const string FindingsDeployPreflight = "honua_ops_findings.deploy_preflight";
        public const string FindingsGpQueue = "honua_ops_findings.gp_queue";
        public const string FindingsWorkflowOperations = "honua_ops_findings.workflow_operations";
        public const string FindingsServingLatencyRollup = "honua_ops_findings.serving_latency_rollup";
        public const string FindingsDatabasePressure = "honua_ops_findings.database_pressure";
        public const string FindingsBatchBackends = "honua_ops_findings.batch_backends";
        public const string AlertEvents = "honua_alert_events";
        public const string OperateEvents = "honua_operate_events";
        public const string PlatformReleaseStatus = "honua_platform_release_status";
        public const string DeployOperations = "honua_deploy_operations";
    }
}

internal static class EvidencePostureFactory
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reason codes that describe incomplete coverage of an otherwise successful collection. A source
    /// carrying only these reasons still returned valid observations, so it degrades to
    /// <c>partial</c> rather than <c>unavailable</c>. Any other reason code means the configured
    /// backend produced no evidence that can be trusted at all.
    /// </summary>
    private static readonly HashSet<string> CoverageReasonCodes = new(StringComparer.Ordinal)
    {
        EvidencePostureVocabulary.ReasonCodes.PartialResult,
        EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage,
        EvidencePostureVocabulary.ReasonCodes.Truncated,
    };

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

    /// <summary>
    /// A configured backend that could not supply valid evidence. Missing timestamps stay missing;
    /// they are never replaced with the response time.
    /// </summary>
    public static EvidenceSourceEnvelope Unavailable(
        string sourceId,
        string backendKind,
        string backendId,
        string reasonCode,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? lastSuccessfulAt = null,
        TimeSpan? maximumAge = null,
        EvidenceSourceCoverage? coverage = null) => new()
        {
            SourceId = sourceId,
            BackendKind = backendKind,
            BackendId = backendId,
            ObservedAt = observedAt?.ToUniversalTime(),
            LastSuccessfulAt = lastSuccessfulAt?.ToUniversalTime(),
            Completeness = EvidencePostureVocabulary.Completeness.Unavailable,
            ReasonCodes = [reasonCode],
            Coverage = coverage,
            MaximumAgeSeconds = maximumAge is { } age ? (long)age.TotalSeconds : null,
        };

    /// <summary>A source that returned valid observations with known-incomplete coverage.</summary>
    public static EvidenceSourceEnvelope Partial(
        string sourceId,
        string backendKind,
        string backendId,
        DateTimeOffset observedAt,
        TimeSpan maximumAge,
        string reasonCode,
        EvidenceSourceCoverage? coverage = null) => new()
        {
            SourceId = sourceId,
            BackendKind = backendKind,
            BackendId = backendId,
            ObservedAt = observedAt.ToUniversalTime(),
            LastSuccessfulAt = observedAt.ToUniversalTime(),
            Completeness = EvidencePostureVocabulary.Completeness.Partial,
            ReasonCodes = [reasonCode],
            Coverage = coverage,
            MaximumAgeSeconds = (long)maximumAge.TotalSeconds,
            ValidUntil = observedAt.ToUniversalTime().Add(maximumAge),
        };

    /// <summary>
    /// A source with no configured backend at all. Distinct from <see cref="Unavailable"/>: nothing
    /// was queried, so there is neither an observation nor a collection-health signal to report.
    /// </summary>
    public static EvidenceSourceEnvelope NotConfigured(string sourceId, string backendKind, string backendId) => new()
    {
        SourceId = sourceId,
        BackendKind = backendKind,
        BackendId = backendId,
        Completeness = EvidencePostureVocabulary.Completeness.NotConfigured,
        ReasonCodes = [EvidencePostureVocabulary.ReasonCodes.NotConfigured],
    };

    /// <summary>
    /// Composes a top-level source over already-validated component sources. The aggregate never
    /// claims more than its weakest component and publishes which component ids it actually covered.
    /// </summary>
    public static EvidenceSourceEnvelope Aggregate(
        string sourceId,
        string backendId,
        TimeSpan maximumAge,
        IReadOnlyList<EvidenceSourceEnvelope> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var expected = components.Select(component => component.SourceId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var included = components.Where(IsActionable).Select(component => component.SourceId)
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var coverage = new EvidenceSourceCoverage
        {
            IncludedComponentIds = included,
            ExpectedComponentIds = expected,
        };

        // The aggregate is only as fresh as the oldest component it actually summarizes. Individual
        // reason codes stay on the components that earned them; the aggregate reports coverage.
        var componentObservations = components
            .Where(IsActionable)
            .Select(component => component.ObservedAt)
            .OfType<DateTimeOffset>()
            .ToArray();
        if (componentObservations.Length == 0)
        {
            return Unavailable(
                sourceId,
                EvidencePostureVocabulary.BackendKinds.Composite,
                backendId,
                EvidencePostureVocabulary.ReasonCodes.SourceUnavailable,
                maximumAge: maximumAge,
                coverage: coverage);
        }

        var aggregateObservedAt = componentObservations.Min().ToUniversalTime();
        return new EvidenceSourceEnvelope
        {
            SourceId = sourceId,
            BackendKind = EvidencePostureVocabulary.BackendKinds.Composite,
            BackendId = backendId,
            ObservedAt = aggregateObservedAt,
            LastSuccessfulAt = aggregateObservedAt,
            Completeness = included.Length == expected.Length
                ? EvidencePostureVocabulary.Completeness.Complete
                : EvidencePostureVocabulary.Completeness.Partial,
            Coverage = coverage,
            MaximumAgeSeconds = (long)maximumAge.TotalSeconds,
            ValidUntil = aggregateObservedAt.Add(maximumAge),
        };
    }

    public static EvidencePosture Build(DateTimeOffset evaluatedAt, params EvidenceSourceEnvelope[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var normalized = sources.Select(source => Validate(source, evaluatedAt)).ToArray();
        return new EvidencePosture
        {
            Status = SummarizeStatus(normalized),
            Sources = normalized,
        };
    }

    /// <summary>
    /// Summarizes the required sources without hiding any of them: the top-level status is the
    /// weakest individual state, so a client that only reads the summary still fails closed.
    /// </summary>
    private static string SummarizeStatus(EvidenceSourceEnvelope[] sources)
    {
        if (sources.Length == 0)
        {
            return EvidencePostureVocabulary.Completeness.Unavailable;
        }

        if (sources.All(IsActionable))
        {
            return EvidencePostureVocabulary.Completeness.Complete;
        }

        return sources.Any(source =>
            source.Completeness == EvidencePostureVocabulary.Completeness.Unavailable ||
            source.Completeness == EvidencePostureVocabulary.Completeness.NotConfigured)
            ? EvidencePostureVocabulary.Completeness.Unavailable
            : EvidencePostureVocabulary.Completeness.Partial;
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
        else if (source.LastSuccessfulAt > evaluatedAt + ClockSkew)
        {
            // A collection that "last succeeded" in the future is malformed clock evidence and must
            // fail closed exactly like a future observation.
            reasons.Add(EvidencePostureVocabulary.ReasonCodes.FutureObservationTime);
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

            if (coverage.ExpectedComponentIds is { Count: > 0 } expectedComponents)
            {
                var includedComponents = coverage.IncludedComponentIds ?? [];
                if (expectedComponents.Except(includedComponents, StringComparer.Ordinal).Any())
                {
                    reasons.Add(EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage);
                }
            }
        }

        return new EvidenceSourceEnvelope
        {
            SourceId = source.SourceId,
            BackendKind = source.BackendKind,
            BackendId = source.BackendId,
            ObservedAt = source.ObservedAt?.ToUniversalTime(),
            LastSuccessfulAt = source.LastSuccessfulAt?.ToUniversalTime(),
            Completeness = ResolveCompleteness(source.Completeness, reasons),
            ReasonCodes = reasons.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Coverage = source.Coverage,
            MaximumAgeSeconds = source.MaximumAgeSeconds,
            ValidUntil = source.ValidUntil?.ToUniversalTime(),
        };
    }

    /// <summary>
    /// Resolves the published completeness from the declared value and the accumulated reasons.
    /// A not-configured source stays not-configured (a client must be able to tell "nothing was
    /// wired" from "the configured backend failed"); coverage-only reasons degrade to
    /// <c>partial</c> so valid partial data is not misreported as a total source failure; anything
    /// else is <c>unavailable</c>.
    /// </summary>
    private static string ResolveCompleteness(string declared, HashSet<string> reasons)
    {
        if (reasons.Count == 0)
        {
            return declared;
        }

        if (string.Equals(declared, EvidencePostureVocabulary.Completeness.NotConfigured, StringComparison.Ordinal))
        {
            return EvidencePostureVocabulary.Completeness.NotConfigured;
        }

        return reasons.All(CoverageReasonCodes.Contains)
            ? EvidencePostureVocabulary.Completeness.Partial
            : EvidencePostureVocabulary.Completeness.Unavailable;
    }
}
