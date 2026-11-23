// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    [Fact]
    public async Task QueryPageAsync_DanglingAnd_IsRejectedLikeALeadingAnd()
    {
        var reader = CreateReader();
        var valid = await reader.QueryPageAsync(1, new FeatureQuery { Limit = 10, Where = "name = 'Honolulu'" });
        valid.Items.Select(feature => feature.Id).Should().Equal(1);

        foreach (var where in new[] { "name = 'Honolulu' AND", "AND name = 'Honolulu'" })
        {
            var act = () => reader.QueryPageAsync(1, new FeatureQuery { Limit = 10, Where = where });
            await act.Should().ThrowAsync<ArgumentException>(where);
        }
    }
}
