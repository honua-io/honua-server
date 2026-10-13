// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class PbfQueryFormatterTests
{
    internal static byte[] GetFirstLengthDelimitedField(ReadOnlySpan<byte> message, int fieldNumber, int occurrence = 0)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            var currentField = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);

            switch (wireType)
            {
                case 0:
                    _ = ReadVarint(message, ref offset);
                    break;
                case 1:
                    offset += 8;
                    break;
                case 2:
                    {
                        var length = checked((int)ReadVarint(message, ref offset));
                        if (length < 0 || offset + length > message.Length)
                        {
                            throw new InvalidOperationException("Malformed protobuf length-delimited field.");
                        }

                        var payload = message.Slice(offset, length);
                        offset += length;
                        if (currentField == fieldNumber && occurrence-- == 0)
                        {
                            return payload.ToArray();
                        }

                        break;
                    }
                case 5:
                    offset += 4;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported protobuf wire type {wireType} in test parser.");
            }
        }

        throw new InvalidOperationException($"Field {fieldNumber} was not found.");
    }

    internal static ulong GetFirstVarintField(ReadOnlySpan<byte> message, int fieldNumber)
    {
        var offset = 0;
        while (offset < message.Length)
        {
            var tag = ReadVarint(message, ref offset);
            switch (tag & 7)
            {
                case 0:
                    var value = ReadVarint(message, ref offset);
                    if ((int)(tag >> 3) == fieldNumber)
                    {
                        return value;
                    }
                    break;
                case 1:
                    offset += 8;
                    break;
                case 2:
                    var length = checked((int)ReadVarint(message, ref offset));
                    offset += length;
                    break;
                case 5:
                    offset += 4;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected protobuf wire type.");
            }
        }
        throw new InvalidOperationException($"Varint field {fieldNumber} was not found.");
    }

    private static List<long> DecodePackedSInt64Values(ReadOnlySpan<byte> payload)
    {
        var values = new List<long>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var encoded = ReadVarint(payload, ref offset);
            var decoded = (long)((encoded >> 1) ^ (ulong)-(long)(encoded & 1));
            values.Add(decoded);
        }

        return values;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        var shift = 0;

        while (true)
        {
            if (offset >= data.Length)
            {
                throw new InvalidOperationException("Unexpected end of protobuf payload.");
            }

            var b = data[offset++];
            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException("Malformed protobuf varint.");
            }
        }
    }
}
