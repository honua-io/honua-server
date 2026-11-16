// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class GrpcMaskedFieldPredicateTests
{
    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task QueryRelatedRecords_OnePositionPages_PreservesEmptyOriginsAndSeeksWithinGroup()
    {
        var captured = ConfigureRelatedPages();
        var store = (IRelationshipStore)_featureReader;
        store.QueryRelatedPageAsync(0, Arg.Any<RelatedPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RelatedFeaturePage([new(1, Feature.Create(41, null,
                ImmutableDictionary<string, object?>.Empty.Add("name", "parent").Add("secret", "hidden")))]) { HasMore = true },
                new RelatedFeaturePage([new(1, Feature.Create(42, null))]) { HasMore = true },
                new RelatedFeaturePage([new(2, null)]));
        var request = RelatedRequest();
        request.OutFields.Add("name");
        using var context = CreateRelatedCallContext();

        var first = await _sut.QueryRelatedRecords(request, context);
        request.PageToken = first.NextPageToken;
        var second = await _sut.QueryRelatedRecords(request, context);
        request.PageToken = second.NextPageToken;
        var last = await _sut.QueryRelatedRecords(request, context);

        first.RelatedLayerId.Should().Be(1);
        first.Groups.Single().Features.Single().Id.Should().Be(41);
        first.Groups.Single().Features.Single().Attributes.Keys.Should().Equal("name");
        first.Fields.Select(field => field.Name).Should().Equal("name");
        captured.Should().HaveCount(3);
        captured[0].After.Should().BeNull();
        captured[1].After.Should().Be(new RelatedPageCursor(1, 41));
        captured[2].After.Should().Be(new RelatedPageCursor(1, 42));
        captured.Should().OnlyContain(page => page.PositionLimit == 1 && page.Relationship.RelatedLayerId == 1 &&
            page.Relationship.OriginForeignKeyField == "parent_id" && page.Relationship.DestinationForeignKeyField == "objectid");
        last.Groups.Single().ObjectId.Should().Be(2);
        last.Groups.Single().Features.Should().BeEmpty();
        last.NextPageToken.Should().BeEmpty();
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData("secret = 'hidden'", false)]
    [InlineData("name = 'visible'", true)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task QueryRelatedRecords_UsesSharedMaskAndFilterPipeline(string predicate, bool permitted)
    {
        var captured = ConfigureRelatedPages();
        var request = RelatedRequest();
        request.Where = predicate;
        using var context = CreateRelatedCallContext();
        if (permitted)
        {
            await _sut.QueryRelatedRecords(request, context);
            captured.Single().Relationship.SqlFilter.Should().NotBeNull();
        }
        else
        {
            var action = () => _sut.QueryRelatedRecords(request, context);
            (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
            captured.Should().BeEmpty();
        }
        AssertNoRead();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task QueryRelatedRecords_ForeignContinuation_IsRejectedBeforeProviderRead(bool changeCaller)
    {
        var captured = ConfigureRelatedPages();
        ((IRelationshipStore)_featureReader).QueryRelatedPageAsync(0, Arg.Any<RelatedPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RelatedFeaturePage([new(1, Feature.Create(41, null))]) { HasMore = true });
        var request = RelatedRequest();
        using var context = CreateRelatedCallContext();
        request.PageToken = (await _sut.QueryRelatedRecords(request, context)).NextPageToken;
        if (changeCaller)
        {
            context.GetHttpContext().User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "another-reader"), new Claim(ClaimTypes.Role, "admin")], "Test"));
        }
        else
        {
            request.Where = "name = 'changed'";
        }

        var action = () => _sut.QueryRelatedRecords(request, context);

        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        captured.Should().ContainSingle();
    }

    [UnitTheory]
    [InlineData("projection", StatusCode.InvalidArgument)]
    [InlineData("relationship", StatusCode.NotFound)]
    [InlineData("provider", StatusCode.FailedPrecondition)]
    [InlineData("origins", StatusCode.InvalidArgument)]
    [InlineData("page", StatusCode.InvalidArgument)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryRelatedRecords")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.FeatureService/QueryRelatedRecords")]
    public async Task QueryRelatedRecords_UnavailableInput_RefusesWithoutFallback(string scenario, StatusCode expected)
    {
        var captured = ConfigureRelatedPages();
        var request = RelatedRequest();
        switch (scenario)
        {
            case "projection": request.OutFields.Add("secret"); break;
            case "relationship": request.RelationshipId = 999; break;
            case "provider": ((IRelationshipStore)_featureReader).SupportsRelatedPages.Returns(false); break;
            case "origins": request.ObjectIds.Add(1); break;
            case "page": request.PageSize = 1001; break;
        }
        using var context = CreateRelatedCallContext();

        var action = () => _sut.QueryRelatedRecords(request, context);

        (await action.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(expected);
        captured.Should().BeEmpty();
        AssertNoRead();
    }

    private List<RelatedPageQuery> ConfigureRelatedPages()
    {
        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(DestinationRelationshipTriple()));
        var captured = new List<RelatedPageQuery>();
        var store = (IRelationshipStore)_featureReader;
        store.SupportsRelatedPages.Returns(true);
        store.QueryRelatedPageAsync(0, Arg.Do<RelatedPageQuery>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(new RelatedFeaturePage([]));
        return captured;
    }

    private TestServerCallContext CreateRelatedCallContext() =>
        CreateCallContext(services => services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()));

    private static Proto.QueryRelatedRecordsRequest RelatedRequest()
    {
        var request = new Proto.QueryRelatedRecordsRequest
        {
            ServiceId = "test", LayerId = 0, RelationshipId = 7, PageSize = 1
        };
        request.ObjectIds.AddRange([1L, 2L]);
        return request;
    }
}
