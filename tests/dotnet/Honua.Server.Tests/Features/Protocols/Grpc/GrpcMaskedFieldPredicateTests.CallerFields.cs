// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// Sort keys and filters name only fields the caller may see: declared, not hidden, not masked.
public sealed partial class GrpcMaskedFieldPredicateTests
{
    private const int HiddenFieldLayerId = 1;
    private const string HiddenField = "internal_code";

    [UnitTheory]
    [InlineData("secret DESC", QueryShape.Features)]
    [InlineData("name, ssn", QueryShape.Features)]
    [InlineData("internal_code", QueryShape.Features)]
    [InlineData("secret DESC", QueryShape.Stream)]
    [InlineData("name, ssn", QueryShape.Stream)]
    [InlineData("internal_code", QueryShape.Stream)]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_OrderByFieldCallerCannotSee_IsRefusedBeforeAnyRead(string orderBy, QueryShape shape)
    {
        UseHiddenFieldLayer();
        var request = new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = HiddenFieldLayerId, OrderBy = orderBy };

        var act = () => RunQueryAsync(request, shape);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        exception.Which.Status.Detail.Should().Contain("orderByFields");
        AssertNoRead();
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WhereOnLayerWithHiddenField_TranslatesWithoutTheHiddenField()
    {
        UseHiddenFieldLayer();
        MetadataV2Object? translatedAgainst = null;
        _sqlFilterTranslator
            .Translate(Arg.Any<FilterExpression>(), Arg.Do<MetadataV2Object>(resource => translatedAgainst = resource))
            .Returns(new SqlFragment("TRUE", []));
        var request = new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = HiddenFieldLayerId, Where = "name = 'a'" };

        await RunQueryAsync(request, QueryShape.Features);

        translatedAgainst.Should().NotBeNull();
        translatedAgainst!.SchemaFields.Select(field => field.Name).Should().Contain("name").And.NotContain(HiddenField);
    }

    private async Task RunQueryAsync(Proto.QueryFeaturesRequest request, QueryShape shape)
    {
        using var context = CreateCallContext();
        if (shape is QueryShape.Stream)
        {
            await _sut.QueryFeaturesStream(request, new DiscardingStreamWriter(), context);
            return;
        }

        await _sut.QueryFeatures(request, context);
    }

    private void UseHiddenFieldLayer()
    {
        var resource = _resource with
        {
            SchemaFields =
            [
                .. _resource.SchemaFields,
                new MetadataV2Field { Name = HiddenField, Type = MetadataV2FieldType.String, Nullable = true, Hidden = true }
            ]
        };
        var triple = CreateTriple();
        _resourceValidator
            .ValidateServiceLayerV2Async("test", HiddenFieldLayerId, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(new MetadataV2ServiceLayerTriple(triple.Service, triple.Publication, resource)
            {
                StorageLayerId = 0
            }));
    }
}
