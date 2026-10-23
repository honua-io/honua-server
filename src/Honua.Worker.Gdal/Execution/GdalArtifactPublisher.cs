// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Raster.CogParser;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Publishes a produced output file as a durable artifact reference (#3089).
/// When the dispatch context carries a staged-output store, outputs above the
/// configured inline ceiling are streamed to an attempt-scoped immutable object key
/// and published as a typed <see cref="StagedObjectRasterOutputDescriptor"/>; outputs
/// at or below the ceiling publish as bounded typed inline descriptors. Payload bytes
/// never enter the durable job record on this path. Without a store the legacy
/// bounded <c>data:</c>-URI publication (capped by
/// <see cref="GdalWorkerOptions.MaxArtifactBytes"/>) is preserved unchanged.
/// </summary>
internal static partial class GdalArtifactPublisher
{
    /// <summary>
    /// Publishes <paramref name="outputPath"/> and returns null on success, or a
    /// client-safe failure message the caller should surface as
    /// <c>JobExecutionResult.Failed</c>.
    /// </summary>
    public static async Task<string?> PublishFileAsync(
        IJobExecutionContext context,
        GdalWorkerOptions options,
        ILogger logger,
        string operationId,
        string outputPath,
        string contentType,
        string artifactLabel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length == 0)
        {
            return $"{artifactLabel} is empty or missing.";
        }

        if (context is not GdalStagedOutputContext staged)
        {
            return await PublishLegacyInlineAsync(
                context, options, logger, operationId, outputPath, info.Length, contentType, artifactLabel,
                cancellationToken).ConfigureAwait(false);
        }

        // Fail closed on a single-object Zarr fiction before any bytes move (#3103
        // owns the multi-object protocol).
        if (RasterOutputDescriptorValidator.LooksLikeZarr(contentType, info.Name))
        {
            Log.ZarrOutputRefused(logger, operationId);
            return "Native Zarr outputs are multi-object hierarchies and cannot be published as a single "
                + "artifact; hierarchy-aware Zarr publication is tracked by #3103.";
        }

        if (info.Length > options.MaxStagedArtifactBytes)
        {
            Log.StagedArtifactTooLarge(logger, operationId, info.Length, options.MaxStagedArtifactBytes);
            return $"{artifactLabel} size {info.Length} bytes exceeds configured "
                + $"MaxStagedArtifactBytes={options.MaxStagedArtifactBytes}.";
        }

        // Descriptor content identity carries a bare IANA media type; transport
        // parameters (e.g. "image/tiff; application=geotiff") stay on the wire format.
        var mediaType = NormalizeMediaType(contentType);
        var outputIndex = staged.NextOutputIndex();
        var outputName = ResolveOutputName(staged.Job.Spec.Parameters, outputIndex);
        var grid = await TryProbeGridAsync(outputPath, contentType, cancellationToken).ConfigureAwait(false);
        var lineage = BuildLineage(staged.Job.Spec.Parameters);
        var inlineCeiling = Math.Min(
            staged.StagingOptions.MaxInlineArtifactBytes,
            RasterOutputContract.MaximumInlinePayloadBytes);

        // An output with a post-success registration intent must be staged regardless
        // of size: only staged objects can register into the COG catalog, and an
        // inline descriptor would deterministically fail registration on every results
        // read, permanently wedging a Succeeded job (#3089 review).
        var hasRegistrationIntent = staged.Job.Spec.Parameters.ContainsKey(
            GdalWorkerParameterKeys.OutputRegistrationPrefix + outputName);

        RasterOutputDescriptor descriptor;
        if (info.Length <= inlineCeiling && !hasRegistrationIntent)
        {
            var bounded = await ReadBoundedPayloadAsync(outputPath, inlineCeiling, cancellationToken)
                .ConfigureAwait(false);
            if (bounded.ExceededLimit)
            {
                return $"{artifactLabel} size {bounded.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds "
                    + "the inline publication ceiling.";
            }

            if (bounded.Payload is not { Length: > 0 } payload)
            {
                return $"{artifactLabel} is empty or missing.";
            }

            descriptor = new InlineRasterOutputDescriptor
            {
                JobId = staged.Job.OperationId,
                AttemptNumber = Math.Max(staged.Job.AttemptCount, 1),
                OutputName = outputName,
                Content = new RasterContentIdentity
                {
                    SizeBytes = payload.Length,
                    MediaType = mediaType,
                    Checksum = new RasterChecksum(
                        "sha256",
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
                            .ToLowerInvariant()),
                },
                Grid = grid,
                ProducingEngine = RasterOutputContract.GdalWorkerEngine,
                Lineage = lineage,
                Payload = payload,
            };
        }
        else
        {
            var attemptNumber = Math.Max(staged.Job.AttemptCount, 1);
            var objectKey = GeoprocessingOutputObjectKeys.Build(
                staged.StagingOptions.KeyPrefix,
                staged.Job.OperationId,
                attemptNumber,
                outputName,
                info.Name);

            RasterContentIdentity content;
            try
            {
                await using var source = new FileStream(
                    outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var bounded = new BoundedReadStream(source, options.MaxStagedArtifactBytes);
                content = await staged.Store
                    .WriteAsync(objectKey, bounded, mediaType, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArtifactByteLimitException ex)
            {
                Log.StagedArtifactTooLarge(logger, operationId, ex.ObservedLength, options.MaxStagedArtifactBytes);
                return $"{artifactLabel} size {ex.ObservedLength.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured "
                    + $"MaxStagedArtifactBytes={options.MaxStagedArtifactBytes.ToString(CultureInfo.InvariantCulture)}.";
            }

            // The object is now readable in the shared store, but no durable artifact
            // reference has been published yet. The live qualification lane cancels
            // precisely here to prove the publication fence and orphan sweep.
            await ExecutionQualificationBarrier.WaitAsync(
                "output-bytes-written-unpublished",
                cancellationToken).ConfigureAwait(false);

            descriptor = new StagedObjectRasterOutputDescriptor
            {
                JobId = staged.Job.OperationId,
                AttemptNumber = attemptNumber,
                OutputName = outputName,
                Content = content,
                Grid = grid,
                ProducingEngine = RasterOutputContract.GdalWorkerEngine,
                Lineage = lineage,
                Provider = staged.Store.Provider,
                StoreReference = staged.Store.StoreReference,
                ObjectKey = objectKey,
            };

            Log.ArtifactStaged(logger, operationId, objectKey, info.Length);
        }

        var validation = RasterOutputDescriptorValidator.Validate(
            descriptor,
            new RasterOutputValidationOptions { MaxInlineBytes = inlineCeiling });
        if (!validation.IsValid)
        {
            var failure = validation.Errors[0];
            Log.DescriptorInvalid(logger, operationId, failure.Code, failure.Message);
            return $"Produced output descriptor is invalid ({failure.Code}): {failure.Message}";
        }

        await context.PublishArtifactAsync(RasterOutputJson.Serialize(descriptor), cancellationToken)
            .ConfigureAwait(false);
        return null;
    }

    private static async Task<string?> PublishLegacyInlineAsync(
        IJobExecutionContext context,
        GdalWorkerOptions options,
        ILogger logger,
        string operationId,
        string outputPath,
        long length,
        string contentType,
        string artifactLabel,
        CancellationToken cancellationToken)
    {
        if (length > options.MaxArtifactBytes)
        {
            Log.InlineArtifactTooLarge(logger, operationId, length, options.MaxArtifactBytes);
            return $"{artifactLabel} size {length} bytes exceeds configured "
                + $"MaxArtifactBytes={options.MaxArtifactBytes}. Configure Geoprocessing:OutputStaging to "
                + "publish large outputs as staged artifact references (#3089).";
        }

        var bounded = await ReadBoundedPayloadAsync(outputPath, options.MaxArtifactBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bounded.ExceededLimit)
        {
            Log.InlineArtifactTooLarge(logger, operationId, bounded.Length, options.MaxArtifactBytes);
            return $"{artifactLabel} size {bounded.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured "
                + $"MaxArtifactBytes={options.MaxArtifactBytes.ToString(CultureInfo.InvariantCulture)}. Configure Geoprocessing:OutputStaging to "
                + "publish large outputs as staged artifact references (#3089).";
        }

        var payload = bounded.Payload ?? [];
        var artifactUri = GdalDataUri.Build(contentType, payload);
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Resolves the stable logical output name the submit path recorded for the slot,
    /// falling back to a deterministic positional name.
    /// </summary>
    internal static string ResolveOutputName(IReadOnlyDictionary<string, string> parameters, int index)
    {
        var candidate = parameters.GetValueOrDefault($"{GdalWorkerParameterKeys.OutputNamePrefix}{index}")
            ?? parameters.GetValueOrDefault($"{GdalWorkerParameterKeys.GPServerOutputNamePrefix}{index}");
        if (!string.IsNullOrWhiteSpace(candidate) && IsSafeOutputName(candidate))
        {
            return candidate;
        }

        return $"output{index + 1}";
    }

    /// <summary>Strips transport parameters, keeping the bare IANA media type.</summary>
    internal static string NormalizeMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        var bare = separator >= 0 ? contentType[..separator] : contentType;
        return bare.Trim();
    }

    private static bool IsSafeOutputName(string value)
        => value.Length <= 160
           && value[0] != '.'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.All(character =>
               char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static RasterOutputLineage BuildLineage(IReadOnlyDictionary<string, string> parameters)
    {
        var sourceReferences = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (!key.StartsWith(GdalWorkerParameterKeys.StepRasterSourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var source = RasterSourceJson.Deserialize(value);
                sourceReferences.Add($"{source.GetType().Name}:{source.Version}");
            }
            catch (System.Text.Json.JsonException)
            {
                // Lineage is best-effort provenance; a malformed source parameter is
                // reported by input validation, not here.
            }
        }

        return new RasterOutputLineage
        {
            ProcessId = parameters.GetValueOrDefault(GdalWorkerParameterKeys.ProcessDefinitions),
            PlanId = parameters.GetValueOrDefault(GdalWorkerParameterKeys.PlanId),
            SourceReferences = sourceReferences,
        };
    }

    /// <summary>
    /// Bounded TIFF header probe for the grid summary; non-TIFF outputs and probe
    /// failures produce a null grid rather than failing publication.
    /// </summary>
    private static async Task<RasterOutputGridSummary?> TryProbeGridAsync(
        string outputPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (!contentType.Contains("tiff", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var probe = await CogRasterHeaderProbe.ReadAsync(
                new LocalFileRangeReader(outputPath),
                LocalFileRangeReader.LocalIdentity,
                LocalFileRangeReader.LocalIdentity,
                LocalFileRangeReader.LocalIdentity,
                cancellationToken).ConfigureAwait(false);

            return new RasterOutputGridSummary
            {
                Width = probe.Dimensions.Width,
                Height = probe.Dimensions.Height,
                BandCount = probe.Dimensions.BandCount,
                BitsPerSample = probe.Dimensions.BitsPerSample,
                PixelScale = probe.PixelScale,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException
            or IOException
            or EndOfStreamException
            // Truncated or hostile TIFF directories can surface as argument/overflow
            // faults from the bounded IFD parser rather than InvalidDataException; the
            // grid summary is best-effort metadata, so any parse fault degrades to a
            // null grid instead of failing the publication.
            or ArgumentOutOfRangeException
            or ArgumentException
            or OverflowException)
        {
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9295, LogLevel.Information,
            "Staged output artifact for job {OperationId} at key {ObjectKey} ({Bytes} bytes)")]
        public static partial void ArtifactStaged(ILogger logger, string operationId, string objectKey, long bytes);

        [LoggerMessage(9296, LogLevel.Warning,
            "Refused staged artifact for job {OperationId}: size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void StagedArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9297, LogLevel.Warning,
            "Refused inline artifact for job {OperationId}: size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void InlineArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9298, LogLevel.Error,
            "Produced output descriptor invalid for job {OperationId}: {Code} {Message}")]
        public static partial void DescriptorInvalid(ILogger logger, string operationId, string code, string message);

        [LoggerMessage(9299, LogLevel.Warning,
            "Refused Zarr output publication for job {OperationId}: single-object Zarr artifacts are not supported (#3103)")]
        public static partial void ZarrOutputRefused(ILogger logger, string operationId);
    }

    /// <summary>
    /// Bytes read from a file that stayed within <paramref name="Length"/>'s cap.
    /// <see cref="ExceededLimit"/> is set when the file was already over the cap
    /// or grew past it during the read; <see cref="Payload"/> is then null.
    /// </summary>
    /// <param name="Payload">The file bytes when the read stayed within the cap.</param>
    /// <param name="Length">Observed length, including a byte that pushed the file over the cap.</param>
    /// <param name="ExceededLimit">True when the cap was exceeded and no payload is retained.</param>
    internal readonly record struct BoundedPayload(byte[]? Payload, long Length, bool ExceededLimit);

    /// <summary>
    /// Reads <paramref name="path"/> only when its length is within
    /// <paramref name="maxBytes"/> and fits in one array. A missing or empty file
    /// returns a null payload. A file that is already over the cap, or that grows
    /// past the cap while it is read, returns <see cref="BoundedPayload.ExceededLimit"/>
    /// without retaining the bytes.
    /// </summary>
    internal static async Task<BoundedPayload> ReadBoundedPayloadAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return new BoundedPayload(null, 0, false);
        }

        if (info.Length > maxBytes || info.Length > int.MaxValue || info.Length > Array.MaxLength)
        {
            return new BoundedPayload(null, info.Length, true);
        }

        var length = (int)info.Length;
        var buffer = new byte[length];
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var extra = new byte[1];
        var extraRead = await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false);
        if (extraRead > 0)
        {
            return new BoundedPayload(null, offset + extraRead, true);
        }

        if (offset == 0)
        {
            return new BoundedPayload(null, 0, false);
        }

        if (offset != buffer.Length)
        {
            Array.Resize(ref buffer, offset);
        }

        return new BoundedPayload(buffer, offset, false);
    }

    /// <summary>
    /// Raised when a staged read observes a byte past the configured cap.
    /// The extra byte is not returned to the caller.
    /// </summary>
    private sealed class ArtifactByteLimitException : IOException
    {
        public ArtifactByteLimitException(long observedLength)
            : base("Artifact read exceeded the configured byte limit.")
        {
            ObservedLength = observedLength;
        }

        public long ObservedLength { get; }
    }

    /// <summary>
    /// Forwards reads up to a byte cap. <see cref="CanSeek"/> is false so a copy
    /// cannot bypass the cap by seeking. The caller owns the inner stream.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _delivered;
        private bool _exhausted;

        public BoundedReadStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadRemaining(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
            => ReadRemaining(buffer);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_exhausted)
            {
                return 0;
            }

            var remaining = _limit - _delivered;
            if (remaining <= 0)
            {
                var peek = new byte[1];
                var extra = await _inner.ReadAsync(peek, cancellationToken).ConfigureAwait(false);
                if (extra == 0)
                {
                    _exhausted = true;
                    return 0;
                }

                throw new ArtifactByteLimitException(_delivered + extra);
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _delivered += read;
            }
            else
            {
                _exhausted = true;
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns and disposes the inner stream.
            base.Dispose(disposing);
        }

        private int ReadRemaining(Span<byte> buffer)
        {
            if (_exhausted)
            {
                return 0;
            }

            var remaining = _limit - _delivered;
            if (remaining <= 0)
            {
                var peek = new byte[1];
                var extra = _inner.Read(peek, 0, 1);
                if (extra == 0)
                {
                    _exhausted = true;
                    return 0;
                }

                throw new ArtifactByteLimitException(_delivered + extra);
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = _inner.Read(buffer[..toRead]);
            if (read > 0)
            {
                _delivered += read;
            }
            else
            {
                _exhausted = true;
            }

            return read;
        }
    }
}
