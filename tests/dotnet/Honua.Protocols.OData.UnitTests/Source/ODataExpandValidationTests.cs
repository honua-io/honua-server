// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataExpandValidationTests
{
    private static readonly MetadataV2Object Parent = new()
    {
        Metadata = new() { Id = "parents" },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        Relationships = [new() { Id = "rel", Name = "Children", RelatedResourceId = "children", OriginField = "join_id", DestinationField = "join_id" }]
    };

    [Theory]
    [InlineData("NoSuchRelation", "not a navigation property")]
    [InlineData("Layer($expand=Features)", "Nested $expand")]
    [InlineData("Children($expand=Children)", "Nested $expand")]
    public async Task ProcessExpand_UnknownOrNestedExpansion_IsRejectedEvenOnAnEmptyPage(string expand, string message)
    {
        var act = () => CreateService().ProcessExpandAsync(expand, Parent, 13, [], CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage($"*{message}*");
    }

    [Theory]
    [InlineData("Children")]
    [InlineData("Children($select=name)")]
    public async Task ProcessExpand_DeclaredNavigationProperty_IsAccepted(string expand)
    {
        var result = await CreateService().ProcessExpandAsync(expand, Parent, 13, [], CancellationToken.None);

        result.Should().BeEmpty();
    }

    private static ODataSearchService CreateService() => new(new ODataSearchDependencies(
        Substitute.For<IFeatureReader>(), Substitute.For<IRelationshipStore>(), Substitute.For<IStreamingFeatureStore>(),
        Substitute.For<IGeometryService>(), Substitute.For<ICrsRegistry>(), Substitute.For<IMetadataV2GraphProvider>(), null!,
        Options.Create(new ODataOptions())), null!);
}
