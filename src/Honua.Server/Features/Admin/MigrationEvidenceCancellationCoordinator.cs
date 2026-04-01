// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Admin;

internal static class MigrationEvidenceCancellationCoordinator
{
    public static async Task<MigrationEvidenceCancellationDecision> RequestAsync(
        string jobId,
        MigrationEvidenceProgress progress,
        MigrationEvidenceJobManager jobManager,
        MigrationEvidenceCancellationTokens cancellationTokens,
        CancellationToken cancellationToken)
    {
        if (!MigrationEvidenceEndpoints.IsActiveStatus(progress.Status))
        {
            return MigrationEvidenceCancellationDecision.Conflict($"Cannot cancel job in {progress.Status} status");
        }

        MigrationEvidenceJobState? jobState;
        try
        {
            jobState = await jobManager.RequestStore.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DistributedCoordinationMode.IsDurableCoordinationUnavailable(ex))
        {
            return MigrationEvidenceCancellationDecision.Unavailable(
                "Distributed migration evidence coordination is unavailable. Retry cancellation when Redis is healthy.");
        }

        if (jobState == null)
        {
            return MigrationEvidenceCancellationDecision.Conflict("Migration evidence job is no longer cancellable");
        }

        if (jobState.ReportPersistedAt is not null)
        {
            return MigrationEvidenceCancellationDecision.Conflict("Migration evidence job is no longer cancellable");
        }

        if (!jobState.CancellationRequested)
        {
            try
            {
                await jobManager.RequestStore.SetProgressAsync(
                    jobId,
                    jobState with
                    {
                        CancellationRequested = true,
                        CancellationRequestedAt = DateTimeOffset.UtcNow
                    },
                    TimeSpan.FromHours(24),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (DistributedCoordinationMode.IsDurableCoordinationUnavailable(ex))
            {
                return MigrationEvidenceCancellationDecision.Unavailable(
                    "Distributed migration evidence coordination is unavailable. Retry cancellation when Redis is healthy.");
            }
        }

        _ = cancellationTokens.Cancel(jobId);
        return MigrationEvidenceCancellationDecision.Requested();
    }
}

internal readonly record struct MigrationEvidenceCancellationDecision(bool Success, int StatusCode, string Message)
{
    public static MigrationEvidenceCancellationDecision Requested()
        => new(true, StatusCodes.Status200OK, "Migration evidence job cancellation requested");

    public static MigrationEvidenceCancellationDecision Conflict(string message)
        => new(false, StatusCodes.Status409Conflict, message);

    public static MigrationEvidenceCancellationDecision Unavailable(string message)
        => new(false, StatusCodes.Status503ServiceUnavailable, message);
}
