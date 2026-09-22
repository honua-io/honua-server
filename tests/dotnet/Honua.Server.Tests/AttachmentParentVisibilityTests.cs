// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using NSubstitute;

namespace Honua.Server.Tests;

public sealed class AttachmentParentVisibilityTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(null)]
    [InlineData("name = 'Visible'")]
    public async Task Visibility_UsesPortableFiltersAndBoundsEveryProviderRequest(string? definitionExpression)
    {
        var reader = Substitute.For<IFeatureReader>();
        var queries = new List<FeatureQuery>();
        reader.QueryObjectIdsAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<FeatureQuery>(1);
                // Simulate the real providers' rejection of pretranslated SQL and
                // oversized IN lists rather than accepting arbitrary mock inputs.
                var requestedIds = query.ObjectIds ?? throw new InvalidOperationException("Parent IDs are required.");
                if (query.SqlFilter is not null || requestedIds.Length > 1000)
                {
                    throw new NotSupportedException("Unsupported provider query.");
                }
                queries.Add(query);
                return Task.FromResult(requestedIds.Where(id => id % 2 == 0).ToImmutableArray());
            });
        var ids = Enumerable.Range(1, 5000).Select(id => (long)id).ToArray();

        var visible = await AttachmentEndpoints.FilterVisibleAttachmentParentsAsync(
            reader, 7, ids, definitionExpression, CancellationToken.None);

        visible.Should().Equal(ids.Where(id => id % 2 == 0));
        queries.Should().HaveCountGreaterThan(1);
        queries.Should().OnlyContain(query => query.Where == definitionExpression && query.ExcludeAttributes);
        queries.SelectMany(query => query.ObjectIds ?? ImmutableArray<long>.Empty).Should().Equal(ids);
    }
}
