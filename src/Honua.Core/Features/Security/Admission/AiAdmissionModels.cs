// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Security.Admission;

/// <summary>Server-resolved identity for one model invocation. Never deserialize this from caller input.</summary>
public sealed record AiExecutionContext
{
    /// <summary>Stable operator-configured deployment identity, shared by every replica.</summary>
    public required string DeploymentId { get; init; }
    /// <summary>Digest of the authenticated canonical actor, never a display name or credential.</summary>
    public required string ActorKey { get; init; }
    /// <summary>Server-selected workload: studio-chat, reporting-narrative or imagery-inference.</summary>
    public required string UseCase { get; init; }
    /// <summary>Optional digest of a server-validated session, authorized job or workflow run.</summary>
    public string? WorkflowKey { get; init; }

    /// <summary>Creates a bounded context from already verified server identity inputs.</summary>
    public static AiExecutionContext Create(string deploymentId, string canonicalActor, string useCase, string? verifiedWorkflow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalActor);
        if (deploymentId.Length > 128 || canonicalActor.Length > 4096
            || useCase is not ("studio-chat" or "reporting-narrative" or "imagery-inference")
            || (verifiedWorkflow is not null && (string.IsNullOrWhiteSpace(verifiedWorkflow) || verifiedWorkflow.Length > 4096)))
            throw new ArgumentException("Invalid AI admission identity.");
        return new AiExecutionContext
        {
            DeploymentId = deploymentId,
            ActorKey = Digest(canonicalActor),
            UseCase = useCase,
            WorkflowKey = verifiedWorkflow is null ? null : Digest(verifiedWorkflow)
        };
    }

    /// <summary>Whether all persisted keys and the selected workload have their bounded canonical shape.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(DeploymentId) && DeploymentId.Length <= 128
        && IsDigest(ActorKey) && (WorkflowKey is null || IsDigest(WorkflowKey))
        && UseCase is "studio-chat" or "reporting-narrative" or "imagery-inference";

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsDigest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Authoritative positive attempt and concurrency limits. These are not financial or inferred token budgets.</summary>
public sealed record AiAdmissionPolicy
{
    /// <summary>Largest supported rolling window; history is retained for at least this horizon.</summary>
    public const int MaximumWindowSeconds = 3600;
    /// <summary>Largest supported invocation deadline.</summary>
    public const int MaximumOperationSeconds = 3600;
    /// <summary>Additional reservation retention after its invocation deadline.</summary>
    public const int DrainSeconds = 30;
    /// <summary>Hard cardinality bound shared by every backend.</summary>
    public const int MaximumLedgerEntries = 8192;
    /// <summary>Maximum interval between authoritative stop checks.</summary>
    public const int StopPollMilliseconds = 1000;
    /// <summary>Conservative Redis first-setup and lost-ledger bootstrap delay.</summary>
    public const int RedisBootstrapCooldownSeconds = MaximumOperationSeconds + DrainSeconds;

    /// <summary>Rolling accounting window in seconds.</summary>
    public int WindowSeconds { get; init; } = 60;
    /// <summary>Maximum duration of an admitted invocation, independent of provider timeout.</summary>
    public int MaxOperationSeconds { get; init; } = 300;
    /// <summary>Deployment attempt allowance per rolling window.</summary>
    public int DeploymentAttempts { get; init; } = 120;
    /// <summary>Canonical actor attempt allowance per rolling window.</summary>
    public int ActorAttempts { get; init; } = 30;
    /// <summary>Actor/workload cohort allowance per rolling window.</summary>
    public int CohortAttempts { get; init; } = 20;
    /// <summary>Additional verified workflow allowance per rolling window.</summary>
    public int WorkflowAttempts { get; init; } = 10;
    /// <summary>Deployment active reservation limit.</summary>
    public int DeploymentConcurrency { get; init; } = 8;
    /// <summary>Actor active reservation limit.</summary>
    public int ActorConcurrency { get; init; } = 4;
    /// <summary>Actor/workload active reservation limit.</summary>
    public int CohortConcurrency { get; init; } = 2;
    /// <summary>Additional verified workflow active reservation limit.</summary>
    public int WorkflowConcurrency { get; init; } = 1;

    /// <summary>Whether policy values fit the finite, supported accounting horizon and state bounds.</summary>
    public bool IsValid => WindowSeconds is > 0 and <= MaximumWindowSeconds
        && MaxOperationSeconds is > 0 and <= MaximumOperationSeconds
        && new[] { DeploymentAttempts, ActorAttempts, CohortAttempts, WorkflowAttempts,
            DeploymentConcurrency, ActorConcurrency, CohortConcurrency, WorkflowConcurrency }
            .All(value => value is > 0 and <= MaximumLedgerEntries);
}

/// <summary>Durable control state. Missing state is uninitialized and must never be auto-created by admission.</summary>
public sealed record AiAdmissionState(string DeploymentId, AiAdmissionPolicy Policy, Guid Generation, long Revision, long Epoch,
    bool Stopped, DateTimeOffset NotBeforeUtc);

/// <summary>Single-use server-generated reservation, retained until response/body disposal.</summary>
public sealed record AiAdmissionReservation(string DeploymentId, Guid AttemptId, Guid Generation, long Epoch,
    DateTimeOffset DeadlineUtc, DateTimeOffset LeaseExpiresUtc);

/// <summary>Sanitized result of atomic multi-scope admission.</summary>
public sealed record AiAdmissionDecision(string Code, AiAdmissionReservation? Reservation = null, int RetryAfterSeconds = 1)
{
    /// <summary>Whether one invocation was atomically charged and reserved.</summary>
    public bool Granted => Reservation is not null;
}

/// <summary>Explicit operator control action; no action resets live accounting.</summary>
public enum AiAdmissionControlAction
{
    /// <summary>Create a missing stopped ledger; never replace an existing one.</summary>
    Initialize,
    /// <summary>Change policy only while stopped and drained; retain attempt history.</summary>
    UpdatePolicy,
    /// <summary>Stop admissions and invalidate live permits.</summary>
    Stop,
    /// <summary>Resume after bootstrap/recovery without resetting counters.</summary>
    Resume
}

/// <summary>Command assembled from authenticated canonical operation context, not caller-provided actor metadata.</summary>
public sealed record AiAdmissionControlCommand(string DeploymentId, AiAdmissionControlAction Action,
    Guid ExpectedGeneration, long ExpectedRevision, long ExpectedEpoch, string ActorId, string InvocationId, string CorrelationId,
    AiAdmissionPolicy? Policy = null)
{
    /// <summary>Whether the command fits bounded identity, policy and optimistic-concurrency requirements.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(DeploymentId) && DeploymentId.Length <= 128
        && !string.IsNullOrWhiteSpace(ActorId) && ActorId.Length <= 4096
        && !string.IsNullOrWhiteSpace(InvocationId) && InvocationId.Length <= 128
        && !string.IsNullOrWhiteSpace(CorrelationId) && CorrelationId.Length <= 128
        && ExpectedRevision >= 0 && ExpectedEpoch >= 0 && Enum.IsDefined(Action)
        && (Action is AiAdmissionControlAction.Initialize
            ? ExpectedGeneration == Guid.Empty && ExpectedRevision == 0 && ExpectedEpoch == 0 && Policy is { IsValid: true }
            : ExpectedGeneration != Guid.Empty && ExpectedRevision > 0 && ExpectedEpoch > 0
                && (Action is not AiAdmissionControlAction.UpdatePolicy || Policy is { IsValid: true }));
}

/// <summary>Control outcome retained for idempotent gateway replay.</summary>
public sealed record AiAdmissionControlResult(string Code, AiAdmissionState? State, bool Applied = false);
