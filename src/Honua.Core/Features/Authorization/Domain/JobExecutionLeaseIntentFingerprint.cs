// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Bounded, versioned binding of immutable accepted job intent and its original security context.</summary>
public static class JobExecutionLeaseIntentFingerprint
{
    private const int MaximumBytes = 1048576;

    /// <summary>Hashes intent independently of mutable execution state and the lease being sealed.</summary>
    /// <param name="job">Actual server-prepared or authoritative stored job.</param>
    /// <returns>A versioned SHA256 fingerprint; this value alone is never authority.</returns>
    public static string Compute(ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.OperationId) || job.OperationId.Length > 512
            || job.Spec.Parameters.Count > 512)
        {
            throw new UnauthorizedAccessException("Job intent exceeds the execution lease bounds.");
        }
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in job.Spec.Parameters)
        {
            if (parameter.Key.Length > 512 || parameter.Value is null || parameter.Value.Length > 65536)
            {
                throw new UnauthorizedAccessException("Job parameters exceed the execution lease bounds.");
            }
            parameters.Add(parameter.Key, parameter.Value);
        }
        var context = job.Audit.SubmitterSecurityContext;
        if (context is not null)
        {
            _ = JobSecurityContextFingerprint.Compute(context);
            context = context with { ExecutionLease = null };
        }
        var intent = new JobExecutionLeaseIntent(
            1, job.OperationId, job.CreatedAt, job.AcceptanceDeadline, job.Priority,
            job.Spec with { Parameters = parameters },
            job.Audit with { SubmitterSecurityContext = context },
            job.Concurrency, job.RetryPolicy, job.HeartbeatPolicy, job.TimeoutPolicy,
            job.AdmissionCostWeight);
        using var stream = new BoundedIntentStream(MaximumBytes);
        JsonSerializer.Serialize(stream, intent, JobExecutionLeaseIntentJsonContext.Default.JobExecutionLeaseIntent);
        return "v1:sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    /// <summary>Hashes the complete original context, excluding only its separate lease and transient authority.</summary>
    /// <param name="context">Original trusted policy and credential context.</param>
    /// <returns>A versioned fingerprint for transient execution scope matching.</returns>
    public static string ComputeContext(JobSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = JobSecurityContextFingerprint.Compute(context);
        using var stream = new BoundedIntentStream(MaximumBytes);
        JsonSerializer.Serialize(stream, context with { ExecutionLease = null }, JobExecutionLeaseIntentJsonContext.Default.JobSecurityContext);
        return "v1:sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private sealed class BoundedIntentStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            RequireCapacity(1);
            base.WriteByte(value);
        }

        private void RequireCapacity(int count)
        {
            if (count > maximumBytes - Length)
            {
                throw new UnauthorizedAccessException("Job intent exceeds the execution lease byte limit.");
            }
        }
    }
}

internal sealed record JobExecutionLeaseIntent(
    int Version,
    string JobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptanceDeadline,
    OperationPriority Priority,
    ExecutionJobSpec Spec,
    OperationAuditInfo Audit,
    OperationConcurrencyPolicy Concurrency,
    JobRetryPolicy? RetryPolicy,
    JobHeartbeatPolicy? HeartbeatPolicy,
    JobTimeoutPolicy? TimeoutPolicy,
    double? AdmissionCostWeight);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JobExecutionLeaseIntent))]
[JsonSerializable(typeof(JobSecurityContext))]
internal sealed partial class JobExecutionLeaseIntentJsonContext : JsonSerializerContext;
