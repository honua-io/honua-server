// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Honua.TestKit.Attributes;
using Honua.Tests.ParquetUnitSupport;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class GeoParquetQueryFormatterTests
{
    [UnitTest]
    public async Task FormatAsGeoParquet_ArtifactLayout_KeepsNeighboursTogether()
    {
        var features = InterleavedCoastPoints();
        var resource = CreateResource();
        var result = QueryResult<Feature>.Create(features.Count, features.ToImmutableArray());
        var (before, after) = FormatScatteredAndArtifact(result, resource);
        using var beforeReader = ArrowParquetTestReader.Open(before);
        using var afterReader = ArrowParquetTestReader.Open(after);
        AssertArtifactGroups(beforeReader, afterReader);
        var beforeExtents = await ReadGroupExtentsAsync(beforeReader);
        var afterExtents = await ReadGroupExtentsAsync(afterReader);
        AssertNeighbourExtents(beforeExtents, afterExtents);
        ReportParcelLookup(beforeReader, afterReader, new LookupProbe
        {
            BeforeExtents = beforeExtents,
            AfterExtents = afterExtents,
            BeforeLength = before.Length,
            AfterLength = after.Length,
        });
        using var group = afterReader.ParquetReader.RowGroup(0);
        using var chunk = group.MetaData.GetColumnChunkMetaData(0);
        chunk.Compression.Should().Be(ParquetSharp.Compression.Zstd);
    }

    [UnitTest]
    public void FormatAsGeoParquet_ArtifactBudget_IgnoresTheResponseCap()
    {
        var feature = Feature.Create(1, FormatterTestResources.CreatePointWkb(1, 2), ImmutableDictionary<string, object?>.Empty);
        var resource = CreateResource();
        var rejected = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), resource, true, 4326, false, false, new GeometryLimits(),
            encodingLimits: new GeoParquetLimits { MaxArtifactBytes = 32 });
        rejected.Should().Throw<GeoParquetLimitExceededException>();

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), resource, true, 4326, false, false, new GeometryLimits(),
            encodingLimits: new GeoParquetLimits { MaxResponseBytes = 32, MaxArtifactBytes = 8 * 1024 * 1024 });
        payload.Length.Should().BeGreaterThan(32);
    }

    [UnitTest]
    public void FormatAsGeoParquet_RowGroupAboveSpecLimit_IsRejected()
    {
        var feature = Feature.Create(1, FormatterTestResources.CreatePointWkb(1, 2), ImmutableDictionary<string, object?>.Empty);
        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), CreateResource(), true, 4326, false, false, new GeometryLimits(),
            encodingLimits: new GeoParquetLimits { RowsPerRowGroup = GeoParquetLimits.SpecMaxRowsPerRowGroup + 1 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void FormatAsGeoParquet_ForArtifact_RecordsHilbertZstdAndDefaultRowGroup()
    {
        var feature = Feature.Create(1, FormatterTestResources.CreatePointWkb(1, 2), ImmutableDictionary<string, object?>.Empty);
        var limits = GeoParquetLimits.ForArtifact(8 * 1024 * 1024);
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), CreateResource(), true, 4326, false, false, new GeometryLimits(),
            encodingLimits: limits);
        using var reader = ArrowParquetTestReader.Open(payload);
        reader.NumRowGroups.Should().Be(1);
        reader.Schema.Metadata[GeoParquetFeatureWriter.RowOrderMetadataKey]
            .Should().Be(GeoParquetFeatureWriter.HilbertBboxCenterOrder);
        reader.Schema.Metadata[GeoParquetFeatureWriter.RowsPerRowGroupMetadataKey]
            .Should().Be(GeoParquetLimits.DefaultArtifactRowsPerRowGroup.ToString(CultureInfo.InvariantCulture));
        using var group = reader.ParquetReader.RowGroup(0);
        using var chunk = group.MetaData.GetColumnChunkMetaData(0);
        chunk.Compression.Should().Be(ParquetSharp.Compression.Zstd);
    }

    [UnitTest]
    public async Task FormatAsGeoParquet_ForQuery_KeepsSourceOrderAndDropsArtifactFlags()
    {
        var configured = new GeoParquetLimits
        {
            MaxRowsPerBatch = 4,
            MaxArtifactBytes = 8 * 1024 * 1024,
            RowsPerRowGroup = 16,
            OrderByBboxHilbert = true,
            CompressWithZstd = true,
        };
        var queryLimits = configured.ForQuery();
        queryLimits.OrderByBboxHilbert.Should().BeFalse();
        queryLimits.CompressWithZstd.Should().BeFalse();
        queryLimits.RowsPerRowGroup.Should().BeNull();
        queryLimits.MaxArtifactBytes.Should().BeNull();

        // East is first in source order and last on the Hilbert curve, so a sort would fail this.
        var features = ImmutableArray.Create(
            Feature.Create(1, FormatterTestResources.CreatePointWkb(10, 50), ImmutableDictionary<string, object?>.Empty),
            Feature.Create(2, FormatterTestResources.CreatePointWkb(-120, 35), ImmutableDictionary<string, object?>.Empty));
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(2, features), CreateResource(), true, 4326, false, false, new GeometryLimits(),
            encodingLimits: queryLimits);
        using var reader = ArrowParquetTestReader.Open(payload);
        reader.Schema.Metadata.ContainsKey(GeoParquetFeatureWriter.RowOrderMetadataKey).Should().BeFalse();
        using var batches = reader.GetRecordBatchReader();
        using var batch = await batches.ReadNextRecordBatchAsync();
        var point = (Point)new WKBReader().Read(((BinaryArray)batch!.Column("geometry")).GetBytes(0).ToArray());
        point.X.Should().Be(10);
        using var group = reader.ParquetReader.RowGroup(0);
        using var chunk = group.MetaData.GetColumnChunkMetaData(0);
        chunk.Compression.Should().NotBe(ParquetSharp.Compression.Zstd);
    }

    [UnitTest]
    public async Task FormatAsGeoParquet_HilbertOrder_SortsMissingGeometryLast()
    {
        var features = ImmutableArray.Create(
            Feature.Create(1, FormatterTestResources.CreatePointWkb(10, 50), ImmutableDictionary<string, object?>.Empty),
            Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty),
            Feature.Create(3, FormatterTestResources.CreatePointWkb(-120, 35), ImmutableDictionary<string, object?>.Empty));
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(3, features), CreateResource(), true, 4326, false, false, new GeometryLimits(),
            encodingLimits: GeoParquetLimits.ForArtifact(8 * 1024 * 1024));
        using var reader = ArrowParquetTestReader.Open(payload);
        using var batches = reader.GetRecordBatchReader();
        var ids = new List<long>();
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var objectIds = (Int64Array)batch.Column("objectid");
                for (var row = 0; row < batch.Length; row++)
                {
                    ids.Add(objectIds.GetValue(row)!.Value);
                }
            }
        }

        ids.Should().HaveCount(3);
        ids[^1].Should().Be(2);
    }

    [UnitTest]
    public void FormatAsGeoParquet_OverBudgetPage_IsRefusedBeforeHilbertDecode()
    {
        var junk = new byte[4_000];
        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(2, [
                Feature.Create(1, junk, ImmutableDictionary<string, object?>.Empty),
                Feature.Create(2, junk, ImmutableDictionary<string, object?>.Empty),
            ]),
            CreateResource(), true, 4326, false, false, new GeometryLimits(),
            encodingLimits: new GeoParquetLimits
            {
                MaxEstimatedInputBytes = 128,
                OrderByBboxHilbert = true,
            });
        act.Should().Throw<GeoParquetLimitExceededException>();
    }

    private static List<Feature> InterleavedCoastPoints()
    {
        const int perCluster = 16;
        var features = new List<Feature>(perCluster * 2);
        for (var index = 0; index < perCluster; index++)
        {
            // Consecutive points alternate across the same two anchors, so every 4-row
            // source batch includes x=-120 and a lookup of that point reads every group.
            var westX = -120 + (index % 2) * 0.0001;
            var eastX = 10 + (index % 2) * 0.0001;
            features.Add(Feature.Create(index + 1, FormatterTestResources.CreatePointWkb(westX, 35),
                ImmutableDictionary<string, object?>.Empty));
            features.Add(Feature.Create(1_000 + index, FormatterTestResources.CreatePointWkb(eastX, 50),
                ImmutableDictionary<string, object?>.Empty));
        }

        return features;
    }

    private static (byte[] Before, byte[] After) FormatScatteredAndArtifact(
        QueryResult<Feature> result,
        MetadataV2Object resource)
    {
        var scattered = new GeoParquetLimits { MaxRowsPerBatch = 4 };
        var artifact = new GeoParquetLimits
        {
            MaxRowsPerBatch = 4,
            RowsPerRowGroup = 16,
            OrderByBboxHilbert = true,
            CompressWithZstd = true,
            MaxArtifactBytes = 8 * 1024 * 1024,
            MaxResponseBytes = 64,
        };
        var (before, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            result, resource, true, 4326, false, false, new GeometryLimits(), encodingLimits: scattered);
        var (after, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            result, resource, true, 4326, false, false, new GeometryLimits(), encodingLimits: artifact);
        return (before, after);
    }

    private static void AssertArtifactGroups(ParquetSharp.Arrow.FileReader beforeReader, ParquetSharp.Arrow.FileReader afterReader)
    {
        beforeReader.NumRowGroups.Should().Be(8);
        using (var beforeGroup = beforeReader.ParquetReader.RowGroup(0))
        using (var beforeChunk = beforeGroup.MetaData.GetColumnChunkMetaData(0))
        {
            beforeChunk.Compression.Should().NotBe(ParquetSharp.Compression.Zstd);
        }

        afterReader.NumRowGroups.Should().Be(2, "row groups follow RowsPerRowGroup, not the 4-row response batch");
        beforeReader.Schema.Metadata.ContainsKey(GeoParquetFeatureWriter.RowOrderMetadataKey).Should().BeFalse();
        afterReader.Schema.Metadata[GeoParquetFeatureWriter.RowOrderMetadataKey]
            .Should().Be(GeoParquetFeatureWriter.HilbertBboxCenterOrder);
        afterReader.Schema.Metadata[GeoParquetFeatureWriter.RowsPerRowGroupMetadataKey].Should().Be("16");
    }

    private static void AssertNeighbourExtents(
        List<(double MinX, double MaxX)> beforeExtents,
        List<(double MinX, double MaxX)> afterExtents)
    {
        beforeExtents.Should().OnlyContain(extent => extent.MinX < -100 && extent.MaxX > 0);
        afterExtents.Should().Contain(extent => extent.MaxX < -100);
        afterExtents.Should().Contain(extent => extent.MinX > 0);
        afterExtents.Should().OnlyContain(extent => extent.MaxX - extent.MinX < 1);
    }

    private sealed class LookupProbe
    {
        public required List<(double MinX, double MaxX)> BeforeExtents { get; init; }
        public required List<(double MinX, double MaxX)> AfterExtents { get; init; }
        public required int BeforeLength { get; init; }
        public required int AfterLength { get; init; }
    }

    private void ReportParcelLookup(
        ParquetSharp.Arrow.FileReader beforeReader,
        ParquetSharp.Arrow.FileReader afterReader,
        LookupProbe probe)
    {
        const double probeX = -120;
        var beforeWatch = Stopwatch.StartNew();
        var beforeMatch = MatchingGroupBytes(beforeReader, probe.BeforeExtents, probeX);
        beforeWatch.Stop();
        var afterWatch = Stopwatch.StartNew();
        var afterMatch = MatchingGroupBytes(afterReader, probe.AfterExtents, probeX);
        afterWatch.Stop();

        beforeMatch.Groups.Should().Be(beforeReader.NumRowGroups);
        afterMatch.Groups.Should().Be(1);
        afterMatch.Bytes.Should().BeLessThan(MatchingGroupBytes(afterReader, probe.AfterExtents, double.NaN).Bytes);
        output.WriteLine(
            "GeoParquet parcel lookup: beforeGroups={0} beforeBytes={1} beforeMs={2:F3}; afterGroups={3} afterBytes={4} afterMs={5:F3}; fileBefore={6} fileAfter={7}",
            beforeMatch.Groups, beforeMatch.Bytes, beforeWatch.Elapsed.TotalMilliseconds,
            afterMatch.Groups, afterMatch.Bytes, afterWatch.Elapsed.TotalMilliseconds,
            probe.BeforeLength, probe.AfterLength);
    }

    private static async Task<List<(double MinX, double MaxX)>> ReadGroupExtentsAsync(ParquetSharp.Arrow.FileReader reader)
    {
        var ordinates = ReadOrdinatesAsync(reader);
        var values = await ordinates;
        return GroupExtents(reader, values);
    }

    private static async Task<List<double>> ReadOrdinatesAsync(ParquetSharp.Arrow.FileReader reader)
    {
        var ordinates = new List<double>();
        using var batches = reader.GetRecordBatchReader();
        while (await batches.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var geometries = (BinaryArray)batch.Column("geometry");
                for (var row = 0; row < batch.Length; row++)
                {
                    var point = (Point)new WKBReader().Read(geometries.GetBytes(row).ToArray());
                    ordinates.Add(point.X);
                }
            }
        }

        return ordinates;
    }

    private static List<(double MinX, double MaxX)> GroupExtents(ParquetSharp.Arrow.FileReader reader, List<double> ordinates)
    {
        var extents = new List<(double MinX, double MaxX)>(reader.NumRowGroups);
        var offset = 0;
        for (var index = 0; index < reader.NumRowGroups; index++)
        {
            using var group = reader.ParquetReader.RowGroup(index);
            var count = (int)group.MetaData.NumRows;
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            for (var row = 0; row < count; row++)
            {
                var value = ordinates[offset + row];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            extents.Add((min, max));
            offset += count;
        }

        return extents;
    }

    private static (int Groups, long Bytes) MatchingGroupBytes(
        ParquetSharp.Arrow.FileReader reader,
        List<(double MinX, double MaxX)> extents,
        double probeX)
    {
        var groups = 0;
        long bytes = 0;
        for (var index = 0; index < extents.Count; index++)
        {
            var extent = extents[index];
            var matches = double.IsNaN(probeX) || (probeX >= extent.MinX && probeX <= extent.MaxX);
            if (!matches)
            {
                continue;
            }

            groups++;
            using var group = reader.ParquetReader.RowGroup(index);
            for (var column = 0; column < group.MetaData.NumColumns; column++)
            {
                using var chunk = group.MetaData.GetColumnChunkMetaData(column);
                bytes += chunk.TotalCompressedSize;
            }
        }

        return (groups, bytes);
    }
}
