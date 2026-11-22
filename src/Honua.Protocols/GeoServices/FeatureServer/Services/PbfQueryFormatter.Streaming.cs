// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed record PbfStreamSource
{
    public required IAsyncEnumerable<Feature> Features { get; init; }
    public required Func<bool> HasMoreResults { get; init; }
    public required MetadataV2Object Resource { get; init; }
    public required PbfStreamEncodingOptions Options { get; init; }
    internal Func<PbfFeatureSpool>? SpoolFactory { get; init; }
}

internal sealed partial class PbfQueryFormatter
{
    public (IResult response, string contentType) FormatAsPbfStream(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        PbfStreamEncodingOptions options)
        => FormatAsPbfStream(result, resource, options, static () => new PbfFeatureSpool());

    internal (IResult response, string contentType) FormatAsPbfStream(
        QueryResult<Feature> result,
        MetadataV2Object resource,
        PbfStreamEncodingOptions options,
        Func<PbfFeatureSpool> spoolFactory)
    {
        var headerOptions = CreateHeaderOptions(resource, options);
        var header = new ProtobufWriter(256);
        var spool = spoolFactory();
        try
        {
            var encoding = WriteFeatureHeader(ref header, result, headerOptions);
            // Encode each feature once into the bounded spool so result.Items is never retained
            // while a slow client drains and no full-body managed array is allocated.
            foreach (var feature in result.Items)
            {
                WriteEncodedFeatureToSpool(spool, feature, encoding);
            }

            // Flush and rewind before returning so a buffered scratch-full error surfaces as 503
            // before any 200 status or envelope bytes reach the response stream.
            spool.CompleteWriting();
            var featureResultLength = checked((int)(header.Position + spool.Length));
            var headerBytes = header.ToArrayAndDispose();
            return (new PbfStreamResult(new SpooledPbfPayload(headerBytes, spool, featureResultLength)),
                "application/x-protobuf");
        }
        catch
        {
            spool.Dispose();
            throw;
        }
        finally
        {
            header.Dispose();
        }
    }

    public async Task StreamAsPbfAsync(
        PbfStreamSource source,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var headerOptions = CreateHeaderOptions(source.Resource, source.Options);
        var encoding = BuildStreamingFeatureEncoding(headerOptions);
        using var spool = (source.SpoolFactory ?? (static () => new PbfFeatureSpool()))();
        await foreach (var feature in source.Features.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            WriteEncodedFeatureToSpool(spool, feature, encoding);
        }

        // Flush buffered scratch bytes before setting HTTP 200 headers so ENOSPC/EDQUOT
        // maps cleanly to HTTP 503 with zero bytes written to the response body.
        spool.CompleteWriting();
        var headerBytes = BuildStreamingHeaderBytes(new StreamingHeaderInput
        {
            Resource = headerOptions.Resource,
            Srid = headerOptions.Srid,
            RequestedOutputSrid = headerOptions.RequestedOutputSrid,
            HasMoreResults = source.HasMoreResults(),
            Encoding = encoding
        });
        var featureResultLength = checked((int)(headerBytes.Length + spool.Length));
        await WriteSpooledResponseAsync(
            context,
            new SpooledPbfPayload(headerBytes, spool, featureResultLength),
            cancellationToken).ConfigureAwait(false);
    }

    private FeatureHeaderOptions CreateHeaderOptions(MetadataV2Object resource, PbfStreamEncodingOptions options)
    {
        var srid = options.OutputSrid ?? resource.ReadSrid() ?? 4326;
        var limits = GeometryOutputProcessor.ForGeometrySrid(
            GeometryOutputProcessor.CreateEffectiveLimits(_geometryLimits, options.GeometryPrecision,
                options.MaxAllowableOffset, forceSimplify: options.MaxAllowableOffset is > 0),
            srid);
        return new FeatureHeaderOptions
        {
            Resource = resource,
            ObjectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource),
            Srid = srid,
            RequestedOutputSrid = options.RequestedOutputSrid,
            ReturnGeometry = options.ReturnGeometry,
            ReturnZ = options.ReturnZ,
            ReturnM = options.ReturnM,
            GeometryLimits = limits,
            OutFields = options.OutFields,
            QuantizationTransform = options.QuantizationTransform
        };
    }

    private static FeatureEncoding BuildStreamingFeatureEncoding(FeatureHeaderOptions options)
    {
        var queryFields = QueryFormatter.BuildQueryFields(
            options.Resource,
            options.OutFields,
            options.ObjectIdFieldName,
            runtimeFields: null);
        var fieldIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < queryFields.Length; i++)
        {
            fieldIndex[queryFields[i].Name] = i;
        }

        double scale = ComputeScale(options.GeometryLimits, options.QuantizationTransform);
        return new FeatureEncoding(queryFields, fieldIndex, options.ObjectIdFieldName, options.ReturnGeometry,
            ReturnZ: false, ReturnM: false, options.GeometryLimits, scale, options.QuantizationTransform);
    }

    private static byte[] BuildStreamingHeaderBytes(StreamingHeaderInput input)
    {
        var writer = new ProtobufWriter(256);
        try
        {
            var encoding = input.Encoding;
            writer.WriteString(1, encoding.ObjectId);
            var geometryType = input.Resource.ReadGeometryType();
            var hasGeometry = geometryType != MetadataV2GeometryType.None
                || input.Resource.FindPrimaryGeometryField() is not null;
            if (hasGeometry)
            {
                writer.WriteEnum(7, MapPbfGeometryType(geometryType));
                var sr = new ProtobufWriter(32);
                sr.WriteUInt32(1, (uint)(input.RequestedOutputSrid ?? input.Srid));
                sr.WriteUInt32(2, (uint)input.Srid);
                writer.WriteMessage(8, ref sr);
                sr.Dispose();
            }

            writer.WriteBool(9, input.HasMoreResults);
            writer.WriteBool(10, encoding.ReturnZ);
            writer.WriteBool(11, encoding.ReturnM);
            if (hasGeometry && encoding.ReturnGeometry)
                WriteTransform(ref writer, encoding.ReturnZ, encoding.ReturnM, encoding.Transform);

            foreach (var field in encoding.Fields)
            {
                var fieldMsg = new ProtobufWriter(64);
                fieldMsg.WriteString(1, field.Name);
                fieldMsg.WriteEnum(2, MapPbfFieldType(field.Type));
                fieldMsg.WriteString(3, field.Alias ?? field.Name);
                writer.WriteMessage(13, ref fieldMsg);
                fieldMsg.Dispose();
            }

            return writer.ToArrayAndDispose();
        }
        finally { writer.Dispose(); }
    }

    private sealed record StreamingHeaderInput
    {
        public required MetadataV2Object Resource { get; init; }
        public int Srid { get; init; }
        public int? RequestedOutputSrid { get; init; }
        public bool HasMoreResults { get; init; }
        public required FeatureEncoding Encoding { get; init; }
    }

    private static void WriteEncodedFeatureToSpool(PbfFeatureSpool spool, Feature feature, FeatureEncoding encoding)
    {
        var message = EncodeFeature(feature, encoding);
        try
        {
            if (message.Length > 0)
            {
                Span<byte> prefix = stackalloc byte[8];
                var prefixLength = WriteFeaturePrefix(prefix, message.Length);
                spool.Write(prefix[..prefixLength]);
                spool.Write(message.Buffer.AsSpan(0, message.Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message.Buffer);
        }
    }

    private static int WriteFeaturePrefix(Span<byte> destination, int length)
    {
        // Field 15, wire type 2 (length-delimited): (15 << 3) | 2 = 122.
        destination[0] = (15 << 3) | 2;
        var pos = 1;
        var value = (uint)length;
        while (value > 0x7F)
        {
            destination[pos++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        destination[pos++] = (byte)value;
        return pos;
    }

    private static async Task WriteSpooledResponseAsync(
        HttpContext context,
        SpooledPbfPayload payload,
        CancellationToken cancellationToken)
    {
        var prefix = CreateEnvelope(payload.FeatureResultLength);
        context.Response.ContentType = "application/x-protobuf";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = checked(prefix.Length + payload.FeatureResultLength);
        var output = context.Response.Body;
        await WriteChunksAsync(output, prefix, cancellationToken).ConfigureAwait(false);
        await WriteChunksAsync(output, payload.Header, cancellationToken).ConfigureAwait(false);
        await payload.Spool.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteChunksAsync(Stream output, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        {
            var length = Math.Min(PbfFeatureSpool.StreamChunkBytes, bytes.Length);
            await output.WriteAsync(bytes[..length], token).ConfigureAwait(false);
            bytes = bytes[length..];
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
        public int? RequestedOutputSrid { get; init; }
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

    private readonly record struct SpooledPbfPayload(byte[] Header, PbfFeatureSpool Spool, int FeatureResultLength);

    private sealed class PbfStreamResult(SpooledPbfPayload payload)
        : IResult, IDisposable
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                await WriteSpooledResponseAsync(context, payload, context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                payload.Spool.Dispose();
            }
        }

        public void Dispose() => payload.Spool.Dispose();
    }
}
