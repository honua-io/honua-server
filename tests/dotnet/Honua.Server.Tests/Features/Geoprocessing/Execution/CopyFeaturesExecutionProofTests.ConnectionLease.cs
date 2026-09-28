// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed partial class CopyFeaturesExecutionProofTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyFeatures_WrappedConnection_ReleasesOwnerOnSuccessAndFailure(bool failCopy)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = _fixture.CurrentSchema + ",public"
        }.ConnectionString;
        var releases = 0;
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.GetConnectionString().Returns(connectionString);
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var inner = new NpgsqlConnection(connectionString);
            try
            {
                await inner.OpenAsync(call.Arg<CancellationToken>());
                return (DbConnection)new SemaphoreReleasingConnection(inner, () => releases++);
            }
            catch
            {
                await inner.DisposeAsync();
                throw;
            }
        });
        var service = new PostgresFeatureLayerCopyService(_fixture.GetService<FeatureProviderQueryRouter>(),
            provider, _fixture.GetService<IMetadataV2GraphStore>(),
            _fixture.GetService<ILayerPublishingService>(), _masks);
        var copy = () => service.CopyAsync(_sourceId, "Wrapped connection copy", new FeatureQuery(),
            Guid.NewGuid().ToString("N"), failCopy ? 1 : 1_000_000, CancellationToken.None);

        if (failCopy)
        {
            await copy.Should().ThrowAsync<InvalidOperationException>().WithMessage("*byte limit*");
        }
        else
        {
            var result = await copy();
            result.FeatureCount.Should().Be(3);
            await AssertRows(result.LayerId, [11, 13, 15]);
        }

        releases.Should().Be(1, "the connection owner must release its pool pin and admission slot exactly once");
        await AssertRows(_sourceId, [11, 13, 15]);
    }
}
