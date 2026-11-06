// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

// URLs and credential-like strings are stripped from the resume marker, and the marker is truncated to a fixed bound.
public static class MigrationRunCheckpointSanitizer
{
    public const int MaxMarkerLength = 128;

    public const string RedactedMarker = "[redacted-marker]";

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
