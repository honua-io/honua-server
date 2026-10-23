// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing.Execution;

internal sealed record GeoprocessingImportContentSummary(
    long Bytes,
    string Sha256,
    long FeatureCount,
    FeatureArtifactSpatialReference SpatialReference);

/// <summary>
/// Writes a bounded GeoJSON import source from an already authorized job output.
/// It never fetches a URI and never opens a caller-supplied path.
/// </summary>
internal static class GeoprocessingImportArtifactWriter
{
    private static readonly byte[] CollectionPrefix = "{\"type\":\"FeatureCollection\",\"features\":["u8.ToArray();

    internal static async Task<GeoprocessingImportContentSummary> WriteAsync(
        string reference,
        ExecutionJobRecord authorizedJob,
        Stream destination,
        IGeoprocessingFeatureStreamReader? featureStreams,
        long maximumBytes,
        long maximumInlineBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(authorizedJob);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInlineBytes);
        if (authorizedJob.Status != ExecutionJobStatus.Succeeded
            || authorizedJob.Spec.Kind != ExecutionJobKind.Geoprocessing
            || !authorizedJob.ArtifactReferences.Contains(reference, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The artifact does not belong to the authorized completed job.");
        }

        using var output = new BoundedOutput(destination, Math.Min(maximumBytes, DurableFeatureStream.MaximumBytes));
        if (reference.StartsWith(FeatureCollectionArtifact.DataUriPrefix, StringComparison.Ordinal))
        {
            var encoded = reference.AsMemory(FeatureCollectionArtifact.DataUriPrefix.Length);
            var limit = Math.Min(maximumInlineBytes, output.MaximumBytes);
            if (encoded.Length > limit / 3 * 4 + 4)
            {
                throw new InvalidDataException("The inline artifact exceeds the configured byte limit.");
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded.ToString());
            }
            catch (FormatException)
            {
                throw new InvalidDataException("The inline artifact encoding is invalid.");
            }

            if (payload.Length == 0 || payload.LongLength > limit)
            {
                throw new InvalidDataException("The inline artifact exceeds the configured byte limit.");
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count(property => property.NameEquals("type")) != 1
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("A canonical GeoJSON feature artifact is required.");
            }

            var crs = FeatureArtifactSpatialReference.Read(root) ?? new("unknown", null);
            if (type.GetString() == "FeatureCollection")
            {
                if (root.EnumerateObject().Count(property => property.NameEquals("features")) != 1
                    || !root.TryGetProperty("features", out var features)
                    || features.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("A canonical feature collection is required.");
                }

                await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                return output.Complete(features.GetArrayLength(), crs);
            }

            if (type.GetString() != "Feature")
            {
                throw new InvalidDataException("The job artifact is not a supported feature source.");
            }

            await output.WriteAsync(CollectionPrefix, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await WriteTrailerAsync(output, crs, cancellationToken).ConfigureAwait(false);
            return output.Complete(1, crs);
        }

        if (!DurableFeatureStream.TryParse(reference, out _) || featureStreams is null)
        {
            throw new InvalidDataException("The job artifact is not an available verified shared feature stream.");
        }

        var writer = GeoJsonArtifactCodec.CreateWriter();
        var summary = new FeatureArtifactSpatialReferenceAccumulator();
        long count = 0;
        await output.WriteAsync(CollectionPrefix, cancellationToken).ConfigureAwait(false);
        await foreach (var row in featureStreams.ReadRowsAsync(
            reference, authorizedJob, Math.Min(maximumBytes, DurableFeatureStream.MaximumBytes), cancellationToken)
            .ConfigureAwait(false))
        {
            summary.Add(row.Feature.Geometry, row.HasProducerSpatialReference);
            var json = writer.Write(row.Feature);
            if (Encoding.UTF8.GetByteCount(json) > DurableFeatureStream.MaximumLineBytes)
            {
                throw new InvalidDataException("The feature exceeds the bounded row limit.");
            }

            if (count > 0)
            {
                await output.WriteAsync(","u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            await output.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
            count++;
        }

        await WriteTrailerAsync(output, summary.Value, cancellationToken).ConfigureAwait(false);
        return output.Complete(count, summary.Value);
    }

    private static async Task WriteTrailerAsync(
        BoundedOutput output,
        FeatureArtifactSpatialReference crs,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            crs.Write(writer);
            writer.WriteEndObject();
        }

        var metadata = buffer.ToArray();
        await output.WriteAsync("],"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(metadata.AsMemory(1, metadata.Length - 2), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private sealed class BoundedOutput(Stream destination, long maximumBytes) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;

        internal long MaximumBytes => maximumBytes;

        internal async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (bytes.Length > 0 && _bytes > maximumBytes - bytes.Length)
            {
                throw new InvalidDataException("The import source exceeds the configured byte limit.");
            }

            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            _hash.AppendData(bytes.Span);
            _bytes += bytes.Length;
        }

        internal GeoprocessingImportContentSummary Complete(long count, FeatureArtifactSpatialReference crs)
            => new(_bytes, Convert.ToHexStringLower(_hash.GetHashAndReset()), count, crs);

        public void Dispose() => _hash.Dispose();
    }
}
