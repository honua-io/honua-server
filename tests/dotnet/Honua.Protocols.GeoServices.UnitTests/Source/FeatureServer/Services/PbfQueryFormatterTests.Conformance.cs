// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class PbfQueryFormatterTests
{
    [Fact]
    public void FormatAsPbf_BlobAttribute_EncodesBytesValueField8()
    {
        var layer = CreateLayer(
            [new MetadataV2Field { Name = "payload", Type = MetadataV2FieldType.Binary }],
            MetadataV2GeometryType.Point);
        var expectedBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 };
        var feature = Feature.Create(
            1,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("payload", expectedBytes));

        var (payload, contentType) = _sut.FormatAsPbf(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: ["payload"]);

        contentType.Should().Be("application/x-protobuf");
        var result = GetFirstLengthDelimitedField(GetFirstLengthDelimitedField(payload, 2), 1);
        var outputFeature = GetFirstLengthDelimitedField(result, 15);
        var attribute = GetFirstLengthDelimitedField(outputFeature, 1, 0);

        GetFirstLengthDelimitedField(attribute, 8).Should().Equal(expectedBytes);
        FindFirstLengthDelimitedField(attribute, 1).Should().BeNull("blobs must not fall back to string_value");
    }

    [Fact]
    public void FormatAsPbf_EmptyBlobAttribute_EmitsExplicitEmptyBytesValueField8()
    {
        var layer = CreateLayer(
            [new MetadataV2Field { Name = "payload", Type = MetadataV2FieldType.Binary }],
            MetadataV2GeometryType.Point);
        var feature = Feature.Create(
            1,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("payload", Array.Empty<byte>()));

        var (payload, _) = _sut.FormatAsPbf(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: ["payload"]);

        var result = GetFirstLengthDelimitedField(GetFirstLengthDelimitedField(payload, 2), 1);
        var outputFeature = GetFirstLengthDelimitedField(result, 15);
        var attribute = GetFirstLengthDelimitedField(outputFeature, 1, 0);

        var rawBytes = FindFirstLengthDelimitedField(attribute, 8);
        rawBytes.Should().NotBeNull();
        rawBytes.Should().BeEmpty();
        TryGetVarintField(attribute, 10, out _).Should().BeFalse("empty byte[] is not null");
    }

    [Fact]
    public void FormatAsPbf_RequestedOutputSrid_EchoesWkidAndLatestWkid()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, CreatePointWkb(0, 0), ImmutableDictionary<string, object?>.Empty);

        var (payload, contentType) = _sut.FormatAsPbf(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            requestedOutputSrid: 102100);

        contentType.Should().Be("application/x-protobuf");
        var result = GetFirstLengthDelimitedField(GetFirstLengthDelimitedField(payload, 2), 1);
        var spatialReference = GetFirstLengthDelimitedField(result, 8);
        GetFirstVarintField(spatialReference, 1).Should().Be(102100UL);
        GetFirstVarintField(spatialReference, 2).Should().Be(3857UL);
    }

    [Fact]
    public async Task FormatAsPbfStream_RequestedOutputSrid_EchoesWkidAndLatestWkid()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, CreatePointWkb(0, 0), ImmutableDictionary<string, object?>.Empty);

        var (streamResult, contentType) = _sut.FormatAsPbfStream(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            new PbfStreamEncodingOptions
            {
                ReturnGeometry = true,
                OutputSrid = 3857,
                RequestedOutputSrid = 102100
            });

        contentType.Should().Be("application/x-protobuf");
        var httpContext = new DefaultHttpContext();
        await using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await streamResult.ExecuteAsync(httpContext);

        var payload = body.ToArray();
        var result = GetFirstLengthDelimitedField(GetFirstLengthDelimitedField(payload, 2), 1);
        var spatialReference = GetFirstLengthDelimitedField(result, 8);
        GetFirstVarintField(spatialReference, 1).Should().Be(102100UL);
        GetFirstVarintField(spatialReference, 2).Should().Be(3857UL);
    }

    [Fact]
    public void FormatEmptyExtentAsPbf_ZeroCount_EmitsExtentCountResultWithoutEnvelope()
    {
        var (payload, contentType) = PbfQueryFormatter.FormatEmptyExtentAsPbf(0);

        contentType.Should().Be("application/x-protobuf");
        var queryResult = GetFirstLengthDelimitedField(payload, 2);
        var extentCountResult = GetFirstLengthDelimitedField(queryResult, 4);
        FindFirstLengthDelimitedField(extentCountResult, 1).Should().BeNull();
        GetFirstVarintField(extentCountResult, 2).Should().Be(0UL);
    }

    private static byte[]? FindFirstLengthDelimitedField(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            switch (tag & 7)
            {
                case 0:
                    _ = ReadVarint(message, ref offset);
                    break;
                case 1:
                    offset += 8;
                    break;
                case 2:
                    var length = checked((int)ReadVarint(message, ref offset));
                    if ((int)(tag >> 3) == fieldNumber)
                    {
                        return message.Slice(offset, length).ToArray();
                    }

                    offset += length;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected protobuf wire type.");
            }
        }

        return null;
    }
}
