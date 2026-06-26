// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Materializes a transform's output feature stream into a DAG artifact, choosing the
/// inline base64 data URI for small results (back-compat) and spilling to an NDJSON file
/// for large ones (streaming, no hard cap). The decision is made by buffering only up to
/// <see cref="FeatureStreamArtifact.DefaultInlineThresholdBytes"/>: as soon as the buffered
/// payload crosses the threshold the publisher flips to spill mode, flushes the buffer to
/// the backing file, and writes the rest of the stream incrementally — so peak memory is
/// bounded by the inline threshold regardless of how many features flow through.
/// </summary>
internal static class FeatureStreamPublisher
{
    /// <summary>
    /// The outcome of publishing a feature stream: the artifact reference to publish plus
    /// the input/output feature counts for provenance.
    /// </summary>
    internal readonly record struct PublishResult(string ArtifactReference, long OutputCount);

    /// <summary>
    /// Consumes <paramref name="features"/> and produces a DAG artifact reference.
    /// </summary>
    /// <param name="features">The transform output stream (lazy).</param>
    /// <param name="processId">Process id stamped on inline FeatureCollection artifacts.</param>
    /// <param name="inputCount">Upstream feature count for inline provenance members.</param>
    /// <param name="outputRootDirectory">Root under which spill files are created.</param>
    /// <param name="operationId">Owning job id, used to name the spill file.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <param name="inlineThresholdBytes">
    /// Buffered-payload ceiling under which the output is published inline. At or above it
    /// the publisher spills. Defaults to <see cref="FeatureStreamArtifact.DefaultInlineThresholdBytes"/>.
    /// </param>
    public static async Task<PublishResult> PublishAsync(
        IAsyncEnumerable<IFeature> features,
        string processId,
        long inputCount,
        string outputRootDirectory,
        string operationId,
        CancellationToken cancellationToken,
        long inlineThresholdBytes = FeatureStreamArtifact.DefaultInlineThresholdBytes)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(outputRootDirectory);

        var buffer = new List<IFeature>();
        long approxBytes = 0;
        var spilled = false;
        string? spillPath = null;

        await using var spillWriter = new NdjsonSpillWriter();

        await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!spilled)
            {
                buffer.Add(feature);
                approxBytes += EstimateFeatureBytes(feature);

                if (approxBytes >= inlineThresholdBytes)
                {
                    // Crossed the inline ceiling: flip to spill mode, flush the buffered
                    // prefix to the backing file, and stream the remainder from here.
                    spilled = true;
                    spillPath = FeatureStreamArtifact.AllocateSpillPath(outputRootDirectory, operationId, processId);
                    await spillWriter.OpenAsync(spillPath, cancellationToken).ConfigureAwait(false);
                    foreach (var buffered in buffer)
                    {
                        await spillWriter.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
                    }

                    buffer.Clear();
                    buffer.TrimExcess();
                }

                continue;
            }

            await spillWriter.WriteAsync(feature, cancellationToken).ConfigureAwait(false);
        }

        if (spilled)
        {
            var (count, bytes) = await spillWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
            var reference = FeatureStreamArtifact.BuildStreamReference(spillPath!, count, bytes);
            return new PublishResult(reference, count);
        }

        // Small result: keep the legacy inline base64 FeatureCollection artifact so tiny
        // pipelines and OGC API Processes surfaces see exactly the same document as before.
        // Preserve the legacy inline artifact shape: stamp inputCount only when the caller
        // knew it (the buffered path). Streaming callers pass -1 (unknown without draining),
        // in which case the member is omitted rather than written as a sentinel.
        var extraMembers = inputCount >= 0
            ? new[] { ("inputCount", (object)inputCount) }
            : null;
        var payload = FeatureCollectionArtifact.WriteFeatureCollection(buffer, processId, extraMembers);
        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);
        return new PublishResult(artifactUri, buffer.Count);
    }

    private static long EstimateFeatureBytes(IFeature feature)
    {
        // Cheap, allocation-free upper-ish estimate so the publisher can decide spill
        // without serializing every feature twice. Geometry coordinate count dominates;
        // each coordinate is ~40 GeoJSON bytes, attributes add a flat per-feature constant.
        long estimate = 64;
        var geometry = feature.Geometry;
        if (geometry is not null)
        {
            estimate += (long)geometry.NumPoints * 40L;
        }

        var attributes = feature.Attributes;
        if (attributes is not null)
        {
            estimate += attributes.Count * 24L;
        }

        return estimate;
    }

    /// <summary>
    /// Incremental NDJSON writer used by the publisher once it flips to spill mode. Keeps a
    /// single feature in flight at a time; the underlying file stream flushes as its buffer
    /// fills. Mirrors <see cref="FeatureStreamArtifact.WriteStreamAsync"/> but lets the
    /// publisher feed features one at a time as it decides to spill.
    /// </summary>
    private sealed class NdjsonSpillWriter : IAsyncDisposable
    {
        private readonly NetTopologySuite.IO.GeoJsonWriter _writer = new();
        private FileStream? _fileStream;
        private StreamWriter? _textWriter;
        private long _count;
        private long _bytes;

        public async Task OpenAsync(string path, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _fileStream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            _textWriter = new StreamWriter(
                _fileStream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task WriteAsync(IFeature feature, CancellationToken cancellationToken)
        {
            var line = FeatureStreamArtifact.SerializeLine(_writer, feature);
            await _textWriter!.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            _count++;
            _bytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        }

        public async Task<(long Count, long Bytes)> CompleteAsync(CancellationToken cancellationToken)
        {
            if (_textWriter is not null)
            {
                await _textWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return (_count, _bytes);
        }

        public async ValueTask DisposeAsync()
        {
            if (_textWriter is not null)
            {
                await _textWriter.DisposeAsync().ConfigureAwait(false);
            }

            if (_fileStream is not null)
            {
                await _fileStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
