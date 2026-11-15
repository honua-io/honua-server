// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed partial class PbfQueryFormatter
{
    public (IResult response, string contentType) FormatAsPbfStream(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        PbfStreamEncodingOptions options)
    {
        var returnGeometry = options.ReturnGeometry;
        var outputSrid = options.OutputSrid;
        var returnZ = options.ReturnZ;
        var returnM = options.ReturnM;
        var geometryPrecision = options.GeometryPrecision;
        var maxAllowableOffset = options.MaxAllowableOffset;
        var outFields = options.OutFields;
        var quantizationTransform = options.QuantizationTransform;
        var limits = GeometryOutputProcessor.CreateEffectiveLimits(_geometryLimits, geometryPrecision,
            maxAllowableOffset, forceSimplify: maxAllowableOffset is > 0);
        var header = new ProtobufWriter(256);
        try
        {
            var encoding = WriteFeatureHeader(ref header, result, new FeatureHeaderOptions
            {
                Resource = resource,
                ObjectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource),
                Srid = outputSrid ?? resource.ReadSrid() ?? 4326,
                ReturnGeometry = returnGeometry,
                ReturnZ = returnZ,
                ReturnM = returnM,
                GeometryLimits = limits,
                OutFields = outFields,
                QuantizationTransform = quantizationTransform
            });
            var featureResultLength = header.Position;
            // Protobuf lengths precede nested messages. Validate and count each feature
            // before sending headers, retaining only one encoded feature at a time.
            foreach (var feature in result.Items)
            {
                var message = EncodeFeature(feature, encoding);
                try
                {
                    if (message.Length > 0)
                    {
                        featureResultLength = checked(featureResultLength + 1 + VarintLength(message.Length) + message.Length);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(message.Buffer);
                }
            }
            return (new PbfStreamResult(result.Items, encoding, header.ToArrayAndDispose(), featureResultLength),
                "application/x-protobuf");
        }
        finally
        {
            header.Dispose();
        }
    }

    private static (byte[] Buffer, int Length) EncodeFeature(Feature feature, FeatureEncoding encoding)
    {
        var writer = new ProtobufWriter(256);
        try
        {
            WriteFeature(ref writer, feature, encoding.Fields, encoding.FieldIndex, encoding.ObjectId,
                encoding.ReturnGeometry, encoding.ReturnZ, encoding.ReturnM, encoding.Limits,
                encoding.Scale, encoding.Transform);
            return writer.DetachBuffer();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static int VarintLength(int value)
    {
        var length = 1;
        while (value > 127)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    private sealed record FeatureHeaderOptions
    {
        public required MetadataV2Object Resource { get; init; }
        public required string ObjectIdFieldName { get; init; }
        public int Srid { get; init; }
        public bool ReturnGeometry { get; init; }
        public bool ReturnZ { get; init; }
        public bool ReturnM { get; init; }
        public required GeometryLimits GeometryLimits { get; init; }
        public string[]? OutFields { get; init; }
        public QuantizationTransform? QuantizationTransform { get; init; }
    }

    private sealed record FeatureEncoding(GeoServicesFieldInfo[] Fields, Dictionary<string, int> FieldIndex,
        string ObjectId, bool ReturnGeometry, bool ReturnZ, bool ReturnM, GeometryLimits Limits,
        double Scale, QuantizationTransform? Transform);

    private sealed class PbfStreamResult(ImmutableArray<Feature> features, FeatureEncoding encoding,
        byte[] header, int featureResultLength) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            var prefix = CreateEnvelope(featureResultLength);
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength = checked(prefix.Length + featureResultLength);
            var output = context.Response.Body;
            var cancellationToken = context.RequestAborted;
            await WriteChunksAsync(output, prefix, cancellationToken);
            await WriteChunksAsync(output, header, cancellationToken);
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = EncodeFeature(feature, encoding);
                try
                {
                    if (message.Length > 0)
                    {
                        await WriteChunksAsync(output, CreateFeaturePrefix(message.Length), cancellationToken);
                        await WriteChunksAsync(output, message.Buffer.AsMemory(0, message.Length), cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(message.Buffer);
                }
            }
        }

        private static byte[] CreateEnvelope(int length)
        {
            var writer = new ProtobufWriter(32);
            try
            {
                writer.WriteString(1, PbfVersion);
                writer.WriteTag(2, 2);
                writer.WriteRawVarint((uint)checked(1 + VarintLength(length) + length));
                writer.WriteTag(1, 2);
                writer.WriteRawVarint((uint)length);
                return writer.ToArrayAndDispose();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static byte[] CreateFeaturePrefix(int length)
        {
            var writer = new ProtobufWriter(8);
            try
            {
                writer.WriteTag(15, 2);
                writer.WriteRawVarint((uint)length);
                return writer.ToArrayAndDispose();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static async Task WriteChunksAsync(Stream output, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            const int chunkSize = 64 * 1024;
            while (!bytes.IsEmpty)
            {
                var length = Math.Min(chunkSize, bytes.Length);
                await output.WriteAsync(bytes[..length], token);
                bytes = bytes[length..];
            }
        }
    }
}
