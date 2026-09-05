// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Stac.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Trait("Tier", "Fast")]
public sealed class StacPageReaderTests
{
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 100L)]
    public async Task OptionalOrAlreadyKnownCount_UsesCountFreeProviderPage(bool omitCount, long? knownCount)
    {
        var reader = Substitute.For<IFeatureReader, IPagedFeatureReader>();
        var query = new FeatureQuery { Limit = 1, Offset = 10 };
        var features = ImmutableArray.Create(Feature.Create(11, null));
        ((IPagedFeatureReader)reader).QueryPageAsync(1, query, CancellationToken.None)
            .Returns(PagedQueryResult<Feature>.Create(features, true));

        var page = await StacPageReader.ReadAsync(reader, 1, query, omitCount, CancellationToken.None, knownCount);

        page.Items.Should().Equal(features);
        page.HasMoreResults.Should().BeTrue();
        page.TotalCount.Should().Be(omitCount ? null : knownCount);
        await reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
        await reader.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default);
    }
}
