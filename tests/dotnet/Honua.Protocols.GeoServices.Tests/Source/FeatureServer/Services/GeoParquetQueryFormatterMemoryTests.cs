// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

// GC.GetTotalMemory is process-wide: run alone so parallel collections cannot inflate the delta.
[Collection(GeoParquetStreamingMemoryTestGroup.Name)]
public sealed class GeoParquetQueryFormatterMemoryTests
{
    [UnitTest]
    public async Task FormatAsGeoParquetStream_HundredThousandFeatures_UsesBoundedFileStorage()
    {
        const int count = 100_000;
        var features = Enumerable.Range(1, count).Select(id => Feature.Create(id, null,
            ImmutableDictionary<string, object?>.Empty.Add("name", $"{id:D8}-{new string('x', 256)}"))).ToImmutableArray();
        var query = QueryResult<Feature>.Create(count, features);
        var resource = GeoParquetQueryFormatterTests.CreateLayer(
            GeoParquetQueryFormatterTests.Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            GeoParquetQueryFormatterTests.Field("name", MetadataV2FieldType.String));
        var baseline = GC.GetTotalMemory(true);
        var (payload, contentType) = GeoParquetQueryFormatter.FormatAsGeoParquetStream(query, resource, new GeoParquetStreamEncodingOptions
        {
            ReturnGeometry = false,
            OutputSrid = null,
            ReturnZ = false,
            ReturnM = false,
            GeometryLimits = new GeometryLimits(),
            EncodingLimits = new GeoParquetLimits
            {
                MaxRowsPerBatch = 256,
                MaxEstimatedBatchBytes = 1024 * 1024,
                MaxEstimatedInputBytes = 512 * 1024 * 1024
            }
        });
        await using (payload)
        {
            payload.Should().BeAssignableTo<FileStream>();
            contentType.Should().Be("application/vnd.apache.parquet");
            (GC.GetTotalMemory(true) - baseline).Should().BeLessThan(8 * 1024 * 1024);
            var path = ((FileStream)payload).Name;
            using (var reader = new ParquetSharp.Arrow.FileReader(payload, leaveOpen: true))
            using (var batches = reader.GetRecordBatchReader())
            {
                long rows = 0;
                while (await batches.ReadNextRecordBatchAsync() is { } batch)
                {
                    using (batch) rows += batch.Length;
                }
                rows.Should().Be(count);
            }
            await payload.DisposeAsync();
            File.Exists(path).Should().BeFalse();
        }
        GC.KeepAlive(query);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GeoParquetStreamingMemoryTestGroup
{
    public const string Name = "GeoParquet streaming memory";
}
