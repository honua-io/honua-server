// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

// Regression guard: the streaming query formatters must push bytes to the response pipe as they read
// features. If the pipe is never flushed, the whole body accumulates in pipe memory before the first byte.
public sealed class StreamingQueryFormatterBoundedBufferTests
{
    private const int FeatureCount = 100_000;
    private const int MaxFeaturesBetweenWrites = 1_024;

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamAsGeoServicesJsonAsync_HundredThousandFeatures_WritesWhileReading()
    {
        using var body = new MemoryStream();
        var pipe = PipeWriter.Create(body);
        var source = new BodyWatchingFeatureSource(body);

        await CreateFormatter().StreamAsGeoServicesJsonAsync(
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
            outputStream: pipe);
        await pipe.CompleteAsync();

        source.LongestRunWithoutBodyWrite.Should().BeLessThan(MaxFeaturesBetweenWrites);
        CountFeatures(body).Should().Be(FeatureCount);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task StreamAsGeoJsonAsync_HundredThousandFeatures_WritesWhileReading()
    {
        using var body = new MemoryStream();
        var pipe = PipeWriter.Create(body);
        var source = new BodyWatchingFeatureSource(body);

        await CreateFormatter().StreamAsGeoJsonAsync(
            source.Features(FeatureCount),
            CreateResource(),
            returnGeometry: false,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: null,
            hasMoreResults: false,
            outputStream: pipe);
        await pipe.CompleteAsync();

        source.LongestRunWithoutBodyWrite.Should().BeLessThan(MaxFeaturesBetweenWrites);
        CountFeatures(body).Should().Be(FeatureCount);
    }

    private static StreamingQueryFormatter CreateFormatter()
        => new(Options.Create(new LimitsOptions()));

    private static MetadataV2Object CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "stream-layer", Name = "stream-layer" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

    private static int CountFeatures(MemoryStream body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        return document.RootElement.GetProperty("features").GetArrayLength();
    }

    private sealed class BodyWatchingFeatureSource(MemoryStream body)
    {
        private const int YieldEvery = 4_096;

        public int LongestRunWithoutBodyWrite { get; private set; }

        public async IAsyncEnumerable<Feature> Features(int count)
        {
            var lastLength = body.Length;
            var run = 0;
            for (var id = 1L; id <= count; id++)
            {
                run = body.Length == lastLength ? run + 1 : 0;
                lastLength = body.Length;
                LongestRunWithoutBodyWrite = Math.Max(LongestRunWithoutBodyWrite, run);
                if (id % YieldEvery == 0)
                {
                    await Task.Yield();
                }

                yield return Feature.Create(
                    id,
                    geometry: null,
                    ImmutableDictionary<string, object?>.Empty.Add("objectid", id).Add("name", "feature"));
            }
        }
    }
}
