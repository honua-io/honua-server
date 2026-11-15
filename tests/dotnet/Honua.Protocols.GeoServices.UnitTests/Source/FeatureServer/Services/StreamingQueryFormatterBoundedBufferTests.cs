// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class StreamingQueryFormatterBoundedBufferTests
{
    private const int FeatureCount = 100_000;
    private const int MaxFeaturesAheadOfReader = 1_024;
    private const int MaxBufferedBytes = 64 * 1_024;

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamAsGeoServicesJsonAsync_HundredThousandFeatures_WritesWhileReading()
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16 * 1_024,
            resumeWriterThreshold: 8 * 1_024,
            useSynchronizationContext: false));
        var body = new IncrementalFeatureReader();
        var source = new BodyWatchingFeatureSource(body);
        var reading = body.ReadAsync(pipe.Reader);

        var writing = WriteResponseAsync(pipe.Writer, () => CreateFormatter().StreamAsGeoServicesJsonAsync(
            source.Features(FeatureCount),
            CreateResource(),
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe.Writer));
        await Task.WhenAll(writing, reading);

        body.FeatureCount.Should().Be(FeatureCount);
        body.ObjectIdCount.Should().Be(FeatureCount);
        body.PeakBufferedBytes.Should().BeLessThan(MaxBufferedBytes);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamAsGeoJsonAsync_HundredThousandFeatures_WritesWhileReading()
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16 * 1_024,
            resumeWriterThreshold: 8 * 1_024,
            useSynchronizationContext: false));
        var body = new IncrementalFeatureReader();
        var source = new BodyWatchingFeatureSource(body);
        var reading = body.ReadAsync(pipe.Reader);

        var writing = WriteResponseAsync(pipe.Writer, () => CreateFormatter().StreamAsGeoJsonAsync(
            source.Features(FeatureCount),
            CreateResource(),
            returnGeometry: false,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe.Writer));
        await Task.WhenAll(writing, reading);

        body.FeatureCount.Should().Be(FeatureCount);
        body.ObjectIdCount.Should().Be(FeatureCount);
        body.PeakBufferedBytes.Should().BeLessThan(MaxBufferedBytes);
    }

    private static StreamingQueryFormatter CreateFormatter()
        => new(Options.Create(new LimitsOptions()));

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "stream-layer", Name = "stream-layer" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

    private static async Task WriteResponseAsync(PipeWriter writer, Func<Task> write)
    {
        Exception? error = null;
        try
        {
            await write();
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            await writer.CompleteAsync(error);
        }
    }

    private sealed class IncrementalFeatureReader
    {
        private JsonReaderState _state;
        private bool _readingObjectId;
        private bool _readingFeatures;
        private bool _inFeatures;
        private int _featureCount;

        public int FeatureCount => Volatile.Read(ref _featureCount);
        public int ObjectIdCount { get; private set; }
        public long PeakBufferedBytes { get; private set; }

        public async Task ReadAsync(PipeReader reader)
        {
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync();
                    PeakBufferedBytes = Math.Max(PeakBufferedBytes, result.Buffer.Length);
                    var consumed = Parse(result.Buffer, result.IsCompleted);
                    reader.AdvanceTo(consumed, result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            finally
            {
                await reader.CompleteAsync();
            }
        }

        private SequencePosition Parse(ReadOnlySequence<byte> buffer, bool final)
        {
            var json = new Utf8JsonReader(buffer, final, _state);
            while (json.Read())
            {
                if (_readingFeatures && json.TokenType == JsonTokenType.StartArray)
                {
                    _inFeatures = true;
                }
                else if (json.TokenType == JsonTokenType.EndArray && json.CurrentDepth == 1)
                {
                    _inFeatures = false;
                }

                if (_inFeatures && json.TokenType == JsonTokenType.EndObject && json.CurrentDepth == 2)
                {
                    Interlocked.Increment(ref _featureCount);
                }

                if (_readingObjectId && json.TokenType == JsonTokenType.Number)
                {
                    json.GetInt64().Should().Be(++ObjectIdCount);
                }

                _readingObjectId = _inFeatures && json.TokenType == JsonTokenType.PropertyName && json.ValueTextEquals("objectid");
                _readingFeatures = json.TokenType == JsonTokenType.PropertyName && json.ValueTextEquals("features");
            }

            _state = json.CurrentState;
            return buffer.GetPosition(json.BytesConsumed);
        }
    }

    private sealed class BodyWatchingFeatureSource(IncrementalFeatureReader body)
    {
        public async IAsyncEnumerable<Feature> Features(int count)
        {
            for (var id = 1L; id <= count; id++)
            {
                (id - body.FeatureCount).Should().BeLessThan(MaxFeaturesAheadOfReader,
                    "the producer must wait for the bounded response reader");
                yield return Feature.Create(
                    id,
                    geometry: null,
                    ImmutableDictionary<string, object?>.Empty.Add("objectid", id).Add("name", "feature"));
                if (id % 256 == 0)
                {
                    await Task.Yield();
                }
            }
        }
    }
}
