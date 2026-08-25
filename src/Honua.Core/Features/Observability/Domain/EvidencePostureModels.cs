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
    public const string Unverified = "unverified";

    internal static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        InProcess, Configuration, HealthCheckService, DurableStore, Composite, NotConfigured, Unverified,
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

    /// <summary>Returns whether a source identifier is part of the version 1 closed vocabulary.</summary>
    public static bool IsKnownSourceId(string? sourceId)
        => sourceId is not null && EvidenceSourceIds.All.Contains(sourceId);

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
        var suppliedReasons = (reasonCodes ?? []).ToArray();
        var reasons = new HashSet<string>(suppliedReasons, StringComparer.Ordinal);
        if (suppliedReasons.Length > MaximumReasonCodes || suppliedReasons.Any(reason => !EvidenceReasonCodes.All.Contains(reason)))
        {
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
        }
        var normalizedCompleteness = EvidenceCompletenessStatuses.All.Contains(completeness)
            ? completeness
            : EvidenceCompletenessStatuses.Unavailable;

        if (!EvidenceSourceIds.All.Contains(sourceId))
        {
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            normalizedCompleteness = Worsen(
                normalizedCompleteness,
                EvidenceCompletenessStatuses.Unavailable);
        }

        var normalizedBackendKind = EvidenceBackendKinds.All.Contains(backendKind)
            ? backendKind
            : EvidenceBackendKinds.Unverified;
        var normalizedBackendId = IsSafeIdentifier(backendId) ? backendId : EvidenceBackendKinds.Unverified;
        if (normalizedBackendKind == EvidenceBackendKinds.Unverified
            || normalizedBackendId == EvidenceBackendKinds.Unverified)
        {
            reasons.Add(EvidenceReasonCodes.BackendUnverified);
            normalizedCompleteness = Worsen(
                normalizedCompleteness,
                EvidenceCompletenessStatuses.Unavailable);
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
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Unavailable);
            }
            else if (normalizedObservedAt > now + MaximumFutureSkew)
            {
                reasons.Add(EvidenceReasonCodes.FutureObservationTime);
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Unavailable);
            }

            if (normalizedLastSuccessfulAt is null)
            {
                reasons.Add(EvidenceReasonCodes.NeverSucceeded);
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Unavailable);
            }
            else if (normalizedLastSuccessfulAt > now + MaximumFutureSkew)
            {
                reasons.Add(EvidenceReasonCodes.FutureObservationTime);
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Unavailable);
            }
        }

        var normalizedCoverage = NormalizeCoverage(coverage ?? new(), now, reasons, ref normalizedCompleteness);
        if (maximumObservationAgeSeconds is <= 0 or > 604800)
        {
            maximumObservationAgeSeconds = null;
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            normalizedCompleteness = Worsen(
                normalizedCompleteness,
                EvidenceCompletenessStatuses.Unavailable);
        }
        else if (maximumObservationAgeSeconds is { } maxAge)
        {
            if (normalizedObservedAt is { } observation && now - observation > TimeSpan.FromSeconds(maxAge))
            {
                reasons.Add(EvidenceReasonCodes.StaleObservation);
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Partial);
            }

            if (normalizedLastSuccessfulAt is { } lastSuccess && now - lastSuccess > TimeSpan.FromSeconds(maxAge))
            {
                reasons.Add(EvidenceReasonCodes.StaleLastSuccess);
                normalizedCompleteness = Worsen(
                    normalizedCompleteness,
                    EvidenceCompletenessStatuses.Partial);
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
            BackendKind = normalizedBackendKind,
            BackendId = normalizedBackendId,
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
        var hasDuplicateSourceIds = normalizedSources
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any());
        var actionable = normalizedSources.Length > 0
            && !hasDuplicateSourceIds
            && normalizedSources.All(source => IsSourceActionable(source, normalizedGeneratedAt));
        var completeness = actionable
            ? EvidenceCompletenessStatuses.Complete
            : normalizedSources.Any(source => source.Completeness == EvidenceCompletenessStatuses.Unavailable)
                || hasDuplicateSourceIds
                ? EvidenceCompletenessStatuses.Unavailable
                : normalizedSources.Any(source => source.Completeness == EvidenceCompletenessStatuses.Partial)
                    || normalizedSources.Any(source => source.Completeness == EvidenceCompletenessStatuses.Complete)
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
        var now = evaluatedAt.ToUniversalTime();
        if (!IsWellFormed(envelope, now) || envelope!.Actionable is false)
        {
            return false;
        }

        var sourceGroups = envelope.Sources
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (sourceGroups.Any(group => group.Skip(1).Any()))
        {
            return false;
        }

        var sources = sourceGroups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var required = requiredSourceIds.Distinct(StringComparer.Ordinal).ToArray();
        return required.Length > 0
            && required.All(IsKnownSourceId)
            && required.All(sourceId => sources.TryGetValue(sourceId, out var source)
                && IsSourceActionable(source, now));
    }

    /// <summary>Validates an envelope received from serialization without trusting its producer.</summary>
    public static bool IsWellFormed(EvidencePostureEnvelope? envelope, DateTimeOffset evaluatedAt)
    {
        var now = evaluatedAt.ToUniversalTime();
        if (envelope is null
            || envelope.SchemaVersion != EvidencePostureEnvelope.CurrentSchemaVersion
            || envelope.GeneratedAt.Offset != TimeSpan.Zero
            || envelope.GeneratedAt > now + MaximumFutureSkew
            || !EvidenceCompletenessStatuses.All.Contains(envelope.Completeness)
            || envelope.Sources is null
            || envelope.Sources.Count is 0 or > 64)
        {
            return false;
        }

        var sourceGroups = envelope.Sources.GroupBy(source => source.SourceId, StringComparer.Ordinal).ToArray();
        if (sourceGroups.Any(group => group.Skip(1).Any())
            || envelope.Sources.Any(source => !IsSourceWellFormed(source)))
        {
            return false;
        }

        var actionableAtGeneration = envelope.Sources.All(source => IsSourceActionable(source, envelope.GeneratedAt));
        return envelope.Actionable == actionableAtGeneration
            && (!envelope.Actionable || envelope.Completeness == EvidenceCompletenessStatuses.Complete);
    }

    public static IReadOnlyList<string> ToEvidenceReferences(EvidencePostureEnvelope envelope)
        => envelope.Sources.Select(source => FormattableString.Invariant(
            $"evidence-source:{source.SourceId}:{source.BackendKind}:{source.BackendId}:{source.ObservedAt:O}:{source.Completeness}"))
            .ToArray();

    private static bool IsSourceActionable(EvidenceSourcePosture source, DateTimeOffset now)
        => IsSourceWellFormed(source)
            && source.Completeness == EvidenceCompletenessStatuses.Complete
            && source.ObservedAt is { } observedAt
            && source.LastSuccessfulAt is { } lastSuccessfulAt
            && observedAt <= now + MaximumFutureSkew
            && lastSuccessfulAt <= now + MaximumFutureSkew
            && source.MaximumObservationAgeSeconds is { } maxAge
            && now - observedAt <= TimeSpan.FromSeconds(maxAge)
            && now - lastSuccessfulAt <= TimeSpan.FromSeconds(maxAge)
            && source.ReasonCodes.Count == 0;

    private static bool IsSourceWellFormed(EvidenceSourcePosture source)
    {
        if (!IsKnownSourceId(source.SourceId)
            || !EvidenceBackendKinds.All.Contains(source.BackendKind)
            || !IsSafeIdentifier(source.BackendId)
            || !EvidenceCompletenessStatuses.All.Contains(source.Completeness)
            || source.MaximumObservationAgeSeconds is <= 0 or > 604800
            || source.ReasonCodes is null
            || source.ReasonCodes.Count > MaximumReasonCodes
            || source.ReasonCodes.Distinct(StringComparer.Ordinal).Count() != source.ReasonCodes.Count
            || source.ReasonCodes.Any(reason => !EvidenceReasonCodes.All.Contains(reason))
            || source.ObservedAt is { Offset: var observedOffset } && observedOffset != TimeSpan.Zero
            || source.LastSuccessfulAt is { Offset: var successOffset } && successOffset != TimeSpan.Zero)
        {
            return false;
        }

        if (source.Completeness == EvidenceCompletenessStatuses.NotConfigured)
        {
            return source.BackendKind == EvidenceBackendKinds.NotConfigured
                && source.ObservedAt is null
                && source.LastSuccessfulAt is null
                && source.ReasonCodes.Contains(EvidenceReasonCodes.SourceNotConfigured, StringComparer.Ordinal)
                && IsCoverageWellFormed(source.Coverage);
        }

        return source.BackendKind is not EvidenceBackendKinds.NotConfigured and not EvidenceBackendKinds.Unverified
            && source.ObservedAt is not null
            && source.LastSuccessfulAt is not null
            && IsCoverageWellFormed(source.Coverage);
    }

    private static bool IsCoverageWellFormed(EvidenceCoverage? coverage)
    {
        if (coverage is null
            || !IsUtc(coverage.RequestedFrom)
            || !IsUtc(coverage.RequestedTo)
            || !IsUtc(coverage.ReturnedFrom)
            || !IsUtc(coverage.ReturnedTo)
            || IsReversed(coverage.RequestedFrom, coverage.RequestedTo)
            || IsReversed(coverage.ReturnedFrom, coverage.ReturnedTo)
            || coverage.RequestedPageSize is <= 0 or > 10000
            || coverage.ReturnedCount is < 0 or > 10000
            || coverage.ObservedReplicaCount is < 0 or > 100000
            || coverage.ExpectedReplicaCount is <= 0 or > 100000
            || coverage.IncludedComponentIds is null
            || coverage.ExpectedComponentIds is null
            || !AreBoundedIdentifiers(coverage.IncludedComponentIds)
            || !AreBoundedIdentifiers(coverage.ExpectedComponentIds))
        {
            return false;
        }

        return coverage.RequestedPageSize is null
            || coverage.ReturnedCount is null
            || coverage.ReturnedCount.Value <= coverage.RequestedPageSize.Value;
    }

    private static bool IsUtc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;

    private static bool IsReversed(DateTimeOffset? from, DateTimeOffset? to)
        => from is { } start && to is { } end && start > end;

    private static bool AreBoundedIdentifiers(IReadOnlyList<string> values)
        => values.Count <= MaximumComponentIds
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count
            && values.All(IsSafeIdentifier);

    private static bool IsSafeIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaximumIdentifierLength
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static DateTimeOffset? NormalizeTimestamp(DateTimeOffset? value)
        => value?.ToUniversalTime();

    private static EvidenceCoverage NormalizeCoverage(
        EvidenceCoverage coverage,
        DateTimeOffset now,
        HashSet<string> reasons,
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
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Unavailable);
        }

        if (coverage.Truncated == true || coverage.HasMore == true)
        {
            reasons.Add(EvidenceReasonCodes.PageTruncated);
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Partial);
        }

        if (coverage.ObservedReplicaCount is { } observedReplicas
            && coverage.ExpectedReplicaCount is { } expectedReplicas
            && observedReplicas < expectedReplicas)
        {
            reasons.Add(EvidenceReasonCodes.IncompleteReplicaCoverage);
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Partial);
        }

        if (coverage.RequestedPageSize is <= 0 or > 10000
            || coverage.ReturnedCount is < 0 or > 10000
            || coverage.ReturnedCount is { } returnedCount
                && coverage.RequestedPageSize is { } requestedPageSize
                && returnedCount > requestedPageSize
            || coverage.ObservedReplicaCount is < 0 or > 100000
            || coverage.ExpectedReplicaCount is <= 0 or > 100000)
        {
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Unavailable);
        }

        var suppliedIncluded = coverage.IncludedComponentIds ?? [];
        var suppliedExpected = coverage.ExpectedComponentIds ?? [];
        var included = SanitizeComponentIds(suppliedIncluded);
        var expected = SanitizeComponentIds(suppliedExpected);
        if (included.Length != suppliedIncluded.Count || expected.Length != suppliedExpected.Count)
        {
            reasons.Add(EvidenceReasonCodes.MalformedEvidence);
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Unavailable);
        }
        if (expected.Length > 0 && !expected.All(id => included.Contains(id, StringComparer.Ordinal)))
        {
            reasons.Add(EvidenceReasonCodes.PartialResult);
            completeness = Worsen(completeness, EvidenceCompletenessStatuses.Partial);
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

    private static string[] SanitizeComponentIds(IEnumerable<string> values)
        => values
            .Where(IsSafeIdentifier)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaximumComponentIds)
            .ToArray();

    private static string Worsen(string current, string candidate)
    {
        static int Rank(string completeness) => completeness switch
        {
            EvidenceCompletenessStatuses.Complete => 0,
            EvidenceCompletenessStatuses.NotConfigured => 1,
            EvidenceCompletenessStatuses.Partial => 2,
            _ => 3,
        };

        return Rank(candidate) > Rank(current) ? candidate : current;
    }
}
