// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

public sealed class ExternalPostgisDagSourceSafetyTests
{
    [UnitTest]
    public async Task ReadAsync_WhereContainsSemicolon_IsRejectedBeforeExecution()
    {
        var source = new ExternalPostgisDagSource();
        var request = new DagSourceRequest
        {
            PostgisTable = "features",
            Where = "active = true; DROP TABLE features"
        };

        var act = async () =>
        {
            await foreach (var _ in source.ReadAsync(request))
            {
            }
        };

        var exception = await act.Should().ThrowAsync<ArgumentException>();
        exception.Which.Message.Should().Contain("exactly one statement");
    }

    [UnitTheory]
    [InlineData("active = true")]
    [InlineData("status = 'deleted'")]
    public async Task ReadAsync_LegitimateWhere_IsNotRejectedByTheReadOnlyCheck(string where)
    {
        var source = new ExternalPostgisDagSource();
        var request = new DagSourceRequest
        {
            PostgisTable = "features",
            Where = where
        };

        var act = async () =>
        {
            await foreach (var _ in source.ReadAsync(request))
            {
            }
        };

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("resolved secure connection");
    }
}
