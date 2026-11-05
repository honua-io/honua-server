// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Server.Features.Streaming.Conformance;

internal static class FeatureStreamConformanceOperations
{
    public const string Insert = "insert";

    public const string Update = "update";

    public const string Touch = "touch";

    public const string Delete = "delete";

    public static readonly string[] All = [Insert, Update, Touch, Delete];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return All
            .Where(operation => trimmed.Equals(operation, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}

// The marker carries its own deadline so the sweeper can reclaim a record after the creating process is gone.
internal readonly record struct FeatureStreamConformanceMarker(Guid RunId, DateTimeOffset ExpiresAt)
{
    public const string Prefix = "honua-conformance";

    private const char Separator = ':';

    public string Format()
        => string.Join(
            Separator,
            Prefix,
            RunId.ToString("N"),
            ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    public static bool TryParse(object? stored, out FeatureStreamConformanceMarker marker)
    {
        marker = default;
        if (stored is not string text || text.Length == 0)
        {
            return false;
        }

        var parts = text.Split(Separator);
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var runId))
        {
            return false;
        }

        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnix))
        {
            return false;
        }

        marker = new FeatureStreamConformanceMarker(runId, DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix));
        return true;
    }
}

// Every refusal is fail-closed, and the workflow never mutates a different source.
internal enum FeatureStreamConformanceFailure
{
    None = 0,

    Disabled,

    SourceUnavailable,

    DeploymentRevisionUnavailable,

    DeploymentRevisionMismatch,

    SourceIdentityMismatch,

    LeaseUnavailable,

    RunNotFound,

    MutationBudgetExhausted,

    RecordBudgetExhausted,

    RecordNotOwned,

    InvalidRequest
}

internal readonly record struct FeatureStreamConformanceResult<T>(
    FeatureStreamConformanceFailure Failure,
    string? Message,
    T? Value)
{
    public bool IsSuccess => Failure == FeatureStreamConformanceFailure.None;

    public static FeatureStreamConformanceResult<T> Success(T value)
        => new(FeatureStreamConformanceFailure.None, null, value);

    public static FeatureStreamConformanceResult<T> Fail(FeatureStreamConformanceFailure failure, string message)
        => new(failure, message, default);
}

internal sealed record FeatureStreamConformanceRunRequest
{
    public string? ClientLabel { get; init; }

    // A supplied revision must match this deployment, so a run cannot produce evidence against another image.
    public string? ExpectedDeploymentRevision { get; init; }

    // A supplied service must match the configured source.
    public string? ExpectedServiceId { get; init; }

    public int? TtlSeconds { get; init; }
}

internal sealed record FeatureStreamConformanceRunResponse
{
    public required string RunId { get; init; }

    public required string RunToken { get; init; }

    public required string RunMarker { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required string RunIdField { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required int RemainingMutations { get; init; }

    public required int MaxRecords { get; init; }

    public required string DeploymentRevision { get; init; }

    public required string BaselineDigest { get; init; }

    public required int BaselineRecordCount { get; init; }
}

internal sealed record FeatureStreamConformanceMutationRequest
{
    public string? Operation { get; init; }

    // The target must be a record this run owns.
    public long? ObjectId { get; init; }

    public string? Label { get; init; }
}

internal sealed record FeatureStreamConformanceMutationResponse
{
    public required string RunId { get; init; }

    public required string Operation { get; init; }

    public required long ObjectId { get; init; }

    public required int MutationOrdinal { get; init; }

    public required int RemainingMutations { get; init; }

    public required int OwnedRecords { get; init; }

    public required string RunMarker { get; init; }
}

internal sealed record FeatureStreamConformanceCleanupResponse
{
    public required string RunId { get; init; }

    public required int DeletedRecords { get; init; }

    public required string BaselineDigest { get; init; }

    public required int BaselineRecordCount { get; init; }

    public required bool BaselineRestored { get; init; }
}

internal sealed record FeatureStreamConformanceResetResponse
{
    public required int ReleasedRuns { get; init; }

    public required int DeletedRecords { get; init; }

    public required string BaselineDigest { get; init; }

    public required int BaselineRecordCount { get; init; }
}
