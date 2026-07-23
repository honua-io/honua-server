// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Studio.Export;

/// <summary>
/// Default <see cref="IStudioDeliverableExporter"/> that loads a Studio content version through the
/// canonical lifecycle service, composes it with <see cref="StudioDeliverableComposer"/>, and
/// optionally persists the artifact via cloud storage.
/// </summary>
internal sealed class StudioDeliverableExporter : IStudioDeliverableExporter
{
    private const string ShareFolder = "studio-deliverables";
    private static readonly TimeSpan ShareUrlLifetime = TimeSpan.FromDays(7);

    private readonly IStudioPackageLifecycleService _lifecycle;
    private readonly ICloudFileStorage _storage;
    private readonly ILogger<StudioDeliverableExporter> _logger;

    public StudioDeliverableExporter(
        IStudioPackageLifecycleService lifecycle,
        ICloudFileStorage storage,
        ILogger<StudioDeliverableExporter> logger)
    {
        _lifecycle = lifecycle;
        _storage = storage;
        _logger = logger;
    }

    public async Task<StudioDeliverableExportResult> ExportAsync(
        StudioPackageFamily kind,
        Guid itemId,
        StudioDeliverableFormat format,
        Guid? versionId = null,
        bool store = false,
        CancellationToken cancellationToken = default)
    {
        var version = await ResolveVersionAsync(itemId, versionId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            return StudioDeliverableExportResult.CreateNotFound(
                versionId is null
                    ? "No Studio content versions were found for the requested item."
                    : "The requested Studio content version was not found.");
        }

        if (version.Envelope.Family != kind)
        {
            return StudioDeliverableExportResult.CreateKindMismatch(
                $"The requested item is a '{version.Envelope.Family}' package and cannot be exported as a '{kind}' deliverable.");
        }

        var artifact = StudioDeliverableComposer.Compose(version, format);

        string? artifactUrl = null;
        if (store)
        {
            artifactUrl = await TryStoreAsync(artifact, cancellationToken).ConfigureAwait(false);
        }

        StudioDeliverableExportLog.DeliverableExported(_logger, kind, itemId, format, artifact.Content.Length);
        return StudioDeliverableExportResult.CreateSuccess(artifact, artifactUrl);
    }

    private async Task<StudioContentVersion?> ResolveVersionAsync(
        Guid itemId,
        Guid? versionId,
        CancellationToken cancellationToken)
    {
        if (versionId is { } id)
        {
            return await _lifecycle.GetVersionAsync(itemId, id, cancellationToken).ConfigureAwait(false);
        }

        var versions = await _lifecycle.ListVersionsAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return null;
        }

        StudioContentVersion? latest = null;
        // Not a simple .Where(): this is a running-max fold (each iteration compares
        // against the previous iteration's winner), not a filter/projection.
        foreach (var candidate in (versions).Where(candidate => latest is null || candidate.VersionNumber > latest.VersionNumber))
        {
            latest = candidate;
        }

        return latest;
    }

    private async Task<string?> TryStoreAsync(StudioDeliverableArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            var upload = await _storage.UploadAsync(
                new ByteArrayUploadRequest
                {
                    Content = artifact.Content,
                    FileName = artifact.FileName,
                    ContentType = artifact.ContentType,
                    Folder = ShareFolder,
                },
                cancellationToken).ConfigureAwait(false);

            if (!upload.Success || upload.File is null)
            {
                StudioDeliverableExportLog.DeliverableStoreFailed(_logger, artifact.FileName, upload.ErrorMessage ?? "unknown");
                return null;
            }

            return await _storage.GetPresignedUrlAsync(upload.File.FileId, ShareUrlLifetime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioDeliverableExportLog.DeliverableStoreFailed(_logger, artifact.FileName, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Source-generated log messages for Studio deliverable export.
/// </summary>
internal static partial class StudioDeliverableExportLog
{
    [LoggerMessage(
        EventId = 7401,
        Level = LogLevel.Information,
        Message = "Exported Studio {Kind} deliverable for item {ItemId} as {Format} ({Bytes} bytes).")]
    public static partial void DeliverableExported(ILogger logger, StudioPackageFamily kind, Guid itemId, StudioDeliverableFormat format, int bytes);

    [LoggerMessage(
        EventId = 7402,
        Level = LogLevel.Warning,
        Message = "Failed to persist Studio deliverable {FileName} to share storage: {Reason}")]
    public static partial void DeliverableStoreFailed(ILogger logger, string fileName, string reason);
}
