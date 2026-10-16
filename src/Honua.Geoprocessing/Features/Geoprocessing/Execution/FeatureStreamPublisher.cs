// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Keeps small feature collections inline and stages larger managed outputs in the
/// shared persistent output store. Buffer decisions use actual serialized UTF-8 bytes;
/// temporary spill files are deleted after staging or any unsuccessful publication.
/// </summary>
internal static class FeatureStreamPublisher
{
    internal readonly record struct PublishResult(string ArtifactReference, long OutputCount);

    public static async Task<PublishResult> PublishAsync(
        IAsyncEnumerable<IFeature> features,
        string processId,
        long inputCount,
        string outputRootDirectory,
        string operationId,
        CancellationToken cancellationToken,
        long inlineThresholdBytes = FeatureStreamArtifact.DefaultInlineThresholdBytes,
        IJobExecutionContext? executionContext = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(outputRootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(inlineThresholdBytes);
        inlineThresholdBytes = Math.Min(inlineThresholdBytes, FeatureStreamArtifact.DefaultInlineThresholdBytes);
        if (executionContext is ManagedFeatureStreamContext managed)
        {
            inlineThresholdBytes = Math.Min(inlineThresholdBytes, managed.Options.MaxInlineArtifactBytes);
        }

        var buffer = new List<(IFeature Feature, string Line)>();
        var writer = GeoJsonArtifactCodec.CreateWriter();
        long bufferedBytes = 0;
        long totalBytes = 0;
        long count = 0;
        FileStream? spill = null;
        string? spillPath = null;
        var retainLegacyTestFile = false;
        try
        {
            await foreach (var feature in features.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var line = FeatureStreamArtifact.SerializeLine(writer, feature);
                var lineBytes = Encoding.UTF8.GetByteCount(line);
                if (lineBytes > DurableFeatureStream.MaximumLineBytes)
                {
                    throw new TransformInputException("A serialized feature exceeds the 8 MiB row limit.");
                }

                totalBytes += lineBytes + 1L;
                if (totalBytes > DurableFeatureStream.MaximumBytes)
                {
                    throw new TransformInputException("The feature stream exceeds the 256 MiB output limit.");
                }

                count++;
                if (spill is null)
                {
                    buffer.Add((feature, line));
                    bufferedBytes += lineBytes + 1L;
                    if (bufferedBytes < inlineThresholdBytes)
                    {
                        continue;
                    }

                    if (executionContext is ManagedFeatureStreamContext { Store: null } or ManagedFeatureStreamContext { Options.Enabled: false })
                    {
                        throw new TransformInputException("Large managed outputs require shared persistent output staging.");
                    }

                    spillPath = FeatureStreamArtifact.AllocateSpillPath(outputRootDirectory, operationId, processId);
                    spill = new FileStream(spillPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    foreach (var buffered in buffer)
                    {
                        await FeatureStreamArtifact.WriteNdjsonLineAsync(spill, buffered.Line, cancellationToken).ConfigureAwait(false);
                    }

                    buffer.Clear();
                    buffer.TrimExcess();
                }
                else
                {
                    await FeatureStreamArtifact.WriteNdjsonLineAsync(spill, line, cancellationToken).ConfigureAwait(false);
                }
            }

            if (spill is not null)
            {
                await spill.FlushAsync(cancellationToken).ConfigureAwait(false);
                await spill.DisposeAsync().ConfigureAwait(false);
                spill = null;
                if (executionContext is ManagedFeatureStreamContext durable)
                {
                    var reference = await durable.StageAsync(spillPath!, count, totalBytes, cancellationToken).ConfigureAwait(false);
                    return new PublishResult(reference, count);
                }

                // Compatibility for isolated codec/executor tests. Every production
                // managed dispatch installs ManagedFeatureStreamContext, even without a store.
                retainLegacyTestFile = true;
                return new PublishResult(FeatureStreamArtifact.BuildStreamReference(spillPath!, count, totalBytes), count);
            }

            var extraMembers = inputCount >= 0 ? new[] { ("inputCount", (object)inputCount) } : null;
            var payload = FeatureCollectionArtifact.WriteFeatureCollection(buffer.Select(item => item.Feature).ToArray(), processId, extraMembers);
            return new PublishResult(FeatureCollectionArtifact.BuildDataUri(payload), count);
        }
        finally
        {
            if (spill is not null)
            {
                await spill.DisposeAsync().ConfigureAwait(false);
            }

            if (!retainLegacyTestFile && spillPath is not null)
            {
                File.Delete(spillPath);
            }
        }
    }
}
