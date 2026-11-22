// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Honua.Tests.ParquetUnitSupport;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using static Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services.FormatterTestResources;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class GeoParquetQueryFormatterTests
{
    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_MultiBatchPoints_MatchesMaterializedBytesAndMetadata()
    {
        var resource = CreateResource(Field("name", MetadataV2FieldType.String));
        var features = Enumerable.Range(1, 10)
            .Select(id => Feature.Create(
                id,
                CreatePointWkb(-122.4 + id * 0.01, 37.7 + id * 0.01),
                ImmutableDictionary<string, object?>.Empty.Add("name", $"parcel-{id}")))
            .ToImmutableArray();
        var limits = new GeoParquetLimits { MaxRowsPerBatch = 3 }.ForQuery();

        var (expected, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(features.Length, features),
            resource,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits(),
            encodingLimits: limits);

        var (stream, contentType) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            ToAsyncFeatures(features),
            resource,
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid",
                ReturnGeometry = true,
                OutputSrid = 4326,
                ReturnZ = false,
                ReturnM = false,
                GeometryLimits = new GeometryLimits(),
                EncodingLimits = limits
            });

        await using (stream)
        {
            contentType.Should().Be("application/vnd.apache.parquet");
            using var actualBuffer = new MemoryStream();
            await stream.CopyToAsync(actualBuffer);
            actualBuffer.ToArray().Should().Equal(expected);
        }
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_EmptyStream_MatchesMaterializedEmptyParquet()
    {
        var resource = CreateResource(Field("name", MetadataV2FieldType.String));
        var limits = new GeoParquetLimits().ForQuery();
        var (expected, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(0, []),
            resource,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            geometryLimits: new GeometryLimits(),
            encodingLimits: limits);

        var (stream, contentType) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            ToAsyncFeatures([]),
            resource,
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid",
                ReturnGeometry = true,
                OutputSrid = 4326,
                GeometryLimits = new GeometryLimits(),
                EncodingLimits = limits
            });

        await using (stream)
        {
            contentType.Should().Be("application/vnd.apache.parquet");
            using var actualBuffer = new MemoryStream();
            await stream.CopyToAsync(actualBuffer);
            actualBuffer.ToArray().Should().Equal(expected);
        }
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_EmptyGeometryInLaterBatch_RewritesWithoutCovering()
    {
        var resource = CreateResource(Field("name", MetadataV2FieldType.String));
        var emptyPointWkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false)
            .Write(Point.Empty);
        var features = ImmutableArray.Create(
            Feature.Create(1, CreatePointWkb(-122.4, 37.7), ImmutableDictionary<string, object?>.Empty.Add("name", "a")),
            Feature.Create(2, CreatePointWkb(-122.3, 37.8), ImmutableDictionary<string, object?>.Empty.Add("name", "b")),
            Feature.Create(3, emptyPointWkb, ImmutableDictionary<string, object?>.Empty.Add("name", "empty")));
        var limits = new GeoParquetLimits { MaxRowsPerBatch = 2 }.ForQuery();

        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            ToAsyncFeatures(features),
            resource,
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid",
                ReturnGeometry = true,
                OutputSrid = 4326,
                GeometryLimits = new GeometryLimits(),
                EncodingLimits = limits
            });

        await using (stream)
        using (var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream))
        {
            reader.Schema.FieldsList.Select(f => f.Name).Should().NotContain("bbox");
            using var geo = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
            geo.RootElement.GetProperty("columns").GetProperty("geometry").TryGetProperty("covering", out _).Should().BeFalse();

            var ids = new List<long>();
            using var batches = reader.GetRecordBatchReader();
            while (await batches.ReadNextRecordBatchAsync() is { } batch)
            {
                using (batch)
                {
                    var objectIds = (Int64Array)batch.Column("objectid");
                    for (var i = 0; i < batch.Length; i++)
                    {
                        ids.Add(objectIds.GetValue(i)!.Value);
                    }
                }
            }

            ids.Should().Equal(1, 2, 3);
        }
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_ExceedsMaxResponseBytes_ThrowsLimitExceededAndDeletesSpool()
    {
        var resource = CreateResource(Field("name", MetadataV2FieldType.String));
        var features = ImmutableArray.Create(
            Feature.Create(1, CreatePointWkb(-122.4, 37.7), ImmutableDictionary<string, object?>.Empty.Add("name", "capped")));
        var limits = new GeoParquetLimits { MaxResponseBytes = 32 }.ForQuery();

        var act = () => GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            ToAsyncFeatures(features),
            resource,
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid",
                ReturnGeometry = true,
                OutputSrid = 4326,
                GeometryLimits = new GeometryLimits(),
                EncodingLimits = limits
            });

        await act.Should().ThrowAsync<GeoParquetLimitExceededException>();
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStream_DoesNotRetainMaterializedFeaturesWhileWaitingToDrain()
    {
        var resource = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String));
        var (payload, weakFeature) = CreateParquetStreamWithoutKeepingFeatures(resource);

        await using (payload)
        {
            for (var i = 0; i < 3 && weakFeature.IsAlive; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            weakFeature.IsAlive.Should().BeFalse(
                "the spooled GeoParquet stream must not pin result.Items across slow readers");
            payload.Length.Should().BeGreaterThan(0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Stream Payload, WeakReference WeakFeature) CreateParquetStreamWithoutKeepingFeatures(
        MetadataV2Object resource)
    {
        var feature = Feature.Create(99, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('q', 200_000)));
        var weak = new WeakReference(feature);
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquetStream(query, resource, new GeoParquetStreamEncodingOptions
        {
            ReturnGeometry = false,
            GeometryLimits = new GeometryLimits(),
            EncodingLimits = new GeoParquetLimits().ForQuery()
        });
        return (payload, weak);
    }

    private static async IAsyncEnumerable<Feature> ToAsyncFeatures(
        ImmutableArray<Feature> features,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
