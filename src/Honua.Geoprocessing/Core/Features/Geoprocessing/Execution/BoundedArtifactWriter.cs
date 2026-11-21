// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Features;
using Newtonsoft.Json;

namespace Honua.Geoprocessing.Execution;

internal static class BoundedArtifactWriter
{
    internal static byte[] WriteFeatureCollection(
        IReadOnlyList<IFeature> features,
        string processId,
        long maxBytes,
        CancellationToken cancellationToken,
        IEnumerable<(string Name, object Value)>? extraMembers = null,
        int srid = 0)
    {
        var extraList = extraMembers as IReadOnlyList<(string Name, object Value)> ?? extraMembers?.ToList();
        var resolvedSrid = ResolveOutputSrid(srid, extraList, features);

        using var buffer = new LimitedStream(maxBytes, cancellationToken);
        using (var text = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true))
        using (var json = new JsonTextWriter(text) { AutoCompleteOnClose = false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            json.WritePropertyName("type");
            json.WriteValue("FeatureCollection");
            if (resolvedSrid > 0)
            {
                json.WritePropertyName(FeatureArtifactSpatialReference.MemberName);
                json.WriteStartObject();
                json.WritePropertyName("schemaVersion");
                json.WriteValue(FeatureArtifactSpatialReference.SchemaVersion);
                json.WritePropertyName("state");
                json.WriteValue("known");
                json.WritePropertyName("srid");
                json.WriteValue(resolvedSrid);
                json.WriteEndObject();
            }

            json.WritePropertyName("processId");
            json.WriteValue(processId);
            json.WritePropertyName("featureCount");
            json.WriteValue(features.Count);
            if (extraList is not null)
            {
                foreach (var (name, value) in extraList)
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

    private static int ResolveOutputSrid(
        int explicitSrid,
        IReadOnlyList<(string Name, object Value)>? extraMembers,
        IReadOnlyList<IFeature> features)
    {
        if (explicitSrid > 0)
        {
            return explicitSrid;
        }

        if (extraMembers is not null)
        {
            foreach (var (name, value) in extraMembers)
            {
                if (!string.Equals(name, "srid", StringComparison.Ordinal))
                {
                    continue;
                }

                if (value is int intSrid && intSrid > 0)
                {
                    return intSrid;
                }

                if (value is long longSrid && longSrid is > 0 and <= int.MaxValue)
                {
                    return (int)longSrid;
                }
            }
        }

        var crs = new FeatureArtifactSpatialReferenceAccumulator();
        foreach (var feature in features)
        {
            crs.Add(feature.Geometry);
        }

        return crs.Value is { State: "known", Srid: > 0 } known ? known.Srid.Value : 0;
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
