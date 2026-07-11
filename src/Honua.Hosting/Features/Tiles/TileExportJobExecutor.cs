// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Tiles;

internal interface ITileExportPackageProducer
{
    bool CanProduce(TileExportJobPlan plan);

    Task ProduceAsync(TileExportJobPlan plan, Stream destination, CancellationToken cancellationToken);
}

internal interface ITileExportSourceFence
{
    TileExportSourceKind SourceKind { get; }

    ValueTask<bool> IsAvailableAsync(TileExportJobPlan plan, CancellationToken cancellationToken);
}

internal sealed partial class TileExportJobExecutor(
    ICloudFileStorage storage,
    IEnumerable<ITileExportPackageProducer> producers,
    IEnumerable<ITileExportSourceFence> sourceFences,
    TimeProvider timeProvider,
    ILogger<TileExportJobExecutor> logger) : IJobExecutor
{
    public ExecutionJobKind Kind => ExecutionJobKind.TileExport;

    public IReadOnlySet<string> AcceptedRuntimeProfiles { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, TileExportExecutionSpecBuilder.RuntimeProfile);

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!TileExportExecutionSpecBuilder.TryParse(job.Spec.Parameters, out var plan, out var parseError))
            return JobExecutionResult.Failed(parseError ?? "Invalid tile-export job plan.");

        var parsedPlan = plan!;
        var matchingFences = sourceFences.Where(candidate => candidate.SourceKind == parsedPlan.SourceKind).Take(2).ToArray();
        if (matchingFences.Length != 1 ||
            !await matchingFences[0].IsAvailableAsync(parsedPlan, cancellationToken).ConfigureAwait(false))
        {
            return JobExecutionResult.Failed("Pinned tile-export source is unavailable or has changed.");
        }

        var artifactKey = TileExportArtifactIdentity.BuildObjectKey(parsedPlan);
        var existing = await storage.GetMetadataAsync(artifactKey, cancellationToken).ConfigureAwait(false);
        if (IsReusable(existing, parsedPlan))
        {
            await context.ReportProgressAsync(100, "Reused existing tile package", cancellationToken).ConfigureAwait(false);
            await context.PublishArtifactAsync(artifactKey, cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }

        var matchingProducers = producers.Where(candidate => candidate.CanProduce(parsedPlan)).Take(2).ToArray();
        if (matchingProducers.Length == 0)
            return JobExecutionResult.Failed("No tile-export package producer can execute this plan.");
        if (matchingProducers.Length > 1)
            return JobExecutionResult.Failed("Multiple tile-export package producers matched this plan.");

        var producer = matchingProducers[0];

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"honua-tile-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await context.ReportProgressAsync(0, "Generating tile package", cancellationToken).ConfigureAwait(false);
            await using var temporaryFile = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var bounded = new BoundedWriteStream(temporaryFile, parsedPlan.MaxArtifactBytes, leaveOpen: true))
            {
                await producer.ProduceAsync(parsedPlan, bounded, cancellationToken).ConfigureAwait(false);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (temporaryFile.Length == 0)
                return JobExecutionResult.Failed("Tile-export package producer emitted an empty artifact.");

            temporaryFile.Position = 0;
            await context.ReportProgressAsync(90, "Publishing tile package", cancellationToken).ConfigureAwait(false);
            var upload = await storage.UploadAsync(new FileUploadRequest
            {
                Content = temporaryFile,
                FileName = Path.GetFileName(artifactKey),
                ContentType = parsedPlan.PackageFormat == TileExportPackageFormat.Zip
                    ? "application/zip"
                    : "application/octet-stream",
                SizeBytes = temporaryFile.Length,
                TimeToLive = TimeSpan.FromSeconds(parsedPlan.RetentionSeconds),
                ObjectKeyOverride = artifactKey,
                Metadata = ImmutableDictionary<string, string>.Empty
                    .Add(TileExportArtifactIdentity.IdentityMetadataKey, TileExportArtifactIdentity.Compute(parsedPlan))
                    .Add("honua-job-kind", ExecutionJobKind.TileExport.ToString())
            }, cancellationToken).ConfigureAwait(false);

            if (!upload.Success || upload.File is null)
                return JobExecutionResult.Failed(upload.ErrorMessage ?? "Tile-export artifact upload failed.");

            await context.PublishArtifactAsync(upload.File.FileId, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Tile package ready", cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
        catch (TileExportArtifactLimitExceededException exception)
        {
            return JobExecutionResult.Failed(exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.ExecutionFailed(logger, context.OperationId, exception);
            return JobExecutionResult.Failed("Tile-export package generation failed.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException exception)
            {
                Log.CleanupFailed(logger, context.OperationId, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                Log.CleanupFailed(logger, context.OperationId, exception);
            }
        }
    }

    private bool IsReusable(CloudFile? file, TileExportJobPlan plan)
    {
        var minimumExpiry = timeProvider.GetUtcNow().AddSeconds(plan.RetentionSeconds);
        return file is
        {
            SizeBytes: > 0,
            ExpiresAt: { } expiresAt
        } &&
        file.SizeBytes <= plan.MaxArtifactBytes &&
        // Retention is intentionally outside content identity. Reuse is safe only when the
        // existing object covers the complete requested horizon; equality is sufficient.
        expiresAt >= minimumExpiry &&
        file.Metadata.TryGetValue(TileExportArtifactIdentity.IdentityMetadataKey, out var identity) &&
        string.Equals(identity, TileExportArtifactIdentity.Compute(plan), StringComparison.Ordinal);
    }

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes, bool leaveOpen) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void EnsureCapacity(int writeCount)
        {
            if (writeCount < 0 || inner.Position > maximumBytes - writeCount)
                throw new TileExportArtifactLimitExceededException(maximumBytes);
        }
    }

    private sealed class TileExportArtifactLimitExceededException(long limit)
        : IOException($"Tile-export artifact limit of {limit} bytes was exceeded.");

    private static partial class Log
    {
        [LoggerMessage(9260, LogLevel.Error, "Tile-export job {OperationId} failed")]
        internal static partial void ExecutionFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9261, LogLevel.Warning, "Could not delete tile-export temporary file for job {OperationId}")]
        internal static partial void CleanupFailed(ILogger logger, string operationId, Exception exception);
    }
}
