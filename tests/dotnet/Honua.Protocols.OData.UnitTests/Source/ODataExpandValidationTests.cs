// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Models;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataExpandValidationTests
{
    private static readonly MetadataV2Status Active = new() { Lifecycle = MetadataV2LifecycleStatus.Active };

    private static readonly MetadataV2Object Parent = new()
    {
        Metadata = new() { Id = "parents" },
        Status = Active,
        Relationships = [new() { Id = "rel", Name = "Child Rows", RelatedResourceId = "children", OriginField = "join_id", DestinationField = "join_id" }]
    };

    private static readonly MetadataV2Object Sibling = new()
    {
        Metadata = new() { Id = "siblings" },
        Status = Active,
        Relationships = [new() { Id = "other", Name = "Inspections", RelatedResourceId = "children", OriginField = "id", DestinationField = "id" }]
    };

    [Theory]
    [InlineData("NoSuchRelation", "*'NoSuchRelation' is not a navigation property*")]
    [InlineData("Layer($expand=Features)", "*nested $expand is not supported*")]
    [InlineData("ChildRows($expand=ChildRows)", "*nested $expand is not supported*")]
    public async Task ProcessExpandAsync_UnknownOrNestedExpansion_IsRejectedEvenOnAnEmptyPage(string expand, string message)
    {
        var act = () => CreateService().ProcessExpandAsync(expand, Parent, 13, [], CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<ValidationException>()).WithMessage(message).Which;
        ExceptionMapper.Map(exception).Detail.Should().Be(exception.Message);
    }

    [Theory]
    [InlineData("Child Rows")]
    [InlineData("ChildRows($select=name)")]
    [InlineData("ChildRows_rrel($select=name)")]
    [InlineData("*")]
    public async Task FindInvalidExpandAsync_DeclaredRawSanitisedOrWildcard_IsAccepted(string expand)
        => (await CreateService().FindInvalidExpandAsync(expand, Parent, CancellationToken.None)).Should().BeNull();

    [Fact]
    public async Task ProcessExpandAsync_NavigationPropertyOfAnotherLayer_IsAnEmptyCollection()
    {
        var result = await CreateService().ProcessExpandAsync("Inspections", Parent, 13, [7], CancellationToken.None);

        result[7]["Inspections"].Should().BeEmpty();
    }

    [Fact]
    public async Task BuildFeatureQueryAsync_InvalidExpandOrSrid_FailsBeforeTheQueryIsBuilt()
    {
        var adapter = Substitute.For<IQueryParameterAdapter<ODataQueryParameters>>();
        var crs = Substitute.For<ICrsRegistry>();
        crs.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        var query = new ODataQueryService(new FilterExpressionService(Substitute.For<IFilterExpressionTranslator>()),
            adapter, Substitute.For<IQueryProcessor>());
        var facade = new ODataQuerySearchService(query, CreateService(query, crs), crs);

        var (_, expandError) = await facade.BuildFeatureQueryAsync(null, null, 10, 0, Parent, expand: "NoSuchRelation");
        var (_, sridError) = await facade.BuildFeatureQueryAsync(
            "geo.intersects(Geometry,geography'SRID=999999;POINT(0 0)')", null, 10, 0, Parent);

        expandError.Should().Contain("not a navigation property");
        sridError.Should().Contain("999999");
        await adapter.DidNotReceiveWithAnyArgs().ConvertAsync(default!, default!, default);
    }

    private static ODataSearchService CreateService(ODataQueryService? query = null, ICrsRegistry? crs = null)
    {
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Resources = [Parent, Sibling] }, "test", DateTimeOffset.UtcNow));
        return new ODataSearchService(new ODataSearchDependencies(
            Substitute.For<IFeatureReader>(), Substitute.For<IRelationshipStore>(), Substitute.For<IStreamingFeatureStore>(),
            Substitute.For<IGeometryService>(), crs ?? Substitute.For<ICrsRegistry>(), graph, null!,
            Options.Create(new ODataOptions())), query!);
    }
}
