// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

/// <summary>Closed identifiers for operational evidence sources published to agents.</summary>
public static class EvidenceSourceIds
{
    public const string HealthChecks = "health-checks";
    public const string ServingLatency = "serving-latency";
    public const string GeoprocessingQueue = "geoprocessing-queue";
    public const string AlertDispatch = "alert-dispatch";
    public const string DeployReadiness = "deploy-readiness";
    public const string PlatformRelease = "platform-release";
    public const string Database = "database";
    public const string Cache = "cache";
    public const string OpsFindings = "ops-findings";
    public const string AlertEvents = "alert-events";
    public const string OperateEvents = "operate-events";
    public const string DeployOperations = "deploy-operations";

    internal static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        HealthChecks, ServingLatency, GeoprocessingQueue, AlertDispatch, DeployReadiness,
        PlatformRelease, Database, Cache, OpsFindings, AlertEvents, OperateEvents, DeployOperations,
    };
}

/// <summary>Closed privacy-safe categories for the implementation that supplied evidence.</summary>
public static class EvidenceBackendKinds
{
    public const string InProcess = "inProcess";
    public const string Configuration = "configuration";
    public const string HealthCheckService = "healthCheckService";
    public const string DurableStore = "durableStore";
    public const string Composite = "composite";
    public const string NotConfigured = "notConfigured";

    internal static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        InProcess, Configuration, HealthCheckService, DurableStore, Composite, NotConfigured,
    };
}

/// <summary>Closed completeness states. These values are serialized verbatim.</summary>
public static class EvidenceCompletenessStatuses
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
    public const string NotConfigured = "notConfigured";

    internal static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Complete, Partial, Unavailable, NotConfigured,
    };
}

/// <summary>Closed, sanitized evidence reason codes. Raw provider failures are never included.</summary>
public static class EvidenceReasonCodes
{
    public const string SourceNotConfigured = "source-not-configured";
    public const string SourceUnavailable = "source-unavailable";
    public const string NeverSucceeded = "never-succeeded";
    public const string MissingObservationTime = "missing-observation-time";
    public const string FutureObservationTime = "future-observation-time";
    public const string InvalidTimeWindow = "invalid-time-window";
    public const string StaleObservation = "stale-observation";
    public const string StaleLastSuccess = "stale-last-success";
    public const string PartialResult = "partial-result";
    public const string IncompleteReplicaCoverage = "incomplete-replica-coverage";
    public const string PageTruncated = "page-truncated";
    public const string BackendUnverified = "backend-unverified";
    public const string ComponentCoverageUnknown = "component-coverage-unknown";
    public const string MalformedEvidence = "malformed-evidence";

    internal static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        SourceNotConfigured, SourceUnavailable, NeverSucceeded, MissingObservationTime,
        FutureObservationTime, InvalidTimeWindow, StaleObservation, StaleLastSuccess,
        PartialResult, IncompleteReplicaCoverage, PageTruncated, BackendUnverified,
        ComponentCoverageUnknown, MalformedEvidence,
    };
}

/// <summary>Structured coverage for a source query.</summary>
public sealed record EvidenceCoverage
{
    public DateTimeOffset? RequestedFrom { get; init; }
    public DateTimeOffset? RequestedTo { get; init; }
    public DateTimeOffset? ReturnedFrom { get; init; }
    public DateTimeOffset? ReturnedTo { get; init; }
    public int? RequestedPageSize { get; init; }
    public int? ReturnedCount { get; init; }
    public bool? HasMore { get; init; }
    public bool? Truncated { get; init; }
    public int? ObservedReplicaCount { get; init; }
    public int? ExpectedReplicaCount { get; init; }
    public IReadOnlyList<string> IncludedComponentIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedComponentIds { get; init; } = Array.Empty<string>();
}

/// <summary>Versioned posture for one configured source implementation.</summary>
public sealed record EvidenceSourcePosture
{
    public required string SourceId { get; init; }
    public required string BackendKind { get; init; }
    public required string BackendId { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? LastSuccessfulAt { get; init; }
    public required string Completeness { get; init; }
    public EvidenceCoverage Coverage { get; init; } = new();
    public int? MaximumObservationAgeSeconds { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Top-level source-integrity posture. <see cref="GeneratedAt"/> is response/evaluation time and
/// is deliberately distinct from every source's observation and last-success timestamps.
/// </summary>
public sealed record EvidencePostureEnvelope
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string Completeness { get; init; }
    public required bool Actionable { get; init; }
    public IReadOnlyList<EvidenceSourcePosture> Sources { get; init; } = Array.Empty<EvidenceSourcePosture>();
}

/// <summary>Fail-closed construction and re-validation for evidence posture.</summary>
public static class EvidencePosture
{
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(1);
    private const int MaximumReasonCodes = 16;
    private const int MaximumComponentIds = 64;
    private const int MaximumIdentifierLength = 96;

    public static EvidenceSourcePosture Source(
        string sourceId,
        string backendKind,
        string backendId,
        DateTimeOffset? observedAt,
        DateTimeOffset? lastSuccessfulAt,
        string completeness = EvidenceCompletenessStatuses.Complete,
        EvidenceCoverage? coverage = null,
        int? maximumObservationAgeSeconds = 300,
        IEnumerable<string>? reasonCodes = null,
        DateTimeOffset? evaluatedAt = null)
    {
        var now = (evaluatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var reasons = new HashSet<string>(reasonCodes ?? [], StringComparer.Ordinal);
        var normalizedCompleteness = EvidenceCompletenessStatuses.All.Contains(completeness)
            ? completeness
            : EvidenceCompletenessStatuses.Unavailable;

        if (!EvidenceSourceIds.All.Contains(sourceId))
        {
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
        }

        if (!EvidenceBackendKinds.All.Contains(backendKind)
            || string.IsNullOrWhiteSpace(backendId)
            || backendId.Length > MaximumIdentifierLength
            || backendId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            reasons.Add(EvidenceReasonCodes.BackendUnverified);
            normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
        }

        var normalizedObservedAt = NormalizeTimestamp(observedAt);
        var normalizedLastSuccessfulAt = NormalizeTimestamp(lastSuccessfulAt);
        if (normalizedCompleteness == EvidenceCompletenessStatuses.NotConfigured)
        {
            normalizedObservedAt = null;
            normalizedLastSuccessfulAt = null;
            reasons.Add(EvidenceReasonCodes.SourceNotConfigured);
        }
        else
        {
            if (normalizedObservedAt is null)
            {
                reasons.Add(EvidenceReasonCodes.MissingObservationTime);
                normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
            }
            else if (normalizedObservedAt > now + MaximumFutureSkew)
            {
                reasons.Add(EvidenceReasonCodes.FutureObservationTime);
                normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
            }

            if (normalizedLastSuccessfulAt is null)
            {
                reasons.Add(EvidenceReasonCodes.NeverSucceeded);
                normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
            }
            else if (normalizedLastSuccessfulAt > now + MaximumFutureSkew)
            {
                reasons.Add(EvidenceReasonCodes.FutureObservationTime);
                normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
            }
        }

        var normalizedCoverage = NormalizeCoverage(coverage ?? new(), now, reasons, ref normalizedCompleteness);
        if (maximumObservationAgeSeconds is <= 0 or > 604800)
        {
            maximumObservationAgeSeconds = null;
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            normalizedCompleteness = EvidenceCompletenessStatuses.Unavailable;
        }
        else if (maximumObservationAgeSeconds is { } maxAge)
        {
            if (normalizedObservedAt is { } observation && now - observation > TimeSpan.FromSeconds(maxAge))
            {
                reasons.Add(EvidenceReasonCodes.StaleObservation);
                normalizedCompleteness = EvidenceCompletenessStatuses.Partial;
            }

            if (normalizedLastSuccessfulAt is { } lastSuccess && now - lastSuccess > TimeSpan.FromSeconds(maxAge))
            {
                reasons.Add(EvidenceReasonCodes.StaleLastSuccess);
                normalizedCompleteness = EvidenceCompletenessStatuses.Partial;
            }
        }

        var sanitizedReasons = reasons
            .Where(EvidenceReasonCodes.All.Contains)
            .Order(StringComparer.Ordinal)
            .Take(MaximumReasonCodes)
            .ToArray();

        return new EvidenceSourcePosture
        {
            SourceId = sourceId,
            BackendKind = backendKind,
            BackendId = backendId,
            ObservedAt = normalizedObservedAt,
            LastSuccessfulAt = normalizedLastSuccessfulAt,
            Completeness = normalizedCompleteness,
            Coverage = normalizedCoverage,
            MaximumObservationAgeSeconds = maximumObservationAgeSeconds,
            ReasonCodes = sanitizedReasons,
        };
    }

    public static EvidencePostureEnvelope Envelope(
        DateTimeOffset generatedAt,
        IEnumerable<EvidenceSourcePosture> sources)
    {
        var normalizedGeneratedAt = generatedAt.ToUniversalTime();
        var normalizedSources = sources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        var actionable = normalizedSources.Length > 0
            && normalizedSources.All(source => IsSourceActionable(source, normalizedGeneratedAt));
        var completeness = actionable
            ? EvidenceCompletenessStatuses.Complete
            : normalizedSources.Any(source => source.Completeness == EvidenceCompletenessStatuses.Complete)
                ? EvidenceCompletenessStatuses.Partial
                : normalizedSources.Any(source => source.Completeness == EvidenceCompletenessStatuses.NotConfigured)
                    ? EvidenceCompletenessStatuses.NotConfigured
                    : EvidenceCompletenessStatuses.Unavailable;

        return new EvidencePostureEnvelope
        {
            GeneratedAt = normalizedGeneratedAt,
            Completeness = completeness,
            Actionable = actionable,
            Sources = normalizedSources,
        };
    }

    public static bool IsActionable(
        EvidencePostureEnvelope? envelope,
        IEnumerable<string> requiredSourceIds,
        DateTimeOffset evaluatedAt)
    {
        if (envelope is null || envelope.SchemaVersion != EvidencePostureEnvelope.CurrentSchemaVersion)
        {
            return false;
        }

        var sources = envelope.Sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var required = requiredSourceIds.Distinct(StringComparer.Ordinal).ToArray();
        return required.Length > 0
            && required.All(sourceId => sources.TryGetValue(sourceId, out var source)
                && IsSourceActionable(source, evaluatedAt.ToUniversalTime()));
    }

    public static IReadOnlyList<string> ToEvidenceReferences(EvidencePostureEnvelope envelope)
        => envelope.Sources.Select(source => FormattableString.Invariant(
            $"evidence-source:{source.SourceId}:{source.BackendKind}:{source.BackendId}:{source.ObservedAt:O}:{source.Completeness}"))
            .ToArray();

    private static bool IsSourceActionable(EvidenceSourcePosture source, DateTimeOffset now)
        => source.Completeness == EvidenceCompletenessStatuses.Complete
            && source.ObservedAt is { } observedAt
            && source.LastSuccessfulAt is { } lastSuccessfulAt
            && observedAt <= now + MaximumFutureSkew
            && lastSuccessfulAt <= now + MaximumFutureSkew
            && source.MaximumObservationAgeSeconds is { } maxAge
            && now - observedAt <= TimeSpan.FromSeconds(maxAge)
            && now - lastSuccessfulAt <= TimeSpan.FromSeconds(maxAge)
            && source.ReasonCodes.Count == 0;

    private static DateTimeOffset? NormalizeTimestamp(DateTimeOffset? value)
        => value?.ToUniversalTime();

    private static EvidenceCoverage NormalizeCoverage(
        EvidenceCoverage coverage,
        DateTimeOffset now,
        ISet<string> reasons,
        ref string completeness)
    {
        var requestedFrom = NormalizeTimestamp(coverage.RequestedFrom);
        var requestedTo = NormalizeTimestamp(coverage.RequestedTo);
        var returnedFrom = NormalizeTimestamp(coverage.ReturnedFrom);
        var returnedTo = NormalizeTimestamp(coverage.ReturnedTo);
        if ((requestedFrom is { } requestStart && requestedTo is { } requestEnd && requestStart > requestEnd)
            || (returnedFrom is { } returnStart && returnedTo is { } returnEnd && returnStart > returnEnd)
            || returnedTo > now + MaximumFutureSkew)
        {
            reasons.Add(EvidenceReasonCodes.InvalidTimeWindow);
            completeness = EvidenceCompletenessStatuses.Unavailable;
        }

        if (coverage.Truncated == true || coverage.HasMore == true)
        {
            reasons.Add(EvidenceReasonCodes.PageTruncated);
            completeness = EvidenceCompletenessStatuses.Partial;
        }

        if (coverage.ObservedReplicaCount is { } observedReplicas
            && coverage.ExpectedReplicaCount is { } expectedReplicas
            && observedReplicas < expectedReplicas)
        {
            reasons.Add(EvidenceReasonCodes.IncompleteReplicaCoverage);
            completeness = EvidenceCompletenessStatuses.Partial;
        }

        var included = SanitizeComponentIds(coverage.IncludedComponentIds);
        var expected = SanitizeComponentIds(coverage.ExpectedComponentIds);
        if (expected.Count > 0 && !expected.All(id => included.Contains(id, StringComparer.Ordinal)))
        {
            reasons.Add(EvidenceReasonCodes.PartialResult);
            completeness = EvidenceCompletenessStatuses.Partial;
        }

        return coverage with
        {
            RequestedFrom = requestedFrom,
            RequestedTo = requestedTo,
            ReturnedFrom = returnedFrom,
            ReturnedTo = returnedTo,
            IncludedComponentIds = included,
            ExpectedComponentIds = expected,
        };
    }

    private static IReadOnlyList<string> SanitizeComponentIds(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentifierLength)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaximumComponentIds)
            .ToArray();
}
