// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Shared sanitizer that enforces the release-safe shape of
/// <see cref="MigrationRunCheckpoint"/> values written to a checkpoint store. URLs and
/// credential-like strings are stripped from the resume marker; markers are truncated
/// to a deterministic upper bound.
/// </summary>
public static class MigrationRunCheckpointSanitizer
{
    /// <summary>Maximum resume-marker length tolerated by checkpoint stores.</summary>
    public const int MaxMarkerLength = 128;

    /// <summary>Replacement value used when a marker contains URL-like content.</summary>
    public const string RedactedMarker = "[redacted-marker]";

    /// <summary>
    /// Return a sanitized copy of <paramref name="checkpoint"/> with URL-like markers
    /// redacted, marker length capped, and run identifier trimmed.
    /// </summary>
    public static MigrationRunCheckpoint Sanitize(MigrationRunCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        return checkpoint with
        {
            RunId = SafeRunId(checkpoint.RunId),
            Phase = SafePhase(checkpoint.Phase),
            ResumeMarker = SafeMarker(checkpoint.ResumeMarker),
            CompletedItemCount = checkpoint.CompletedItemCount < 0 ? 0 : checkpoint.CompletedItemCount,
            Attempt = checkpoint.Attempt < 1 ? 1 : checkpoint.Attempt
        };
    }

    private static string SafeRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var trimmed = runId.Trim();
        return trimmed.Length > MaxMarkerLength ? trimmed[..MaxMarkerLength] : trimmed;
    }

    private static string SafePhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return phase.Trim();
    }

    private static string SafeMarker(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return string.Empty;
        var trimmed = marker.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return RedactedMarker;
        }

        return trimmed.Length > MaxMarkerLength ? trimmed[..MaxMarkerLength] : trimmed;
    }
}
