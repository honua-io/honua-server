// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    private static readonly ImmutableDictionary<string, object?> _rowWithMaskedValue =
        ImmutableDictionary<string, object?>.Empty
            .Add("name", "visible")
            .Add(MaskedField, "withheld");

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ResponseSchema_OmitsMaskedField()
    {
        _featureReader
            .QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, ImmutableArray.Create(Feature.Create(1, null, _rowWithMaskedValue))));
        using var context = CreateCallContext();

        var response = await _sut.QueryFeatures(new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = 0 }, context);

        response.Fields.Select(field => field.Name).Should().Contain("name").And.NotContain(MaskedField);
        response.Features.Should().ContainSingle().Which.Attributes.Keys.Should().Contain("name").And.NotContain(MaskedField);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_FirstPageSchema_OmitsMaskedField()
    {
        _streamingStore
            .StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => RowsWithMaskedValue());
        var writer = new CapturingStreamWriter();
        using var context = CreateCallContext();

        await _sut.QueryFeaturesStream(new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = 0 }, writer, context);

        var page = writer.Pages.Should().ContainSingle().Subject;
        page.Fields.Select(field => field.Name).Should().Contain("name").And.NotContain(MaskedField);
        page.Features.Should().ContainSingle().Which.Attributes.Keys.Should().NotContain(MaskedField);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_NoMasksForCaller_ResponseSchemaKeepsEveryAttribute()
    {
        _fieldMaskSource
            .ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);
        using var context = CreateCallContext();

        var response = await _sut.QueryFeatures(new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = 0 }, context);

        response.Fields.Select(field => field.Name).Should().Contain(["name", MaskedField]);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WhereClauseAndSchema_ResolveCallerMasksOnce()
    {
        using var context = CreateCallContext();

        await _sut.QueryFeatures(
            new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = 0, Where = "name = 'visible'" },
            context);

        await _fieldMaskSource.Received(1).ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<Feature> RowsWithMaskedValue()
    {
        await Task.Yield();
        yield return Feature.Create(1, null, _rowWithMaskedValue);
    }

    private sealed class CapturingStreamWriter : IServerStreamWriter<Proto.FeaturePage>
    {
        public List<Proto.FeaturePage> Pages { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.FeaturePage message)
        {
            Pages.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(Proto.FeaturePage message, CancellationToken cancellationToken) => WriteAsync(message);
    }
}
