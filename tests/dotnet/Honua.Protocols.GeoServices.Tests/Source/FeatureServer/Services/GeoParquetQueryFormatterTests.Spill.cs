// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.Tests.ParquetUnitSupport;
using static Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services.FormatterTestResources;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class GeoParquetQueryFormatterTests
{
    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_ExceedsMemoryBudgetAndMatchesMaterializedOrder()
    {
        var features = Enumerable.Range(1, 29).Select(id => Feature.Create(
            id, CreatePointWkb(id % 3, id % 4), ImmutableDictionary<string, object?>.Empty
                .Add("label", $"parcel-{id}").Add("surveyed", new DateOnly(2026, 1, id))))
            .Append(Feature.Create(30, null, ImmutableDictionary<string, object?>.Empty))
            .ToImmutableArray();
        var resource = CreateResource();
        var limits = new GeoParquetLimits
        {
            OrderByBboxHilbert = true,
            MaxRowsPerBatch = 2,
            MaxEstimatedBatchBytes = 2048,
            MaxEstimatedInputBytes = 2048,
        };
        var options = new GeoParquetStreamEncodingOptions
        {
            ObjectIdFieldName = "objectid", ReturnGeometry = true, OutputSrid = 4326,
            EncodingLimits = limits,
        };
        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(
            ToAsyncFeatures(features), resource, options);
        await using (stream)
        {
            var (expected, _) = GeoParquetFeatureWriter.FormatAsGeoParquetStream(
                QueryResult<Feature>.Create(features.Length, features), resource,
                options with { EncodingLimits = new GeoParquetLimits { OrderByBboxHilbert = true, MaxRowsPerBatch = 2 } });
            await using (expected)
            {
                (await ReadSpillIdsAsync(stream)).Should().Equal(await ReadSpillIdsAsync(expected));
            }
            await AssertSpillRowsAsync(stream, features.Length);
        }
    }

    private static async Task AssertSpillRowsAsync(Stream stream, int expectedRows)
    {
        stream.Position = 0;
        using var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream);
        reader.Schema.Metadata[GeoParquetFeatureWriter.RowOrderMetadataKey]
            .Should().Be(GeoParquetFeatureWriter.HilbertBboxCenterOrder);
        using var batches = reader.GetRecordBatchReader();
        var rows = 0;
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var ids = (Int64Array)batch.Column("objectid");
                var labels = (StringArray)batch.Column("label");
                var dates = (Date32Array)batch.Column("surveyed");
                for (var index = 0; index < batch.Length; index++)
                {
                    var id = ids.GetValue(index)!.Value;
                    if (id < 30)
                    {
                        labels.GetString(index).Should().Be($"parcel-{id}");
                        dates.GetDateOnly(index).Should().Be(new DateOnly(2026, 1, (int)id));
                    }
                    rows++;
                }
            }
        }
        rows.Should().Be(expectedRows);
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_PreservesCaseInsensitiveAttributeLookup()
    {
        var attributes = ImmutableDictionary.Create<string, object?>(StringComparer.OrdinalIgnoreCase)
            .Add("label", "parcel");
        var feature = Feature.Create(1, CreatePointWkb(1, 1), attributes);
        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(ToAsyncFeatures([feature]),
            CreateResource(Field("LABEL", MetadataV2FieldType.String)),
            new GeoParquetStreamEncodingOptions
            {
                ReturnGeometry = true, OutputSrid = 4326,
                EncodingLimits = new GeoParquetLimits { OrderByBboxHilbert = true },
            });
        await using (stream)
        {
            using var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream);
            using var batches = reader.GetRecordBatchReader();
            using var batch = await batches.ReadNextRecordBatchAsync();
            ((StringArray)batch!.Column("LABEL")).GetString(0).Should().Be("parcel");
        }
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_CollectsLateSchemaBeforeWriting()
    {
        var empty = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false)
            .Write(Point.Empty);
        var features = Enumerable.Range(1, 128).Select(id => Feature.Create(id,
            CreatePointWkb(id % 7, id % 11), ImmutableDictionary<string, object?>.Empty.Add("label", new string('x', 128))))
            .Append(Feature.Create(129, empty, ImmutableDictionary<string, object?>.Empty.Add("late", "present")))
            .ToImmutableArray();
        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(ToAsyncFeatures(features),
            CreateResource(), new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid", ReturnGeometry = true, OutputSrid = 4326,
                EncodingLimits = new GeoParquetLimits
                {
                    OrderByBboxHilbert = true, MaxRowsPerBatch = 2,
                    MaxEstimatedBatchBytes = 2048, MaxEstimatedInputBytes = 2048,
                },
            });
        await using (stream)
        {
            using (var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream))
            {
                reader.Schema.FieldsList.Select(field => field.Name).Should().Contain("late").And.NotContain("bbox");
            }
            stream.Position = 0;
            (await ReadSpillIdsAsync(stream)).Should().HaveCount(129);
        }
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_AcceptsCharacterAndIgnoredOpaqueValues()
    {
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("label", 'x')
            .Add("ignored", new object()).Add("__internal", new object());
        var feature = Feature.Create(1, CreatePointWkb(1, 1), attributes);
        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(ToAsyncFeatures([feature]),
            CreateResource(Field("label", MetadataV2FieldType.String)), new GeoParquetStreamEncodingOptions
            {
                ReturnGeometry = true, OutputSrid = 4326, OutFields = ["label"],
                EncodingLimits = new GeoParquetLimits { OrderByBboxHilbert = true },
            });
        await using (stream)
        {
            using var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream);
            using var batches = reader.GetRecordBatchReader();
            using var batch = await batches.ReadNextRecordBatchAsync();
            ((StringArray)batch!.Column("label")).GetString(0).Should().Be("x");
        }
    }

    public enum SpillFailure { Source, Cancellation }

    [UnitTheory]
    [InlineData(SpillFailure.Source)]
    [InlineData(SpillFailure.Cancellation)]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_CancelOrSourceFailureDeletesScratch(SpillFailure failure)
    {
        using var cancellation = new CancellationTokenSource();
        var paths = new List<string>();
        var before = Directory.GetFiles(Path.GetTempPath(), "honua-hilbert-*").ToHashSet();
        async IAsyncEnumerable<Feature> Source()
        {
            paths.AddRange(Directory.GetFiles(Path.GetTempPath(), "honua-hilbert-*").Where(p => !before.Contains(p)));
            yield return Feature.Create(1, CreatePointWkb(1, 1), ImmutableDictionary<string, object?>.Empty);
            await Task.Yield();
            if (failure == SpillFailure.Cancellation)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("source failed");
        }
        var act = () => GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(Source(), CreateResource(),
            new GeoParquetStreamEncodingOptions
            {
                EncodingLimits = new GeoParquetLimits { OrderByBboxHilbert = true },
                ReturnGeometry = true, OutputSrid = 4326,
            }, cancellation.Token);
        if (failure == SpillFailure.Cancellation)
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("source failed");
        }
        paths.Should().HaveCount(3);
        paths.Should().OnlyContain(path => !File.Exists(path));
    }

    [UnitTheory]
    [InlineData(1, 1048576)]
    [InlineData(100, 1048576)]
    [InlineData(1048576, 32)]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_EnforcesScratchAndOutputCaps(long scratchBytes, int outputBytes)
    {
        var feature = Feature.Create(1, CreatePointWkb(1, 1), ImmutableDictionary<string, object?>.Empty);
        var act = () => GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(ToAsyncFeatures([feature]), CreateResource(),
            new GeoParquetStreamEncodingOptions
            {
                ReturnGeometry = true, OutputSrid = 4326,
                EncodingLimits = new GeoParquetLimits
                {
                    OrderByBboxHilbert = true, MaxScratchBytes = scratchBytes, MaxResponseBytes = outputBytes,
                },
            });
        await act.Should().ThrowAsync<GeoParquetLimitExceededException>();
    }

    [UnitTest]
    public async Task FormatAsGeoParquetStreamAsync_HilbertSpill_ReleasesSourceFeaturesBeforeEnumerationCompletes()
    {
        var weakFeatures = new List<WeakReference>();
        async IAsyncEnumerable<Feature> Source()
        {
            for (var id = 0; id < 64; id++)
            {
                if (id == 32)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    weakFeatures.Take(16).Should().OnlyContain(weak => !weak.IsAlive,
                        "spilling must release earlier source rows before the source finishes");
                }
                var feature = Feature.Create(id, CreatePointWkb(id % 7, id % 11),
                    ImmutableDictionary<string, object?>.Empty.Add("label", new string('x', 2000)));
                weakFeatures.Add(new WeakReference(feature.Attributes));
                yield return feature;
                await Task.Yield();
            }
        }
        var (stream, _) = await GeoParquetFeatureWriter.FormatAsGeoParquetStreamAsync(Source(), CreateResource(),
            new GeoParquetStreamEncodingOptions
            {
                ObjectIdFieldName = "objectid", ReturnGeometry = true, OutputSrid = 4326,
                EncodingLimits = new GeoParquetLimits
                {
                    OrderByBboxHilbert = true, MaxRowsPerBatch = 4,
                    MaxEstimatedBatchBytes = 16384, MaxEstimatedInputBytes = 16384,
                },
            });
        await using (stream)
        {
            (await ReadSpillIdsAsync(stream)).Should().HaveCount(64);
        }
    }

    private static async Task<List<long>> ReadSpillIdsAsync(Stream stream)
    {
        using var reader = ArrowParquetTestReader.OpenLeavingStreamOpen(stream);
        using var batches = reader.GetRecordBatchReader();
        var ids = new List<long>();
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var column = (Int64Array)batch.Column("objectid");
                for (var index = 0; index < batch.Length; index++)
                {
                    ids.Add(column.GetValue(index)!.Value);
                }
            }
        }
        return ids;
    }
}
