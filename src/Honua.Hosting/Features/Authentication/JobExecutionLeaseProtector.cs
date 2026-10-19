// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Protects an already-authorized immutable lease binding. This primitive does not issue
/// authority: the canonical issuance policy and resource adapters must validate first.
/// </summary>
internal sealed class JobExecutionLeaseProtector(IDataProtectionProvider protection, TimeProvider clock)
{
    private const int MaximumProtectedCharacters = 2097152;
    private const int MaximumPlaintextBytes = 1048576;
    private readonly IDataProtector _protector = protection.CreateProtector("Honua.JobExecutionLease.v1");
    private readonly ConditionalWeakTable<SealedJobExecutionLeaseBinding, VerifiedPurpose> _validated = new();

    internal JobExecutionLease ProtectValidatedBinding(ExecutionJobRecord job, JobExecutionLeaseScope scope,
        JobExecutionLeasePolicyDecision decision, JobExecutionLeaseAdmissionBinding? admission,
        DateTimeOffset expiresAt)
    {
        var context = job.Audit.SubmitterSecurityContext
            ?? throw Denied();
        var now = clock.GetUtcNow();
        if (context.ExecutionLease is not null || context.AgentCredential is null
            || context.PrincipalId is null || context.OwnerActorId != context.PrincipalId
            || job.Audit.RequestedBy != context.OwnerActorId || job.Audit.TenantId != context.TenantId
            || !decision.IsAllowed || string.IsNullOrWhiteSpace(decision.DecisionId)
            || expiresAt <= now || expiresAt > decision.MaximumExpiresAt
            || expiresAt - now > TimeSpan.FromHours(24))
        {
            throw Denied();
        }
        ValidateScope(scope, job.Spec.Kind);
        if (admission is not null && (admission.AuthorityRevision < 1 || admission.TaskRevision < 1
            || admission.PolicyContextFingerprint != JobSecurityContextFingerprint.Compute(context)
            || string.IsNullOrWhiteSpace(admission.AuthorityFingerprint)))
        {
            throw Denied();
        }
        var binding = new SealedJobExecutionLeaseBinding
        {
            Version = 1,
            LeaseId = Guid.NewGuid().ToString("N"),
            JobId = job.OperationId,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            IntentFingerprint = JobExecutionLeaseIntentFingerprint.Compute(job),
            Scope = scope,
            Decision = decision,
            Admission = admission
        };
        using var payload = new BoundedLeaseStream();
        JsonSerializer.Serialize(payload, binding, JobExecutionLeaseJsonContext.Default.SealedJobExecutionLeaseBinding);
        var plaintext = payload.ToArray();
        try
        {
            return new JobExecutionLease
            {
                LeaseId = binding.LeaseId, JobId = binding.JobId, IssuedAt = binding.IssuedAt,
                ExpiresAt = binding.ExpiresAt, ProtectedAuthority = Convert.ToBase64String(_protector.Protect(plaintext))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        }
    }

    /// <summary>Matches a protected lease against an authoritative job; never trusts the caller's job alone.</summary>
    internal SealedJobExecutionLeaseBinding UnprotectAndMatchAuthoritativeJob(ExecutionJobRecord authoritativeJob)
        => Unprotect(authoritativeJob, historical: false);

    // Historical proof validates a retained terminal receipt, never execution authority.
    internal SealedJobExecutionLeaseBinding UnprotectHistoricalTerminalJob(ExecutionJobRecord authoritativeJob)
        => Unprotect(authoritativeJob, historical: true);

    private SealedJobExecutionLeaseBinding Unprotect(ExecutionJobRecord authoritativeJob, bool historical)
    {
        if (historical && !IsTerminal(authoritativeJob))
        {
            throw Denied();
        }
        var lease = authoritativeJob.Audit.SubmitterSecurityContext?.ExecutionLease ?? throw Denied();
        if (lease.ProtectedAuthority.Length > MaximumProtectedCharacters || (!historical && lease.ExpiresAt <= clock.GetUtcNow())
            || lease.JobId != authoritativeJob.OperationId)
        {
            throw Denied();
        }
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(Convert.FromBase64String(lease.ProtectedAuthority));
            if (plaintext.Length > MaximumPlaintextBytes)
            {
                throw Denied();
            }
            var binding = JsonSerializer.Deserialize(plaintext,
                JobExecutionLeaseJsonContext.Default.SealedJobExecutionLeaseBinding) ?? throw Denied();
            if (binding.Version != 1 || binding.LeaseId != lease.LeaseId || binding.JobId != lease.JobId
                || binding.IssuedAt != lease.IssuedAt || binding.ExpiresAt != lease.ExpiresAt
                || binding.IssuedAt > clock.GetUtcNow() || binding.ExpiresAt <= binding.IssuedAt
                || binding.ExpiresAt - binding.IssuedAt > TimeSpan.FromHours(24)
                || !binding.Decision.IsAllowed || binding.Decision.MaximumExpiresAt < binding.ExpiresAt
                || binding.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(authoritativeJob))
            {
                throw Denied();
            }
            ValidateScope(binding.Scope, authoritativeJob.Spec.Kind);
            _validated.Add(binding, new VerifiedPurpose(historical, lease));
            return binding;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            throw new UnauthorizedAccessException("The job execution lease is invalid or no longer current.", exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    // A deserialized lookalike is not proof. Only this protector's successful verification
    // can enable the lease-specific lineage path, and its original job is checked again.
    internal void RequireVerifiedCurrentBinding(SealedJobExecutionLeaseBinding binding, ExecutionJobRecord job)
    {
        if (!_validated.TryGetValue(binding, out var purpose) || purpose.Historical || binding.ExpiresAt <= clock.GetUtcNow()
            || job.Audit.SubmitterSecurityContext?.ExecutionLease != purpose.Lease
            || binding.JobId != job.OperationId
            || binding.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job))
        {
            throw Denied();
        }
    }

    internal void RequireVerifiedHistoricalBinding(SealedJobExecutionLeaseBinding binding, ExecutionJobRecord job)
    {
        if (!_validated.TryGetValue(binding, out var purpose) || !purpose.Historical || !IsTerminal(job)
            || job.Audit.SubmitterSecurityContext?.ExecutionLease != purpose.Lease
            || binding.JobId != job.OperationId
            || binding.IntentFingerprint != JobExecutionLeaseIntentFingerprint.Compute(job))
        {
            throw Denied();
        }
    }

    private static bool IsTerminal(ExecutionJobRecord job)
        => job.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    private sealed record VerifiedPurpose(bool Historical, JobExecutionLease Lease);

    private static void ValidateScope(JobExecutionLeaseScope scope, ExecutionJobKind kind)
    {
        if (scope.Kind != kind || scope.Resources.Length > 128 || scope.Operations.Length is < 1 or > 128
            || scope.ProviderBinding.Length > 65536)
        {
            throw Denied();
        }
        foreach (var resource in scope.Resources)
        {
            if (!Enum.IsDefined(resource.ResourceType) || !Enum.IsDefined(resource.Operation)
                || string.IsNullOrWhiteSpace(resource.ResourceId) || resource.ResourceId.Length > 1024)
            {
                throw Denied();
            }
        }
        foreach (var operation in scope.Operations)
        {
            if (!Enum.IsDefined(operation.ResourceType) || !Enum.IsDefined(operation.Operation)
                || operation.ResourceId?.Length > 1024)
            {
                throw Denied();
            }
        }
    }

    private sealed class BoundedLeaseStream : MemoryStream
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

        private void RequireCapacity(int count)
        {
            if (count > MaximumPlaintextBytes - Length)
            {
                throw Denied();
            }
        }
    }

    private static UnauthorizedAccessException Denied()
        => new("The job execution lease is invalid or no longer current.");
}

internal sealed record SealedJobExecutionLeaseBinding
{
    public required int Version { get; init; }
    public required string LeaseId { get; init; }
    public required string JobId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string IntentFingerprint { get; init; }
    public required JobExecutionLeaseScope Scope { get; init; }
    public required JobExecutionLeasePolicyDecision Decision { get; init; }
    public JobExecutionLeaseAdmissionBinding? Admission { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SealedJobExecutionLeaseBinding))]
internal sealed partial class JobExecutionLeaseJsonContext : JsonSerializerContext;
