// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Tiles;

// A lookup whose source binding does not match the job resolves as not found, so a caller cannot probe another service's jobs.
internal readonly record struct TileExportJobScope(TileExportSourceKind SourceKind, string ResourceId);

// The download URL is signed when the result is read and is never stored on the job.
internal readonly record struct TileExportResultArtifact(
    string DownloadUrl,
    DateTimeOffset? ExpiresAt,
    TileExportPackageFormat Format,
    long? SizeBytes);

internal interface ITileExportJobService
{
    Task<ExecutionJobRecord> SubmitAsync(
        TileExportJobPlan plan,
        string? idempotencyKey,
        string? correlationId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<ExecutionJobRecord> GetStatusAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<TileExportResultArtifact> GetResultAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task CancelAsync(
        string jobId,
        TileExportJobScope scope,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}
