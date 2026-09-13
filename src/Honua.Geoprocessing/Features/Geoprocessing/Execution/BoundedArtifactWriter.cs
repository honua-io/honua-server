// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Features;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

/// <summary>Streams GeoJSON into a byte-limited buffer without building a second JSON document.</summary>
internal static class BoundedArtifactWriter
{
    internal static byte[] WriteFeatureCollection(
        IReadOnlyList<IFeature> features,
        string processId,
        long maxBytes,
        CancellationToken cancellationToken,
        IEnumerable<(string Name, object Value)>? extraMembers = null)
    {
        using var buffer = new LimitedStream(maxBytes, cancellationToken);
        using (var text = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true))
        using (var json = new JsonTextWriter(text) { AutoCompleteOnClose = false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            json.WritePropertyName("type");
            json.WriteValue("FeatureCollection");
            json.WritePropertyName("processId");
            json.WriteValue(processId);
            json.WritePropertyName("featureCount");
            json.WriteValue(features.Count);
            if (extraMembers is not null)
            {
                foreach (var (name, value) in extraMembers)
                {
                    json.WritePropertyName(name);
                    json.WriteValue(value);
                }
            }

            json.WritePropertyName("features");
            json.WriteStartArray();
            var encoder = GeoJsonArtifactCodec.CreateWriter();
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                encoder.Write(feature, json);
                // Flush at feature boundaries as well as the fixed-size text buffer's
                // boundaries, so an oversized feature stops before touching the next row.
                json.Flush();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private sealed class LimitedStream(long maxBytes, CancellationToken cancellationToken) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => Length; set => throw new NotSupportedException(); }

        public byte[] ToArray() => _buffer.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length > maxBytes - Length)
            {
                throw new TransformInputException(
                    $"artifact size exceeds configured MaxArtifactBytes={maxBytes}; stopped during serialization. " +
                    "Narrow the selection, reduce carried attributes, or raise Geoprocessing:Executors:MaxArtifactBytes, then resubmit.");
            }

            _buffer.Write(buffer);
        }

        public override void Flush() => cancellationToken.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
