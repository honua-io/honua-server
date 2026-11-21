// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed partial class FeatureServerQueryExecutorTests
{
    [Fact]
    public async Task StreamPbfQueryAsync_UsesLimitPlusOneProbeAndMatchesMaterializedPbf()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var streamingStore = Substitute.For<IStreamingFeatureStore>();
        FeatureQuery? capturedQuery = null;
        var items = CreatePbfStreamingFeatures();
        streamingStore.StreamFeaturesAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedQuery = callInfo.ArgAt<FeatureQuery>(1);
                return ToAsyncPbfFeatures(items);
            });

        var metadata = CreatePbfStreamingMetadata();
        var context = new DefaultHttpContext();
        using var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        await CreateSut(featureReader, streamingStore).StreamPbfQueryAsync(
            BuildPbfStreamRequest(metadata, context),
            CancellationToken.None);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Value.Limit.Should().Be(3);

        var expectedBytes = FormatExpectedPbf(metadata.Resource, items.Take(2).ToImmutableArray());
        context.Response.ContentType.Should().Be("application/x-protobuf");
        context.Response.ContentLength.Should().Be(expectedBytes.Length);
        responseStream.ToArray().Should().Equal(expectedBytes);
        await featureReader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    private static FeatureServerQueryExecutor.PbfStreamQueryRequest BuildPbfStreamRequest(
        (MetadataV2Service Service, MetadataV2Object Resource, MetadataV2Layer Publication) metadata,
        HttpContext context)
        => new()
        {
            Service = metadata.Service,
            Resource = metadata.Resource,
            Publication = metadata.Publication,
            StorageLayerId = 7,
            Query = new FeatureQuery { Limit = 2 },
            QueryParams = new QueryParameters { ReturnGeometry = false, OutFields = "name" },
            Context = context
        };

    private static ImmutableArray<Feature> CreatePbfStreamingFeatures()
        =>
        [
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "alpha")),
            Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty.Add("name", "beta")),
            Feature.Create(3, null, ImmutableDictionary<string, object?>.Empty.Add("name", "gamma"))
        ];

    private static (MetadataV2Service Service, MetadataV2Object Resource, MetadataV2Layer Publication)
        CreatePbfStreamingMetadata()
    {
        var service = CreateService();
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "parcels", Name = "Parcels" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 255 }
            ]
        };
        return (service, resource, CreatePublication(service, resource));
    }

    private static byte[] FormatExpectedPbf(MetadataV2Object resource, ImmutableArray<Feature> page)
    {
        var pbfFormatter = new PbfQueryFormatter(Options.Create(new LimitsOptions()));
        var expectedResult = QueryResult<Feature>.Create(page.Length, page, hasMoreResults: true);
        var (expectedBytes, _) = pbfFormatter.FormatAsPbf(
            expectedResult,
            resource,
            returnGeometry: false,
            outputSrid: null,
            returnZ: false,
            returnM: false,
            geometryPrecision: null,
            maxAllowableOffset: null,
            outFields: ["name"]);
        return expectedBytes;
    }

    private static async IAsyncEnumerable<Feature> ToAsyncPbfFeatures(
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
