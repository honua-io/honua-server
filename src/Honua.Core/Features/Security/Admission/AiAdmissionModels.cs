// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Security.Admission;

// This identity is server-resolved and must never be deserialized from caller input.
public sealed record AiExecutionContext
{
    public required string DeploymentId { get; init; }
    // The actor digest is of the authenticated canonical actor, never a display name or a credential.
    public required string ActorKey { get; init; }
    public required string UseCase { get; init; }
    public string? WorkflowKey { get; init; }

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

    public bool IsValid => !string.IsNullOrWhiteSpace(DeploymentId) && DeploymentId.Length <= 128
        && IsDigest(ActorKey) && (WorkflowKey is null || IsDigest(WorkflowKey))
        && UseCase is "studio-chat" or "reporting-narrative" or "imagery-inference";

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsDigest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record AiAdmissionPolicy
{
    public const int MaximumWindowSeconds = 3600;
    public const int MaximumOperationSeconds = 3600;
    public const int DrainSeconds = 30;
    public const int MaximumLedgerEntries = 8192;
    public const int StopPollMilliseconds = 1000;
    public const int RedisBootstrapCooldownSeconds = MaximumOperationSeconds + DrainSeconds;

    public int WindowSeconds { get; init; } = 60;
    // The admitted duration is independent of the provider timeout.
    public int MaxOperationSeconds { get; init; } = 300;
    public int DeploymentAttempts { get; init; } = 120;
    public int ActorAttempts { get; init; } = 30;
    public int CohortAttempts { get; init; } = 20;
    public int WorkflowAttempts { get; init; } = 10;
    public int DeploymentConcurrency { get; init; } = 8;
    public int ActorConcurrency { get; init; } = 4;
    public int CohortConcurrency { get; init; } = 2;
    public int WorkflowConcurrency { get; init; } = 1;

    public AiFinancialPolicy? Financial { get; init; }

    public bool IsValid => WindowSeconds is > 0 and <= MaximumWindowSeconds
        && MaxOperationSeconds is > 0 and <= MaximumOperationSeconds
        && (Financial is null || Financial.IsValid)
        && new[] { DeploymentAttempts, ActorAttempts, CohortAttempts, WorkflowAttempts,
            DeploymentConcurrency, ActorConcurrency, CohortConcurrency, WorkflowConcurrency }
            .All(value => value is > 0 and <= MaximumLedgerEntries);
}

// Missing control state is uninitialized and must never be created by admission.
public sealed record AiAdmissionState(string DeploymentId, AiAdmissionPolicy Policy, Guid Generation, long Revision, long Epoch,
    bool Stopped, DateTimeOffset NotBeforeUtc);

public sealed record AiAdmissionReservation(string DeploymentId, Guid AttemptId, Guid Generation, long Epoch,
    DateTimeOffset DeadlineUtc, DateTimeOffset LeaseExpiresUtc, AiFinancialReservation? Financial = null);

public sealed record AiFinancialReservation(string RequestDigest, string BasisDigest);

public sealed record AiAdmissionDecision(string Code, AiAdmissionReservation? Reservation = null, int RetryAfterSeconds = 1)
{
    public bool Granted => Reservation is not null;
}

public enum AiAdmissionControlAction
{
    // A missing stopped ledger may be created, and an existing ledger is never replaced.
    Initialize,
    UpdatePolicy,
    Stop,
    Resume
}

public sealed record AiAdmissionControlCommand(string DeploymentId, AiAdmissionControlAction Action,
    Guid ExpectedGeneration, long ExpectedRevision, long ExpectedEpoch, string ActorId, string InvocationId, string CorrelationId,
    AiAdmissionPolicy? Policy = null)
{
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

public sealed record AiAdmissionControlResult(string Code, AiAdmissionState? State, bool Applied = false);
