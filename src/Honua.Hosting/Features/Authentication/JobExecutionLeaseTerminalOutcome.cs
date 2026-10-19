// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Authentication;

// Terminal output is deliberately outside immutable execution intent. Historical
// reads pin a separate bounded snapshot across every asynchronous authority check.
internal static class JobExecutionLeaseTerminalOutcome
{
    internal static string Compute(ExecutionJobRecord job)
    {
        var artifacts = job.ArtifactReferences.ToArray();
        var committed = job.CommittedEffectReferences.ToArray();
        if (artifacts.Length > 4096 || committed.Length > 4096
            || artifacts.Concat(committed).Any(value => value is null || value.Length > 8192)
            || artifacts.Concat(committed).Sum(value => (long)value.Length) > 262144
            || job.FileImportOutput is { } output && (output.Schema.Length > 1024 || output.PhysicalTableName.Length > 1024))
            throw new UnauthorizedAccessException("The terminal outcome exceeds the supported observation bound.");
        if (job.CurrentPhase is { Length: > 2048 } || job.CommitRecovery is { } recovery
            && (recovery.IntentFingerprint.Length > 256 || recovery.WorkerId.Length > 2048 || recovery.FailureMessage is { Length: > 2048 }))
            throw new UnauthorizedAccessException("The reconciliation metadata exceeds the supported observation bound.");
        var value = new JobTerminalOutcome(job.Status, job.FileImportOutput, artifacts, committed,
            job.CommitRecovery, job.ImportExecutionAttemptCount, job.CancellationRequestedAt, job.CurrentPhase);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,
            JobTerminalOutcomeJsonContext.Default.JobTerminalOutcome)));
    }
}

internal sealed record JobTerminalOutcome(ExecutionJobStatus Status, FileImportOutputReceipt? Output,
    string[] Artifacts, string[] CommittedEffects, ExecutionJobCommitRecovery? CommitRecovery,
    int ExecutionAttemptCount, DateTimeOffset? CancellationRequestedAt, string? Phase);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JobTerminalOutcome))]
internal sealed partial class JobTerminalOutcomeJsonContext : JsonSerializerContext;
