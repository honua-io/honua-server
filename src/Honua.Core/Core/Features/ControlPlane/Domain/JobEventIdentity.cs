// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public static class JobEventIdentity
{
    public const string TraceIdParameter = "honua.trace_id";

    public const string CorrelationEnvironmentName = "HONUA_CORRELATION_ID";

    public const string TraceParentEnvironmentName = "HONUA_TRACEPARENT";

    public const string CorrelationParameter = "env." + CorrelationEnvironmentName;

    public const string TraceParentParameter = "env." + TraceParentEnvironmentName;

    public const string JobMetadataKey = "honua.event.job";

    public const string CorrelationMetadataKey = "honua.event.correlation";

    public const string TraceMetadataKey = "honua.event.trace";

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
