// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Server.Features.Admin.Jobs;

/// <summary>
/// Sanitized, read-only per-step glass-box projection for the console job detail.
/// Surfaces the depth the dev CLI shows — the per-step phase timeline, the GDAL
/// command, and an artifact summary — but over the PRODUCTION HTTP surface, with
/// every client-visible string routed through <see cref="WorkspacePathSanitizer"/>
/// (the same redaction the GDAL worker applies to <c>ErrorMessage</c>) so scratch
/// workspace paths never leak. This is distinct from the dev-only, unsanitized P7
/// glass-box, which is untouched.
/// </summary>
internal sealed partial class ConsoleJobService
{
    // Metadata keys an executor / the GP devkit may persist onto an execution-log
    // entry to carry the per-step command line. Surfaced (sanitized) as the step's
    // Command so the console renders it once the devkit (#2128) begins recording it.
    private static readonly string[] CommandMetadataKeys =
    [
        "command",
        "commandLine",
        "gdalCommand",
        "toolCommand"
    ];

    public async Task<ConsoleJobStepsResponse?> GetStepsAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await GetAuthorizedJobAsync(context, jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return null;
        }

        var logStore = serviceProvider.GetService<IExecutionLogStore>();
        if (logStore == null)
        {
            return new ConsoleJobStepsResponse
            {
                JobId = job.OperationId,
                CorrelationId = job.Audit.CorrelationId,
                State = "unavailable",
                Steps = Array.Empty<ConsoleJobStep>()
            };
        }

        var entries = (await logStore.GetLogsAsync(job.OperationId, cancellationToken).ConfigureAwait(false))
            .Where(static e => e is not null)
            .OrderBy(static e => e.Timestamp)
            .ToArray();

        var workspace = await ResolveWorkspaceAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        var artifactSummary = await BuildStepArtifactSummaryAsync(job, cancellationToken).ConfigureAwait(false);

        var steps = new ConsoleJobStep[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var isLast = i == entries.Length - 1;
            // A step's end is bounded by the next entry's timestamp (it ran until the next
            // logged event); the final step's end is the job's terminal time when known.
            var nextTimestamp = isLast ? job.CompletedAt : entries[i + 1].Timestamp;
            steps[i] = MapStep(job, entries[i], i, workspace, artifactSummary, isLast, nextTimestamp);
        }

        return new ConsoleJobStepsResponse
        {
            JobId = job.OperationId,
            CorrelationId = job.Audit.CorrelationId,
            State = "available",
            Steps = steps
        };
    }

    private static ConsoleJobStep MapStep(
        ExecutionJobRecord job,
        ExecutionLogEntry entry,
        int ordinal,
        string? workspace,
        ConsoleJobStepArtifact[]? artifactSummary,
        bool isLast,
        DateTimeOffset? completedAt)
    {
        var duration = completedAt.HasValue && completedAt.Value >= entry.Timestamp
            ? (long?)(completedAt.Value - entry.Timestamp).TotalMilliseconds
            : null;

        return new ConsoleJobStep
        {
            Ordinal = ordinal,
            Phase = string.IsNullOrWhiteSpace(entry.Phase)
                ? (string.IsNullOrWhiteSpace(job.CurrentPhase) ? job.Status.ToString() : job.CurrentPhase)
                : entry.Phase,
            Status = entry.Level == ExecutionLogLevel.Error
                ? ExecutionJobStatus.Failed.ToString()
                : entry.Level.ToString(),
            StartedAt = entry.Timestamp,
            CompletedAt = completedAt,
            DurationMs = duration,
            Message = WorkspacePathSanitizer.SanitizeForClient(entry.Message, workspace),
            Command = ExtractSanitizedCommand(entry, workspace),
            Artifacts = isLast ? artifactSummary : null,
            Metadata = SanitizeStepMetadata(entry.Metadata, workspace)
        };
    }

    private static string? ExtractSanitizedCommand(ExecutionLogEntry entry, string? workspace)
    {
        if (entry.Metadata == null)
        {
            return null;
        }

        // Not a simple filter: the first matching key's value is looked up, validated, and
        // sanitized before being returned, so a LINQ Where/Select would not simplify this.
        foreach (var key in CommandMetadataKeys)
        {
            if (entry.Metadata.TryGetValue(key, out var command) && !string.IsNullOrWhiteSpace(command))
            {
                return WorkspacePathSanitizer.SanitizeForClient(command, workspace);
            }
        }

        return null;
    }

    private static Dictionary<string, string>? SanitizeStepMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        string? workspace)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return null;
        }

        var sanitized = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            // Command keys are surfaced via the dedicated Command field; keep them out of
            // the generic metadata bag to avoid duplicating the (already sanitized) command.
            if (Array.IndexOf(CommandMetadataKeys, key) >= 0)
            {
                continue;
            }

            if (ContainsSecretToken(key))
            {
                continue;
            }

            sanitized[key] = WorkspacePathSanitizer.SanitizeForClient(value, workspace);
        }

        return sanitized.Count == 0 ? null : sanitized;
    }

    private async Task<ConsoleJobStepArtifact[]?> BuildStepArtifactSummaryAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        if (job.ArtifactReferences.Count == 0)
        {
            return null;
        }

        var artifactStore = serviceProvider.GetService<IArtifactStore>();
        var summary = new List<ConsoleJobStepArtifact>(job.ArtifactReferences.Count);

        foreach (var reference in job.ArtifactReferences)
        {
            if (artifactStore == null)
            {
                summary.Add(new ConsoleJobStepArtifact { Label = reference });
                continue;
            }

            try
            {
                var artifact = await artifactStore.GetAsync(reference, cancellationToken).ConfigureAwait(false);
                summary.Add(artifact == null
                    ? new ConsoleJobStepArtifact { Label = reference }
                    : new ConsoleJobStepArtifact
                    {
                        Label = string.IsNullOrWhiteSpace(artifact.Label) ? artifact.ArtifactId : artifact.Label,
                        Kind = artifact.Kind.ToString(),
                        SizeBytes = artifact.SizeBytes
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ArtifactLookupFailed(logger, reference, ex);
                summary.Add(new ConsoleJobStepArtifact { Label = reference });
            }
        }

        return summary.Count == 0 ? null : summary.ToArray();
    }

    // The per-job scratch workspace path (`<scratchRoot>/<operationId>`) is not stored on
    // the durable job record exposed over HTTP, so we cannot always reconstruct the exact
    // string. We derive the operationId-bearing tail as a best-effort hint for the targeted
    // <scratch> replacement; the defensive absolute-path sweep in SanitizeForClient catches
    // any residual path regardless. Returns null when no targeted hint is available.
    private static Task<string?> ResolveWorkspaceAsync(string operationId, CancellationToken cancellationToken)
    {
        _ = operationId;
        _ = cancellationToken;
        // No durable workspace path is available on the job record; rely on the defensive
        // absolute-path redaction. A future devkit change that persists the workspace path
        // onto job metadata can populate a targeted replacement here.
        return Task.FromResult<string?>(null);
    }
}
