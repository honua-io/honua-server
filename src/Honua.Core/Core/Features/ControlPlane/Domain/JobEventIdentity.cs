// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// The identity every custom-code harness event and the admitted job must share:
/// job, attempt, correlation, and the W3C trace id. Journal text is not a source
/// of this identity. Dispatch copies the admitted values into the worker environment,
/// and ingestion stamps the same values onto stored events.
/// </summary>
public static class JobEventIdentity
{
    /// <summary>Spec parameter and query key for the 32-hex trace id.</summary>
    public const string TraceIdParameter = "honua.trace_id";

    /// <summary>Container environment variable carrying the admitted correlation id.</summary>
    public const string CorrelationEnvironmentName = "HONUA_CORRELATION_ID";

    /// <summary>Container environment variable carrying the admitted W3C traceparent.</summary>
    public const string TraceParentEnvironmentName = "HONUA_TRACEPARENT";

    /// <summary>Spec key that injects <see cref="CorrelationEnvironmentName"/>.</summary>
    public const string CorrelationParameter = "env." + CorrelationEnvironmentName;

    /// <summary>Spec key that injects <see cref="TraceParentEnvironmentName"/>.</summary>
    public const string TraceParentParameter = "env." + TraceParentEnvironmentName;

    /// <summary>Stored-event metadata key for the admitted job id.</summary>
    public const string JobMetadataKey = "honua.event.job";

    /// <summary>Stored-event metadata key for the admitted correlation id.</summary>
    public const string CorrelationMetadataKey = "honua.event.correlation";

    /// <summary>Stored-event metadata key for the admitted 32-hex trace id.</summary>
    public const string TraceMetadataKey = "honua.event.trace";

    /// <summary>
    /// Accepts a correlation id that is safe to copy into a journal and a job record.
    /// Letters, digits, and <c>. _ : -</c> only, at most 128 characters.
    /// </summary>
    /// <param name="value">The candidate correlation id.</param>
    /// <param name="normalized">The trimmed id when the value is accepted.</param>
    public static bool TryNormalizeCorrelation(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length is < 1 or > 128)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-'))
            {
                return false;
            }
        }

        normalized = text;
        return true;
    }

    /// <summary>
    /// Accepts a W3C <c>traceparent</c> (<c>00-{trace}-{span}-{flags}</c>) and returns the trace id.
    /// The all-zero trace id and the all-zero span id are rejected.
    /// </summary>
    /// <param name="value">The candidate traceparent.</param>
    /// <param name="traceParent">The normalized lowercase traceparent.</param>
    /// <param name="traceId">The 32-hex trace id.</param>
    public static bool TryNormalizeTraceParent(string? value, out string traceParent, out string traceId)
    {
        traceParent = string.Empty;
        traceId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().ToLowerInvariant().Split('-');
        if (parts.Length != 4
            || parts[0] != "00"
            || parts[1].Length != 32
            || parts[2].Length != 16
            || parts[3].Length != 2
            || !IsHex(parts[1])
            || !IsHex(parts[2])
            || !IsHex(parts[3])
            || IsAllZero(parts[1])
            || IsAllZero(parts[2]))
        {
            return false;
        }

        traceId = parts[1];
        traceParent = string.Join('-', parts);
        return true;
    }

    /// <summary>Accepts a bare 32-hex trace id that is not all zeros.</summary>
    /// <param name="value">The candidate trace id.</param>
    /// <param name="traceId">The normalized lowercase trace id.</param>
    public static bool TryNormalizeTraceId(string? value, out string traceId)
    {
        traceId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().ToLowerInvariant();
        if (text.Length != 32 || !IsHex(text) || IsAllZero(text))
        {
            return false;
        }

        traceId = text;
        return true;
    }

    /// <summary>
    /// The trace id the admitted job carries: the submission <c>traceparent</c> when it
    /// parses, otherwise <see cref="TraceIdParameter"/> when that value is a trace id.
    /// </summary>
    /// <param name="job">The admitted job.</param>
    public static string? ResolveTraceId(ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (TryNormalizeTraceParent(job.Audit.TraceParent, out _, out var fromParent))
        {
            return fromParent;
        }

        return job.Spec.Parameters.TryGetValue(TraceIdParameter, out var parameter)
            && TryNormalizeTraceId(parameter, out var fromParameter)
            ? fromParameter
            : null;
    }

    /// <summary>
    /// Writes the admitted job, correlation, and trace onto a stored event.
    /// Any correlation or trace already on the entry is removed first, so a journal
    /// cannot forge them.
    /// </summary>
    /// <param name="metadata">The event metadata to update.</param>
    /// <param name="job">The admitted job.</param>
    public static void Stamp(IDictionary<string, string> metadata, ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(job);
        metadata.Remove(CorrelationMetadataKey);
        metadata.Remove(TraceMetadataKey);
        metadata[JobMetadataKey] = job.OperationId;
        if (TryNormalizeCorrelation(job.Audit.CorrelationId, out var correlation))
        {
            metadata[CorrelationMetadataKey] = correlation;
        }

        if (ResolveTraceId(job) is { } traceId)
        {
            metadata[TraceMetadataKey] = traceId;
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var character in value)
        {
            if (character != '0')
            {
                return false;
            }
        }

        return true;
    }
}
