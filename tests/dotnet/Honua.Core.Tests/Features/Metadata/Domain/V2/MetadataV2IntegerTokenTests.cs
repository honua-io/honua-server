// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Serialization;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2IntegerTokenTests
{
    [UnitTheory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("-9223372036854775809")]
    public void IsInteger_IntegerToken_AcceptsIndependentOfRange(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read().Should().BeTrue();

        JsonIntegerToken.IsInteger(ref reader).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("987")]
    [InlineData("18446744073709551616")]
    [InlineData("-9223372036854775809")]
    public void IsInteger_SegmentedIntegerToken_AcceptsIndependentOfRange(string json)
    {
        var reader = ReadSegmentedToken(json);
        reader.HasValueSequence.Should().BeTrue();

        JsonIntegerToken.IsInteger(ref reader).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"future\"")]
    [InlineData("[]")]
    public void IsInteger_NonIntegerToken_RejectsValue(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read().Should().BeTrue();

        JsonIntegerToken.IsInteger(ref reader).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    public void IsInteger_SegmentedNonIntegerNumber_RejectsValue(string json)
    {
        var reader = ReadSegmentedToken(json);
        reader.HasValueSequence.Should().BeTrue();

        JsonIntegerToken.IsInteger(ref reader).Should().BeFalse();
    }

    private static Utf8JsonReader ReadSegmentedToken(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var first = new ByteSegment(bytes.AsMemory(0, 2));
        var last = first.Append(bytes.AsMemory(2));
        var reader = new Utf8JsonReader(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
        reader.Read();
        return reader;
    }

    private sealed class ByteSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public ByteSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new ByteSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
