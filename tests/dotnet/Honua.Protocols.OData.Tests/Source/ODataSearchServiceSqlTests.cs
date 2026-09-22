// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Models;
using Microsoft.Extensions.Options;
using NSubstitute;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Protocols.OData.Services;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed class ODataSearchServiceSqlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessExpandAsync_GroupsRequestedParentsAndKeepsStampInternal(bool stamped)
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var parent = new MetadataV2Resource
        {
            Metadata = new() { Id = "parents" },
            Status = active,
            Relationships = [new() { Id = "rel", Name = "Children", RelatedResourceId = "children", OriginField = "join_id", DestinationField = "join_id" }]
        };
        var child = new MetadataV2Resource { Metadata = new() { Id = "children" }, Status = active, StorageBindingIds = ["binding"] };
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Resources = [parent, child],
            Services = [new() { Metadata = new() { Id = "service" }, Status = active, Protocols = ["OData"] }],
            Publications = [new() { Metadata = new() { Id = "pub" }, Status = active, ResourceId = "children", ServiceId = "service", LayerIndex = 14, StorageBindingId = "binding" }],
            StorageBindings = [new() { Metadata = new() { Id = "binding" }, Status = active, ResourceId = "children", StorageLayerId = 14 }]
        }, "test", DateTimeOffset.UtcNow));
        var attributes = new Dictionary<string, object?> { ["join_id"] = stamped ? 2055L : 901L, ["name"] = "child" }.ToImmutableDictionary();
        if (stamped)
        {
            attributes = attributes.SetItem(RelatedQuery.OriginObjectIdsAttribute, new long[] { 901, 902, 999 });
        }
        var relationships = Substitute.For<IRelationshipStore>();
        relationships.QueryRelatedAsync(13, Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(81, null, attributes)]));
        var service = new ODataSearchService(new ODataSearchDependencies(
            Substitute.For<IFeatureReader>(), relationships, Substitute.For<IStreamingFeatureStore>(),
            Substitute.For<IGeometryService>(), Substitute.For<ICrsRegistry>(), graph, null!,
            Options.Create(new ODataOptions())), null!);

        var result = await service.ProcessExpandAsync("Children", parent, 13, [901, 902, 903], CancellationToken.None);

        result.Keys.Should().BeEquivalentTo(new long[] { 901, 902, 903 });
        var payload = result[901]["Children"].Should().ContainSingle().Subject.Should().BeOfType<Dictionary<string, object?>>().Subject;
        payload["ObjectId"].Should().Be(81L);
        payload.Should().NotContainKey(RelatedQuery.OriginObjectIdsAttribute);
        result[902]["Children"].Should().HaveCount(stamped ? 1 : 0);
        result[903]["Children"].Should().BeEmpty();
    }

    [Fact]
    public void BuildTextSearchCondition_EscapesFieldNamesWithQuotes()
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "O'Reilly", Type = MetadataV2FieldType.String }
        };

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-test", Name = "Test" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = fields
        };

        var terms = new List<List<(string term, bool isNegated, bool isPhrase)>>
        {
            new() { ("Seattle", false, false) }
        };

        var method = typeof(ODataSearchService).GetMethod(
            "BuildTextSearchCondition",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var fragment = (SqlFragment)method!.Invoke(null, new object[] { terms, resource })!;

        fragment.Sql.Should().Contain("attributes->>'O''Reilly'");
        fragment.Sql.Should().NotContain("attributes->>'O'Reilly'");
    }
}
