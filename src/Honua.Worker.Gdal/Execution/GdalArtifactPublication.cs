// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Worker.Gdal.Execution;

/// <summary>Outcome of publishing one file produced by a native GP tool.</summary>
internal sealed record GdalArtifactPublicationResult(long SizeBytes, string? ErrorMessage = null)
{
    /// <summary>Whether the file was published.</summary>
    public bool Succeeded => ErrorMessage is null;
}

/// <summary>
/// Worker-private extension implemented by the raster staging execution context. Keeping file
/// publication off the public job substrate lets a native worker stream large outputs directly to
/// object storage while the durable job record receives only a bounded manifest reference.
/// </summary>
internal interface IGdalFileArtifactPublishingContext
{
    /// <summary>Publishes a file, staging durable raster formats and bounding inline artifacts.</summary>
    Task<GdalArtifactPublicationResult> PublishFileArtifactAsync(
        string path,
        string contentType,
        long maximumInlineBytes,
        string artifactLabel,
        CancellationToken cancellationToken = default);
}

/// <summary>Shared publication path for native tools that write a file.</summary>
internal static class GdalArtifactPublication
{
    /// <summary>
    /// Streams through the raster staging context when present. The Redis-free dev seam and older
    /// unit-test contexts retain the bounded data-URI behavior for compatibility.
    /// </summary>
    public static async Task<GdalArtifactPublicationResult> PublishFileAsync(
        IJobExecutionContext context,
        string path,
        string contentType,
        long maximumInlineBytes,
        string artifactLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumInlineBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactLabel);

        if (context is IGdalFileArtifactPublishingContext fileContext)
        {
            return await fileContext.PublishFileArtifactAsync(
                path,
                contentType,
                maximumInlineBytes,
                artifactLabel,
                cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return new GdalArtifactPublicationResult(0, $"{artifactLabel} is empty.");
        }

        if (info.Length > maximumInlineBytes)
        {
            return new GdalArtifactPublicationResult(
                info.Length,
                $"{artifactLabel} size {info.Length} bytes exceeds configured MaxArtifactBytes={maximumInlineBytes}.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        await context.PublishArtifactAsync(
            GdalDataUri.Build(contentType, bytes),
            cancellationToken).ConfigureAwait(false);
        return new GdalArtifactPublicationResult(bytes.LongLength);
    }
}
